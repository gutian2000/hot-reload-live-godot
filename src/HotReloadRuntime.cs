using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Diagnostics;

namespace GodotHotReload
{
    /// <summary>
    /// 游戏进程内 C# 热更运行时。
    /// Godot F5 运行的是独立进程，编辑器插件（EditorPlugin）的 patch 影响不到游戏，
    /// 所以 watcher / 编译 / ALC 加载 / Harmony patch 全部必须在游戏进程内执行。
    /// 架构：FileSystemWatcher → 500ms 静默去抖 → dotnet build 到 .hotswap/vN
    ///       → 字节流 ALC 加载（不锁 DLL）→ static 方法 diff → Harmony bool prefix。
    /// POC 边界：仅支持 static 方法（跨 ALC 的同名实例类型不兼容 MethodInfo.Invoke）。
    /// </summary>
    public partial class HotReloadRuntime : Node
    {
        static string ProjectRoot => ProjectSettings.GlobalizePath("res://");
        static string BinDir => Path.Combine(ProjectRoot, ".godot", "mono", "temp", "bin", "Debug");
        string HotSwapRoot => Path.Combine(ProjectRoot, ".hotswap");

        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // 后台线程只入队纯字符串（不碰 Godot API），主线程 _Process 统一处理
        readonly ConcurrentQueue<string> _events = new();
        FileSystemWatcher _watcher;
        double _quiet;
        bool _armed;
        bool _compiling;
        int _version;

        static HarmonyLib.Harmony _harmony;
        static readonly Dictionary<string, Func<object, object[], object>> _dispatch = new();
        static readonly HashSet<MethodBase> _patched = new();

        public override void _Ready()
        {
            // 导出游戏没有源码目录，自动不启动
            if (!Directory.Exists(Path.Combine(ProjectRoot, "src"))) return;

            _harmony ??= new HarmonyLib.Harmony("com.gutian2000.godot.hotreload.runtime");

            _watcher = new FileSystemWatcher(ProjectRoot, "*.cs")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            // Godot/VS 保存可能是直接写（Changed）或临时文件+重命名（Renamed/Created），全部订阅
            _watcher.Changed += (s, e) => Enqueue(e.FullPath);
            _watcher.Created += (s, e) => Enqueue(e.FullPath);
            _watcher.Renamed += (s, e) => Enqueue(e.FullPath);

            GD.Print($"[HotReloadRuntime] ✅ 游戏内热更已启动，监听：{ProjectRoot}");
        }

        public override void _ExitTree() => _watcher?.Dispose();

        // ---------- 后台线程：只做纯托管入队 ----------
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

        // ---------- 主线程：去抖 + 编译 ----------
        public override void _Process(double delta)
        {
            if (!_armed || _compiling) return;

            while (_events.TryDequeue(out var p))
                GD.Print($"[HotReloadRuntime] 文件变更：{Path.GetFileName(p)}");

            _quiet += delta;
            if (_quiet < 0.5) return; // 等 500ms 静默，避开一次保存的多波事件

            _armed = false;
            _quiet = 0;
            try { CompileLoadPatch(); }
            catch (Exception ex)
            {
                GD.PushError($"[HotReloadRuntime] ❌ 顶层异常：{ex.Message}\n{ex.StackTrace}");
                _compiling = false;
            }
        }

        // ---------- 编译（输出到独立目录，避开游戏进程对 bin 的文件锁） ----------
        void CompileLoadPatch()
        {
            _compiling = true;
            try
            {
                _version++;
                var outDir = Path.Combine(HotSwapRoot, "v" + _version);
                Directory.CreateDirectory(outDir);
                GD.Print($"[HotReloadRuntime] 📦 开始编译 v{_version} …");

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
                if (p == null) { GD.PushError("[HotReloadRuntime] ❌ 无法启动 dotnet"); return; }
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(60_000))
                {
                    try { p.Kill(true); } catch { }
                    GD.PushError("[HotReloadRuntime] ❌ 编译超时（60s）");
                    return;
                }

                if (p.ExitCode != 0)
                {
                    var lines = (stderr + "\n" + stdout).Split('\n')
                        .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                                 || l.Contains(": 错误") || l.Contains("错误 CS"))
                        .TakeLast(8);
                    GD.PushError("[HotReloadRuntime] ❌ 编译失败：\n" + string.Join('\n', lines));
                    return;
                }

                var dll = Path.Combine(outDir, "GodotHotReload.dll");
                if (!File.Exists(dll))
                {
                    GD.PushError($"[HotReloadRuntime] ❌ 输出 DLL 不存在：{dll}");
                    return;
                }

                GD.Print($"[HotReloadRuntime] 📂 编译成功：{dll.Replace('\\', '/')}");
                var asm = LoadAlc(dll);
                if (asm == null) return;
                GD.Print("[HotReloadRuntime] 🎯 程序集已加载");
                ApplyPatch(asm);
            }
            finally { _compiling = false; }
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

        // ---------- 字节流加载：不锁 .hotswap 里的任何文件 ----------
        Assembly LoadAlc(string dllPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(dllPath);
                var alc = new AssemblyLoadContext("HotSwap_v" + _version, isCollectible: true);
                alc.Resolving += (ctx, name) =>
                {
                    // 关键：依赖程序集（GodotSharp/0Harmony 等）优先复用默认 ALC 中已加载的同一份。
                    // 否则反射新方法的 Godot 类型参数时会去文件找 GodotSharp 而失败，
                    // 且从文件重复加载会造成跨 ALC 类型身份分裂。
                    foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (loaded.GetName().Name == name.Name)
                            return loaded;
                    }
                    // 内存中确实没有，才回退从文件（字节流加载，不锁文件）
                    foreach (var d in new[] { dir, BinDir })
                    {
                        var f = Path.Combine(d, name.Name + ".dll");
                        if (File.Exists(f))
                        {
                            using var s = File.OpenRead(f);
                            return ctx.LoadFromStream(s);
                        }
                    }
                    return null;
                };

                using var fs = File.OpenRead(dllPath);
                var pdb = Path.ChangeExtension(dllPath, ".pdb");
                if (File.Exists(pdb))
                {
                    using var ps = File.OpenRead(pdb);
                    return alc.LoadFromStream(fs, ps);
                }
                return alc.LoadFromStream(fs);
            }
            catch (Exception ex)
            {
                GD.PushError($"[HotReloadRuntime] ❌ ALC 加载失败：{ex.Message}");
                return null;
            }
        }

        // ---------- Diff + Harmony patch（仅 static 方法） ----------
        void ApplyPatch(Assembly newAsm)
        {
            try
            {
                var oldAsm = typeof(HotReloadRuntime).Assembly; // 当前正在运行的程序集

                var oldMethods = SafeGetTypes(oldAsm)
                    .Where(IsUserType)
                    .SelectMany(t => t.GetMethods(BF))
                    .Where(m => !m.IsAbstract && !m.IsSpecialName && !IsGodotGenerated(m))
                    .ToList();

                var newMap = new Dictionary<string, MethodBase>();
                foreach (var t in SafeGetTypes(newAsm).Where(IsUserType))
                    foreach (var m in t.GetMethods(BF))
                        if (!m.IsAbstract && !m.IsSpecialName && !IsGodotGenerated(m))
                            newMap[DispatchKey(m)] = m;

                GD.Print($"[HotReloadRuntime] 🔍 旧 static 方法 {oldMethods.Count} / 新 {newMap.Count}");

                var prefix = typeof(HotReloadRuntime).GetMethod(nameof(Dispatch),
                    BindingFlags.Static | BindingFlags.NonPublic);

                int added = 0, updated = 0, failed = 0;
                foreach (var oldM in oldMethods)
                {
                    var key = DispatchKey(oldM);
                    if (!newMap.TryGetValue(key, out var newM)) continue;

                    _dispatch[key] = BuildStub(newM); // 已 patch 的方法：直接换实现目标即可

                    if (_patched.Contains(oldM)) { updated++; continue; }
                    try
                    {
                        _harmony.Patch(oldM, prefix: new HarmonyLib.HarmonyMethod(prefix));
                        _patched.Add(oldM);
                        added++;
                        GD.Print($"[HotReloadRuntime] ✅ Patch：{oldM.DeclaringType?.Name}.{oldM.Name}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        GD.PushError($"[HotReloadRuntime] ❌ Patch 失败 {oldM.Name}：{ex.Message}");
                    }
                }

                GD.Print($"[HotReloadRuntime] 🎉 v{_version} 完成：{added} 新增 / {updated} 更新 / {failed} 失败");
            }
            catch (Exception ex)
            {
                GD.PushError($"[HotReloadRuntime] ❌ ApplyPatch 异常：{ex.Message}\n{ex.StackTrace}");
            }
        }

        // 排除热更基础设施自身（patch 它们会破坏驱动逻辑）
        static bool IsUserType(Type t) =>
            t.Name != nameof(HotReloadRuntime) && t.Name != "GodotHotReloadPlugin";

        // Godot 源生成器为每个 GodotObject 子类注入的引擎桥接方法，绝不能 patch
        // （否则会出现 invalid program、引擎反射/调度异常）。按固定名 + 前缀 + 编译器标记三重识别。
        static readonly HashSet<string> GodotGeneratedNames = new()
        {
            "GetGodotMethodList", "GetGodotPropertyList", "GetGodotSignalList",
            "InvokeGodotClassMethod", "InvokeGodotClassStaticMethod",
            "HasGodotClassMethod", "HasGodotClassSignalMethod",
            "InitializeFromGameProject", "InitializeFromGodotObject",
            "SaveGodotObject", "GetGodotClassNativeInstanceId", "DisposeCore",
        };

        static bool IsGodotGenerated(MethodBase m)
        {
            var n = m.Name;
            if (GodotGeneratedNames.Contains(n)) return true;
            if (n.StartsWith("GetGodot", StringComparison.Ordinal)
                || n.StartsWith("InvokeGodotClass", StringComparison.Ordinal)
                || n.StartsWith("HasGodotClass", StringComparison.Ordinal))
                return true;
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

        static Func<object, object[], object> BuildStub(MethodBase newM)
        {
            return (instance, args) =>
            {
                try { return newM.Invoke(null, args ?? Array.Empty<object>()); }
                catch (TargetInvocationException tie)
                {
                    GD.PushError($"[HotReloadRuntime] 新代码异常：{tie.InnerException?.Message}");
                    return null;
                }
            };
        }

        // Harmony prefix：返回 false 跳过原 static 方法实现
        static bool Dispatch(MethodBase __originalMethod, object[] __args)
        {
            if (_dispatch.TryGetValue(DispatchKey(__originalMethod), out var stub))
            {
                stub(null, __args);
                return false;
            }
            return true;
        }
    }
}
