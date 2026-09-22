using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace GodotHotReload
{
    /// <summary>
    /// GodotHotReload 编辑器插件 — 运行时 C# 热重载
    /// 架构：FileSystemWatcher → dotnet build → ALC 加载 → Diff → Harmony patch + Dispatch 桩
    /// </summary>
    [Tool]
    public partial class GodotHotReloadPlugin : EditorPlugin
    {
        bool _watching;
        FileSystemWatcher _watcher;
        volatile bool _pending;
        static bool _compiling;
        static int _nextVersion = 1;
        static readonly HarmonyLib.Harmony _harmony = new("com.gutian2000.godot.hotreload");
        static readonly Dictionary<string, Func<object, object[], object>> _dispatch = new();
        static readonly HashSet<MethodBase> _patched = new();

        // 用 Godot API 获取项目根目录（Directory.GetCurrentDirectory() 在 Godot 里可能不对）
        static string ProjectRoot => ProjectSettings.GlobalizePath("res://");
        static string GodotBinDir => Path.Combine(ProjectRoot, ".godot", "mono", "temp", "bin", "Debug");

        // ---------- 生命周期 ----------
        public override void _EnterTree()
        {
            GD.Print("========== [HotReload] 插件 _EnterTree ==========");
            GD.Print($"[HotReload] ProjectRoot={ProjectRoot}");
            GD.Print($"[HotReload] GodotBinDir={GodotBinDir}");

            AddToolMenuItem("Hot Reload/开始监听", Callable.From(StartWatching));
            AddToolMenuItem("Hot Reload/停止监听", Callable.From(StopWatching));
            AddToolMenuItem("Hot Reload/手动编译", Callable.From(ManualCompileSync));
            AddToolMenuItem("Hot Reload/还原补丁", Callable.From(RestoreAll));

            GD.Print("========== [HotReload] 菜单已注册 ==========");
        }

        public override void _ExitTree()
        {
            StopWatching();
            RestoreAll();
        }

        public override void _Process(double delta)
        {
            if (_pending && !_compiling)
            {
                _pending = false;
                GD.Print("[HotReload] 检测到文件变更，开始编译…");
                ManualCompileSync();
            }
        }

        void ManualCompileSync()
        {
            try { _ = CompileAndLoadAsync(); }
            catch (Exception ex) { GD.PushError($"[HotReload] ManualCompileSync 异常：{ex.Message}"); }
        }

        // ---------- 文件监听 ----------
        void StartWatching()
        {
            if (_watching) { GD.Print("[HotReload] 已在监听中"); return; }

            try
            {
                _watcher = new FileSystemWatcher(ProjectRoot, "*.cs")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += (s, e) =>
                {
                    GD.Print($"[HotReload] 文件变更：{e.Name}");
                    _pending = true;
                };
                _watcher.Created += (s, e) => { _pending = true; };

                _watching = true;
                GD.Print($"[HotReload] ✅ 监听已启动：{ProjectRoot}");
            }
            catch (Exception ex)
            {
                GD.PushError($"[HotReload] ❌ 监听启动失败：{ex.Message}");
            }
        }

        void StopWatching()
        {
            if (_watcher != null) { _watcher.Dispose(); _watcher = null; }
            _watching = false;
            GD.Print("[HotReload] 已停止监听");
        }

        // ---------- 编译 + 加载 ----------
        async Task CompileAndLoadAsync()
        {
            if (_compiling) { GD.Print("[HotReload] 编译进行中，跳过"); return; }
            _compiling = true;
            try
            {
                GD.Print($"[HotReload] 📦 开始编译 v{_nextVersion} …");

                var dllPath = await Task.Run(() => RunDotnetBuild());
                if (dllPath == null) return;

                GD.Print($"[HotReload] 📂 编译成功：{dllPath}");

                var asm = LoadFromALC(dllPath);
                if (asm == null) return;

                GD.Print($"[HotReload] 🎯 加载成功：{asm.GetName().Name} v{asm.GetName().Version}");

                ApplyHotReload(asm);
            }
            finally
            {
                _compiling = false;
            }
        }

        string RunDotnetBuild()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "build -c Debug --no-restore",
                    WorkingDirectory = ProjectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();

                if (p.ExitCode != 0)
                {
                    GD.PushError($"[HotReload] ❌ dotnet build 失败：{stderr.Split('\n').LastOrDefault()}");
                    return null;
                }

                // 找最新的 GodotHotReload.dll
                var dll = Directory.GetFiles(GodotBinDir, "GodotHotReload.dll").FirstOrDefault();
                if (dll == null)
                {
                    GD.PushError($"[HotReload] ❌ 找不到 DLL，搜索路径：{GodotBinDir}");
                    return null;
                }
                return dll;
            }
            catch (Exception ex)
            {
                GD.PushError($"[HotReload] ❌ dotnet build 异常：{ex.Message}");
                return null;
            }
        }

        Assembly LoadFromALC(string dllPath)
        {
            try
            {
                var alc = new AssemblyLoadContext("HotReload_" + Guid.NewGuid(), isCollectible: true);
                alc.Resolving += (context, name) =>
                {
                    var path = Path.Combine(Path.GetDirectoryName(dllPath), name.Name + ".dll");
                    if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
                    // 也试试 Godot 自己的 API
                    var globalPath = ProjectSettings.GlobalizePath($"res://.godot/mono/temp/bin/Debug/{name.Name}.dll");
                    if (File.Exists(globalPath)) return context.LoadFromAssemblyPath(globalPath);
                    return null;
                };
                var asm = alc.LoadFromAssemblyPath(dllPath);
                return asm;
            }
            catch (Exception ex)
            {
                GD.PushError($"[HotReload] ❌ ALC 加载失败：{ex.Message}");
                return null;
            }
        }

        // ---------- Diff + Patch ----------
        void ApplyHotReload(Assembly newAsm)
        {
            try
            {
                var oldAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "GodotHotReload");
                if (oldAsm == null)
                {
                    GD.PushError("[HotReload] ❌ 未找到原程序集 GodotHotReload");
                    return;
                }

                GD.Print($"[HotReload] 🔍 原程序集类型数：{oldAsm.GetTypes().Length}");

                var oldMethods = oldAsm.GetTypes()
                    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    .Where(m => !m.IsAbstract && !m.IsSpecialName)
                    .ToList();

                GD.Print($"[HotReload] 🔍 原程序集方法数：{oldMethods.Count}");

                var newMap = new Dictionary<string, MethodBase>();
                foreach (var t in newAsm.GetTypes())
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        if (!m.IsAbstract && !m.IsSpecialName)
                            newMap[DispatchKey(m)] = m;

                GD.Print($"[HotReload] 🔍 新程序集方法数：{newMap.Count}");

                int changed = 0, same = 0, failed = 0;
                foreach (var oldM in oldMethods)
                {
                    var key = DispatchKey(oldM);
                    if (!newMap.TryGetValue(key, out var newM)) continue;
                    if (_patched.Contains(oldM)) { same++; continue; }

                    var stub = BuildDispatchStub(newM);
                    _dispatch[key] = stub;

                    try
                    {
                        var prefix = typeof(GodotHotReloadPlugin).GetMethod(nameof(Dispatch),
                            BindingFlags.Static | BindingFlags.NonPublic);
                        _harmony.Patch(oldM, prefix: new HarmonyLib.HarmonyMethod(prefix));
                        _patched.Add(oldM);
                        changed++;
                        GD.Print($"[HotReload] ✅ Patched: {oldM.DeclaringType.Name}.{oldM.Name}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        GD.PushError($"[HotReload] ❌ Patch 失败 {oldM.Name}：{ex.Message}");
                    }
                }

                GD.Print($"[HotReload] 🎉 v{_nextVersion} 完成：{changed} 个替换 / {same} 跳过 / {failed} 失败");
                _nextVersion++;
            }
            catch (Exception ex)
            {
                GD.PushError($"[HotReload] ❌ ApplyHotReload 异常：{ex.Message}\n{ex.StackTrace}");
            }
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

        static Func<object, object[], object> BuildDispatchStub(MethodBase newM)
        {
            return (instance, args) =>
            {
                try
                {
                    object result;
                    if (newM.IsStatic)
                        result = newM.Invoke(null, args);
                    else
                        result = newM.Invoke(instance, args);
                    return result;
                }
                catch (TargetInvocationException tie)
                {
                    GD.PushError($"[HotReload] Dispatch 异常：{tie.InnerException?.Message}");
                    return null;
                }
            };
        }

        static bool Dispatch(MethodBase __originalMethod, object __instance, object[] __args)
        {
            var key = DispatchKey(__originalMethod);
            if (_dispatch.TryGetValue(key, out var stub))
            {
                stub(__instance, __args ?? Array.Empty<object>());
                return false; // 跳过原方法
            }
            return true;
        }

        // ---------- 还原 ----------
        public static void RestoreAll()
        {
            if (_patched.Count == 0) return;
            _harmony.UnpatchAll(_harmony.Id);
            _patched.Clear();
            _dispatch.Clear();
            GD.Print("[HotReload] 已还原全部补丁");
        }
    }
}
