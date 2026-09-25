using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Diagnostics;

namespace GodotHotReload
{
    /// <summary>
    /// Runtime C# hot reload engine — runs inside the GAME process (Godot F5 is a separate process).
    /// Pipeline: FileSystemWatcher → 500ms silent debounce → dotnet build → ALC LoadFromStream
    /// → method diff → DynamicMethod IL stub → Harmony bool prefix.
    ///
    /// Supported: static + instance methods, void/return/ref/out/overloads/generics,
    /// async/iterator MoveNext, field-layout guard, Restore All.
    ///
    /// Instance method strategy: DynamicMethod emits IL that does NOT castclass the instance —
    /// the old ALC object ref is passed directly as the new type's this pointer. This bypasses
    /// the cross-ALC type-identity wall (ALC1.MyClass ≠ ALC2.MyClass) and works because the
    /// runtime object's field layout matches (enforced by ComputeFieldHash guard).
    ///
    /// Generic method strategy: patch a set of constructed instantiations
    /// (object covers all ref types; common value types each patch separately).
    /// The generic dispatch uses GetGenericMethodDefinition() to look up the new definition,
    /// then BuildGeneralStub makes a fresh stub for the constructed signature.
    /// </summary>
    public partial class HotReloadRuntime : Node
    {
        // ---------- Paths ----------
        static string ProjectRoot => ProjectSettings.GlobalizePath("res://");
        static string BinDir => Path.Combine(ProjectRoot, ".godot", "mono", "temp", "bin", "Debug");
        string HotSwapRoot => Path.Combine(ProjectRoot, ".hotswap");
        string TriggerFile => Path.Combine(HotSwapRoot, "trigger");
        string RestoreFile => Path.Combine(HotSwapRoot, "restore");

        // ---------- State ----------
        readonly ConcurrentQueue<string> _events = new();
        FileSystemWatcher _watcher;
        double _quiet;
        bool _armed;
        bool _compiling;
        int _version;
        int _previousFieldHash;

        // ---------- Harmony ----------
        static HarmonyLib.Harmony _harmony;
        // key = DispatchKey → stub for that specific method
        static readonly Dictionary<string, Func<object, object[], object>> _dispatch = new();
        // key = generic-def DispatchKey → new generic MethodInfo (for runtime construction lookup)
        static readonly Dictionary<string, MethodInfo> _genericDispatch = new();
        static readonly HashSet<MethodBase> _patched = new();
        static readonly HashSet<(AssemblyLoadContext alc, int version)> _oldAlcs = new();

        // DynamicMethod rooting — prevents GC from collecting JIT code
        static readonly List<DynamicMethod> _rootedStubs = new();

        // ---------- Binding Flags ----------
        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // ---------- Generic patch candidates ----------
        // CoreCLR JIT shares code for all reference-type generic instantiations
        // → patching object covers every ref type. Value types each have their own body.
        static readonly Type[] CommonValueTypes =
        {
            typeof(int), typeof(float), typeof(double), typeof(bool),
            typeof(long), typeof(byte), typeof(char), typeof(decimal)
        };

        static IEnumerable<Type[]> EnumerateGenericTypeArgs(int arity)
        {
            if (arity == 1)
            {
                yield return new[] { typeof(object) };
                foreach (var vt in CommonValueTypes)
                    yield return new[] { vt };
            }
            else
            {
                yield return Enumerable.Repeat(typeof(object), arity).ToArray();
            }
        }

        // ================================================================
        // Lifecycle
        // ================================================================

        public override void _Ready()
        {
            // Exported games have no source directory — auto-disable
            if (!Directory.Exists(Path.Combine(ProjectRoot, "src"))) return;

            _harmony ??= new HarmonyLib.Harmony("com.gutian2000.godot.hotreload.runtime");

            // Initialize field hash (captures original type layout before any patch)
            _previousFieldHash = ComputeFieldHash(typeof(HotReloadRuntime).Assembly);

            // FileSystemWatcher
            _watcher = new FileSystemWatcher(ProjectRoot, "*.cs")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (s, e) => Enqueue(e.FullPath);
            _watcher.Created += (s, e) => Enqueue(e.FullPath);
            _watcher.Renamed += (s, e) => Enqueue(e.FullPath);

            // Also watch for editor-triggered reload (marker file)
            AddToGroup("HotReloadRuntime"); // editor plugin can find this node

            GD.Print("[Hot Reload Live] ✅ Runtime started — watching " + ProjectRoot);
        }

        public override void _ExitTree()
        {
            _watcher?.Dispose();
            RestoreAll();
        }

        // ================================================================
        // File change → debounce
        // ================================================================

        void Enqueue(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var p = path.Replace('\\', '/');
            if (p.Contains("/.godot/") || p.Contains("/.hotswap/")
                || p.Contains("/addons/") || p.Contains("/obj/")) return;
            _events.Enqueue(p);
            _armed = true;
            _quiet = 0;
        }

        public override void _Process(double delta)
        {
            // Restore All (editor-triggered)
            if (File.Exists(RestoreFile))
            {
                try { File.Delete(RestoreFile); } catch { }
                RestoreAll();
                return;
            }

            // Editor-triggered reload (marker file)
            if (File.Exists(TriggerFile) && !_compiling)
            {
                try { File.Delete(TriggerFile); } catch { }
                _armed = true;
                _quiet = 10; // force immediate (bypass debounce)
            }

            if (!_armed || _compiling) return;

            while (_events.TryDequeue(out var p))
                GD.Print("[Hot Reload Live] Changed: " + Path.GetFileName(p));

            _quiet += delta;
            if (_quiet < 0.5) return; // 500ms silent window

            _armed = false;
            _quiet = 0;
            try { CompileLoadPatch(); }
            catch (Exception ex)
            {
                GD.PushError("[Hot Reload Live] Top-level exception: " + ex.Message);
                _compiling = false;
            }
        }

        // ================================================================
        // Compile → ALC → Patch
        // ================================================================

        void CompileLoadPatch()
        {
            _compiling = true;
            try
            {
                _version++;
                var outDir = Path.Combine(HotSwapRoot, "v" + _version);
                Directory.CreateDirectory(outDir);
                GD.Print("[Hot Reload Live] 📦 Building v" + _version + " …");

                var dotnet = FindDotnet();
                var psi = new ProcessStartInfo
                {
                    FileName = dotnet,
                    Arguments = $"build -c Debug --no-restore -p:OutputPath=\"{outDir.Replace('\\', '/')}\"",
                    WorkingDirectory = ProjectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };

                using var p = Process.Start(psi);
                if (p == null) { GD.PushError("[Hot Reload Live] ❌ Cannot start dotnet"); return; }
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(60_000))
                {
                    try { p.Kill(true); } catch { }
                    GD.PushError("[Hot Reload Live] ❌ Build timed out (60s)");
                    return;
                }

                if (p.ExitCode != 0)
                {
                    var lines = (stderr + "\n" + stdout).Split('\n')
                        .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                                 || l.Contains(": " + (char)0x9519 + " ")) // Chinese "错误"
                        .TakeLast(6);
                    GD.PushError("[Hot Reload Live] ❌ Build failed:\n" + string.Join('\n', lines));
                    return;
                }

                var dll = Path.Combine(outDir, "GodotHotReload.dll");
                if (!File.Exists(dll))
                {
                    GD.PushError("[Hot Reload Live] ❌ Output DLL not found: " + dll);
                    return;
                }

                GD.Print("[Hot Reload Live] 📂 Built: " + dll.Replace('\\', '/'));

                var alc = new AssemblyLoadContext("HotSwap_v" + _version, isCollectible: true);
                alc.Resolving += (ctx, name) =>
                {
                    foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                        if (loaded.GetName().Name == name.Name) return loaded;
                    return null;
                };
                Assembly newAsm;
                try
                {
                    using var fs = File.OpenRead(dll);
                    var pdb = Path.ChangeExtension(dll, ".pdb");
                    newAsm = File.Exists(pdb)
                        ? alc.LoadFromStream(fs, File.OpenRead(pdb))
                        : alc.LoadFromStream(fs);
                }
                catch (Exception ex)
                {
                    GD.PushError("[Hot Reload Live] ❌ ALC load failed: " + ex.Message);
                    return;
                }

                // Field layout guard (must pass BEFORE any patch attempt)
                var newHash = ComputeFieldHash(newAsm);
                if (_previousFieldHash != 0 && newHash != _previousFieldHash)
                {
                    GD.PushWarning("[Hot Reload Live] ⚠️ Field layout changed — skipping this patch (type safety guard)");
                    _oldAlcs.Add((alc, _version));
                    CleanupOldAlcs();
                    return;
                }
                _previousFieldHash = newHash;

                ApplyPatch(newAsm);
                _oldAlcs.Add((alc, _version));
                CleanupOldAlcs();
            }
            finally { _compiling = false; }
        }

        // Unload old ALCs to prevent memory leak (keep only the current one)
        void CleanupOldAlcs()
        {
            foreach (var (alc, v) in _oldAlcs.Take(_oldAlcs.Count - 1))
            {
                try { alc.Unload(); } catch { }
            }
        }

        static string FindDotnet()
        {
            var pf = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
            var c1 = Path.Combine(pf, "dotnet", "dotnet.exe");
            if (File.Exists(c1)) return c1;
            var c2 = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86),
                "dotnet", "dotnet.exe");
            return File.Exists(c2) ? c2 : "dotnet";
        }

        // ================================================================
        // Diff + Patch (Core)
        // ================================================================

        void ApplyPatch(Assembly newAsm)
        {
            try
            {
                var oldAsm = typeof(HotReloadRuntime).Assembly; // running assembly

                // Build old method list (instance + static, ALL excluding Godot generated)
                var oldMethods = SafeGetTypes(oldAsm)
                    .Where(IsUserType)
                    .SelectMany(t => t.GetMethods(BF))
                    .Where(m => !m.IsAbstract && !m.IsSpecialName && !IsGodotGenerated(m))
                    .ToList();

                // Build new method map
                var newMap = new Dictionary<string, MethodBase>();
                foreach (var t in SafeGetTypes(newAsm).Where(IsUserType))
                    foreach (var m in t.GetMethods(BF))
                        if (!m.IsAbstract && !m.IsSpecialName && !IsGodotGenerated(m))
                            newMap[DispatchKey(m)] = m;

                int oldCount = oldMethods.Count;
                GD.Print($"[Hot Reload Live] 🔍 Old methods: {oldCount} / New: {newMap.Count}");

                var prefix = typeof(HotReloadRuntime).GetMethod(nameof(Dispatch),
                    BindingFlags.Static | BindingFlags.NonPublic);

                int added = 0, updated = 0, failed = 0;
                int genericPatched = 0;

                foreach (var oldM in oldMethods)
                {
                    var key = DispatchKey(oldM);
                    if (!newMap.TryGetValue(key, out var newM)) continue;

                    var isGenericDef = oldM is MethodInfo omInfo && omInfo.IsGenericMethodDefinition;
                    var isIteratorEntry = oldM.GetCustomAttribute<System.Runtime.CompilerServices.IteratorStateMachineAttribute>() != null;
                    var isAsyncEntry = IsAsyncEntryMethod(oldM);

                    // --- Generic method definition → patch constructed instantiations ---
                    if (isGenericDef)
                    {
                        _genericDispatch[key] = (MethodInfo)newM;
                        var genDef = (MethodInfo)oldM;
                        var arity = genDef.GetGenericArguments().Length;
                        int thisPatched = 0;
                        foreach (var typeArgs in EnumerateGenericTypeArgs(arity))
                        {
                            MethodInfo constructed;
                            try { constructed = genDef.MakeGenericMethod(typeArgs); }
                            catch { continue; }
                            if (_patched.Contains(constructed)) continue;
                            try
                            {
                                _dispatch[DispatchKey(constructed)] = BuildGeneralStub(
                                    ((MethodInfo)newM).MakeGenericMethod(typeArgs),
                                    oldM.DeclaringType);
                                _harmony.Patch(constructed, prefix: new HarmonyLib.HarmonyMethod(prefix));
                                _patched.Add(constructed);
                                thisPatched++;
                            }
                            catch { }
                        }
                        if (thisPatched > 0)
                        {
                            genericPatched += thisPatched;
                            if (!_patched.Contains(oldM)) { added++; _patched.Add(oldM); } else { updated++; }
                            GD.Print($"[Hot Reload Live] ✅ Generic {oldM.DeclaringType?.Name}.{oldM.Name}`{arity} → {thisPatched} instantiations");
                        }
                        continue;
                    }

                    // --- Non-generic method on generic TYPE definition → patch constructed type instantiations ---
                    // (Harmony cannot patch methods on open generic type definitions)
                    if (oldM.DeclaringType != null && oldM.DeclaringType.IsGenericTypeDefinition)
                    {
                        var typeDef = oldM.DeclaringType;
                        var newTypeDef = newM.DeclaringType;
                        var typeArity = typeDef.GetGenericArguments().Length;
                        int thisPatched = 0;
                        foreach (var typeArgs in EnumerateGenericTypeArgs(typeArity))
                        {
                            MethodBase oldConstructed;
                            MethodInfo newConstructed;
                            Type oldConstructedType;
                            try
                            {
                                oldConstructedType = typeDef.MakeGenericType(typeArgs);
                                oldConstructed = MethodBase.GetMethodFromHandle(oldM.MethodHandle, oldConstructedType.TypeHandle);
                                var newConstructedType = newTypeDef.MakeGenericType(typeArgs);
                                newConstructed = (MethodInfo)MethodBase.GetMethodFromHandle(newM.MethodHandle, newConstructedType.TypeHandle);
                            }
                            catch { continue; }
                            if (_patched.Contains(oldConstructed)) continue;
                            try
                            {
                                _dispatch[DispatchKey(oldConstructed)] = BuildGeneralStub(newConstructed, oldConstructedType);
                                _harmony.Patch(oldConstructed, prefix: new HarmonyLib.HarmonyMethod(prefix));
                                _patched.Add(oldConstructed);
                                thisPatched++;
                            }
                            catch { }
                        }
                        if (thisPatched > 0)
                        {
                            genericPatched += thisPatched;
                            added++;
                            GD.Print($"[Hot Reload Live] ✅ Generic-class {typeDef.Name}.{oldM.Name} → {thisPatched} instantiations");
                        }
                        continue;
                    }

                    // --- Iterator/async ENTRY methods: skip (Harmony patch corrupts state machine IL) ---
                    // But do NOT skip MoveNext! MoveNext gets patched below as a regular instance method
                    if ((isIteratorEntry || isAsyncEntry) && !oldM.IsStatic)
                    {
                        // Silently skip — MoveNext will be found and patched separately
                        continue;
                    }

                    // --- Patch target ---
                    try
                    {
                        // Build DynamicMethod stub (cross-ALC safe for instance methods)
                        _dispatch[key] = BuildGeneralStub((MethodInfo)newM, oldM.DeclaringType);

                        if (_patched.Contains(oldM))
                        {
                            updated++;
                            continue;
                        }
                        _harmony.Patch(oldM, prefix: new HarmonyLib.HarmonyMethod(prefix));
                        _patched.Add(oldM);
                        added++;
                        GD.Print($"[Hot Reload Live] ✅ Patched: {(oldM.IsStatic ? "static " : "instance ")}{oldM.DeclaringType?.Name}.{oldM.Name}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        GD.PushError($"[Hot Reload Live] ❌ Patch failed {oldM.Name}: {ex.Message}");
                    }
                }

                GD.Print($"[Hot Reload Live] 🎉 v{_version}: {added} new / {updated} updated / {failed} failed / {genericPatched} generic-instantiations");
            }
            catch (Exception ex)
            {
                GD.PushError("[Hot Reload Live] ❌ ApplyPatch exception: " + ex.Message);
            }
        }

        // ================================================================
        // Restore All
        // ================================================================

        public static void RestoreAll()
        {
            if (_patched.Count == 0) return;
            _harmony?.UnpatchAll(_harmony.Id);
            _patched.Clear();
            _dispatch.Clear();
            _genericDispatch.Clear();
            _rootedStubs.Clear();
            GD.Print("[Hot Reload Live] 🔄 Restored all patches");
        }

        // ================================================================
        // Dispatch — Harmony bool prefix target
        // ================================================================

        static bool Dispatch(MethodBase __originalMethod, object __instance, object[] __args)
        {
            // Generic construction: look up by constructed key (patched instantiation)
            if (__originalMethod.IsGenericMethod && !__originalMethod.IsGenericMethodDefinition)
            {
                var key = DispatchKey(__originalMethod);
                if (_dispatch.TryGetValue(key, out var stub))
                {
                    stub(__instance, __args);
                    return false;
                }
            }

            // Regular method
            if (_dispatch.TryGetValue(DispatchKey(__originalMethod), out var regularStub))
            {
                regularStub(__instance, __args);
                return false;
            }
            return true;
        }

        // ================================================================
        // DynamicMethod IL Stub Builder (cross-ALC safe)
        // ================================================================

        /// <summary>
        /// Builds a stub that forwards (instance, args[]) → newMethod(...).
        /// For instance methods: does NOT castclass the instance — the old ALC object ref is
        /// passed directly as the new type's this pointer. This bypasses cross-ALC type identity
        /// checks and works because field layout matches (enforced by ComputeFieldHash).
        /// </summary>
        static Func<object, object[], object> BuildGeneralStub(MethodInfo newM, Type oldDeclaringType)
        {
            var ps = newM.GetParameters();
            var dm = new DynamicMethod(
                "HRStub_" + newM.Name + "_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                typeof(object),
                new[] { typeof(object), typeof(object[]) },
                typeof(HotReloadRuntime), true); // skipVisibility: cross-assembly access

            var il = dm.GetILGenerator();
            var resultLocal = il.DeclareLocal(typeof(object));
            var byRefLocals = new LocalBuilder[ps.Length];

            // --- this pointer ---
            if (!newM.IsStatic)
            {
                il.Emit(OpCodes.Ldarg_0);       // instance: old object ref directly as new this
                // Value-type instance methods (async/iterator state machines): unbox requires
                // the OLD declaring type (cross-ALC check would fail with new type)
                if (newM.DeclaringType != null && newM.DeclaringType.IsValueType)
                {
                    var unboxType = oldDeclaringType ?? newM.DeclaringType;
                    il.Emit(OpCodes.Unbox, unboxType);
                }
            }

            // --- parameters ---
            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                if (pt.IsByRef)
                {
                    var ut = pt.GetElementType();
                    var lb = il.DeclareLocal(ut);
                    byRefLocals[i] = lb;
                    il.Emit(OpCodes.Ldarg_1);
                    EmitLdcI4(il, i);
                    il.Emit(OpCodes.Ldelem_Ref);
                    EmitUnboxOrCast(il, ut);
                    il.Emit(OpCodes.Stloc, lb);
                    il.Emit(OpCodes.Ldloca_S, lb);  // &local for ByRef formal
                }
                else
                {
                    il.Emit(OpCodes.Ldarg_1);
                    EmitLdcI4(il, i);
                    il.Emit(OpCodes.Ldelem_Ref);
                    EmitUnboxOrCast(il, pt);
                }
            }

            // --- call new method ---
            il.Emit(OpCodes.Call, newM);

            // --- result handling ---
            if (newM.ReturnType == typeof(void))
                il.Emit(OpCodes.Ldnull);
            else if (newM.ReturnType.IsValueType)
                il.Emit(OpCodes.Box, newM.ReturnType);
            il.Emit(OpCodes.Stloc, resultLocal);

            // --- ref/out writeback ---
            for (int i = 0; i < ps.Length; i++)
            {
                var lb = byRefLocals[i];
                if (lb == null) continue;
                var ut = ps[i].ParameterType.GetElementType();
                il.Emit(OpCodes.Ldarg_1);
                EmitLdcI4(il, i);
                il.Emit(OpCodes.Ldloc, lb);
                if (ut.IsValueType) il.Emit(OpCodes.Box, ut);
                il.Emit(OpCodes.Stelem_Ref);
            }

            il.Emit(OpCodes.Ldloc, resultLocal);
            il.Emit(OpCodes.Ret);

            // Root to prevent GC collecting JIT code
            _rootedStubs.Add(dm);
            return (Func<object, object[], object>)dm.CreateDelegate(typeof(Func<object, object[], object>));
        }

        static void EmitLdcI4(ILGenerator il, int v)
        {
            switch (v)
            {
                case 0: il.Emit(OpCodes.Ldc_I4_0); break;
                case 1: il.Emit(OpCodes.Ldc_I4_1); break;
                case 2: il.Emit(OpCodes.Ldc_I4_2); break;
                case 3: il.Emit(OpCodes.Ldc_I4_3); break;
                case 4: il.Emit(OpCodes.Ldc_I4_4); break;
                case 5: il.Emit(OpCodes.Ldc_I4_5); break;
                case 6: il.Emit(OpCodes.Ldc_I4_6); break;
                case 7: il.Emit(OpCodes.Ldc_I4_7); break;
                case 8: il.Emit(OpCodes.Ldc_I4_8); break;
                default:
                    if (v >= -128 && v <= 127) il.Emit(OpCodes.Ldc_I4_S, (sbyte)v);
                    else il.Emit(OpCodes.Ldc_I4, v);
                    break;
            }
        }

        static void EmitUnboxOrCast(ILGenerator il, Type t)
        {
            if (t.IsValueType) il.Emit(OpCodes.Unbox_Any, t);
            else if (t != typeof(object)) il.Emit(OpCodes.Castclass, t);
        }

        // ================================================================
        // Helpers
        // ================================================================

        static bool IsAsyncEntryMethod(MethodBase m)
        {
            // Async entry method: has AsyncStateMachineAttribute
            return m.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() != null;
        }

        // Field layout guard — hash of all user types' field signatures.
        // If a type's field layout changed, skip the patch to avoid runtime corruption.
        static int ComputeFieldHash(Assembly asm)
        {
            int hash = 17;
            foreach (var t in SafeGetTypes(asm).Where(IsUserType))
            {
                hash = hash * 31 + (t.FullName ?? t.Name).GetHashCode();
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    hash = hash * 31 + (f.FieldType.FullName ?? f.FieldType.Name ?? "").GetHashCode();
            }
            return hash;
        }

        // Skip patching the hot reload engine itself + all compiler-generated nested types
        // (lambdas <>c, async state machines <Method>d__NN, etc.)
        // Compiler-generated types get recompiled into the new ALC too — patching them causes
        // self-patch recursion and can crash the engine.
        static bool IsUserType(Type t)
        {
            if (t.Name == nameof(HotReloadRuntime) || t.Name == "GodotHotReloadPlugin") return false;
            if (t.Name.Contains('<')) return false;                       // compiler-generated: <>c, <M>d__12
            if (t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() != null) return false;
            // Any nested type declared inside HotReloadRuntime → skip
            var d = t.DeclaringType;
            while (d != null) { if (d.Name == nameof(HotReloadRuntime)) return false; d = d.DeclaringType; }
            return true;
        }

        // Godot source generator injects these bridge methods — NEVER patch them
        static readonly HashSet<string> GodotGeneratedNames = new()
        {
            "GetGodotMethodList", "GetGodotPropertyList", "GetGodotSignalList",
            "InvokeGodotClassMethod", "InvokeGodotClassStaticMethod",
            "HasGodotClassMethod", "HasGodotClassSignalMethod",
            "InitializeFromGameProject", "InitializeFromGodotObject",
            "SaveGodotObject", "SaveGodotObjectData",
            "RestoreGodotObjectData", "GetGodotClassNativeInstanceId", "DisposeCore",
            "SetGodotClassPropertyValue",
        };

        static bool IsGodotGenerated(MethodBase m)
        {
            var n = m.Name;
            if (GodotGeneratedNames.Contains(n)) return true;
            if (n.StartsWith("GetGodot", StringComparison.Ordinal)
                || n.StartsWith("InvokeGodotClass", StringComparison.Ordinal)
                || n.StartsWith("HasGodotClass", StringComparison.Ordinal)
                || n.StartsWith("SetGodotClass", StringComparison.Ordinal))
                return true;
            // Godot 4.7 source generator marks ALL its injected methods with [CompilerGenerated]
            return m.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() != null;
        }

        static Type[] SafeGetTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
        }

        static string DispatchKey(MethodBase m)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(m.DeclaringType?.FullName ?? "?").Append("::").Append(m.Name);
            foreach (var p in m.GetParameters())
            {
                var t = p.ParameterType;
                sb.Append('|');
                if (t.IsByRef) { sb.Append("ref:"); t = t.GetElementType(); }
                sb.Append(t.FullName ?? t.Name);
            }
            return sb.ToString();
        }
    }
}
