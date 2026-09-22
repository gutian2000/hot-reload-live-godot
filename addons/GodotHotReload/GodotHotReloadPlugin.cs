using Godot;
using System;
using System.IO;

namespace GodotHotReload
{
    /// <summary>
    /// Godot Hot Reload Live — editor plugin.
    /// This plugin is UI-only: game-process hot reload engine (HotReloadRuntime.cs)
    /// handles watcher + compile + patch. The plugin sends commands via a trigger file
    /// because Godot EditorPlugin cannot directly communicate with the game process.
    /// </summary>
    [Tool]
    public partial class GodotHotReloadPlugin : EditorPlugin
    {
        static string ProjectRoot => ProjectSettings.GlobalizePath("res://");
        static string HotSwapRoot => Path.Combine(ProjectRoot, ".hotswap");
        static string TriggerFile => Path.Combine(HotSwapRoot, "trigger");
        static string RestoreFile => Path.Combine(HotSwapRoot, "restore");

        public override void _EnterTree()
        {
            AddToolMenuItem("Hot Reload Live/Restart Watcher",
                Callable.From(() => { CreateTrigger(); GD.Print("[Hot Reload Live] Triggered game-side watcher restart"); }));
            AddToolMenuItem("Hot Reload Live/Manual Compile",
                Callable.From(() => { CreateTrigger(); GD.Print("[Hot Reload Live] Triggered manual compile"); }));
            AddToolMenuItem("Hot Reload Live/Restore All",
                Callable.From(RestoreAll));
            GD.Print("[Hot Reload Live] Plugin loaded — editor UI ready");
        }

        public override void _ExitTree()
        {
            GD.Print("[Hot Reload Live] Plugin unloaded");
        }

        // Marker file that HotReloadRuntime picks up on next _Process tick
        void CreateTrigger()
        {
            Directory.CreateDirectory(HotSwapRoot);
            File.WriteAllText(TriggerFile, DateTime.UtcNow.ToString("o"));
        }

        // Restore All — writes a marker file that HotReloadRuntime detects
        // (runs in game process, Harmony.UnpatchAll + clear dispatch)
        void RestoreAll()
        {
            Directory.CreateDirectory(HotSwapRoot);
            File.WriteAllText(RestoreFile, DateTime.UtcNow.ToString("o"));
            GD.Print("[Hot Reload Live] Restore All triggered");
        }
    }
}
