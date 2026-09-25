# Godot Hot Reload Live — Architecture

> v1.1.1 (2026-09-25) — Godot 4.7+ .NET 8 / C#
>
> **v1.1.1 changelog**: fixed patching of non-generic methods on generic type definitions
> (e.g. `GenericBox<T>.Set`) — previously failed with `NotSupportedException`; now patched
> via per-instantiation constructed types (`GetMethodFromHandle(handle, constructedType.TypeHandle)`),
> same 9 type-argument strategy as generic methods.

## 1. Process Model

Godot F5 runs the game as a **separate child process** from the editor. Therefore:

| Process | Component | Responsibility |
|---------|-----------|---------------|
| **Game process** | `HotReloadRuntime.cs` (Node, added to scene) | Watcher → Debounce → Compile → ALC Load → Diff → Harmony Patch |
| **Editor process** | `GodotHotReloadPlugin.cs` (EditorPlugin, `[Tool]`) | Menu UI only — start/stop/trigger/restore |

The editor plugin sends signals to the game process via a **file-based IPC** (a marker file `.hotswap/trigger`) because Godot EditorPlugin cannot directly call game-process nodes.

## 2. Pipeline

```
File Save (VS Code / Godot)
    │
    ▼
FileSystemWatcher (500ms silent debounce)
    │
    ▼
dotnet build -c Debug --no-restore -p:OutputPath=.hotswap/vN/
    │
    ├── ExitCode ≠ 0 → Log errors, stop
    │
    ▼
AssemblyLoadContext("HotSwap_vN", isCollectible: true)
    │   LoadFromStream (no file lock)
    │   Resolving: reuse default ALC for GodotSharp / 0Harmony
    │
    ▼
Diff (old static methods vs new)
    │   DispatchKey = $"{Type.FullName}::{Method.Name}|{paramTypes}"
    │   Skip: abstract / special-name / Godot-generated / CompilerGenerated
    │   Skip: field-layout changed (Field Guard)
    │
    ▼
Harmony Patch
    │   _dispatch[key] = newM.Invoke(null, args...)
    │   _harmony.Patch(oldM, prefix: Dispatch)
    │
    ▼
GC.Collect() + alc.Unload() (optional, on next reload)
```

## 3. Scope (v1.0 — $9.99)

| Capability | Status | Notes |
|------------|--------|-------|
| Static void methods | ✅ | Core |
| Static with return value | ✅ | Core |
| Static ref / out parameters | ✅ | Core |
| Method overloads | ✅ | DispatchKey includes param types |
| Generic static methods | ✅ | CoreCLR — no Mono ImportGenericParameter issue |
| Generic class methods (non-generic method on `Foo<T>`) | ✅ | v1.1.1 — per-instantiation patch on constructed types |
| Instance methods | ✅ | v1.1 — DynamicMethod IL stub (no castclass, bypasses cross-ALC identity) |
| async / iterator MoveNext | ✅ | v1.1 — patch MoveNext; entry method skipped (IL corruption risk) |
| Field layout guard | ✅ | Hash fields; skip if changed |
| Restore All | ✅ | Harmony.UnpatchAll + clear dispatch |
| Multi-project cross-assembly | ❌ | Godot .NET single-assembly by default |

## 4. Godot-Specific Constraints

| Constraint | Solution |
|-----------|----------|
| Source generators inject GodotGenerated methods | `IsGodotGenerated()` filter (GetGodotMethodList, GetGodotPropertyList, etc.) |
| Game/Editor process split | All runtime logic in game process; editor plugin = UI only |
| .godot/mono/temp/bin locked by game | Compile to `.hotswap/vN/` (temp dir), load via ALC LoadFromStream |
| Godot 4.7 .NET 8 = CoreCLR | Harmony bool prefix works; ALC collectible works; generic methods work |
| 0Harmony.dll not auto-copied | MSBuild Target copies after dotnet build |

## 5. Unity vs Godot Feature Parity

| Feature | Unity 2022.3 Mono | Godot 4.7 .NET 8 |
|---------|-------------------|------------------|
| Static methods | ✅ | ✅ |
| Instance methods | ✅ (DynamicMethod IL) | ✅ (DynamicMethod IL) |
| ref/out | ✅ | ✅ |
| Overloads | ✅ | ✅ |
| Generics | ✅ (constructed only) | ✅ |
| Coroutine/async MoveNext | ✅ (Coroutine) | ✅ (async/iterator MoveNext) |
| Field Guard | ✅ | ✅ |
| Restore All | ✅ | ✅ |
| Cross-assembly | ✅ (asmdef) | ❌ (single-assembly) |
| ALC | ❌ (Mono has no collectible ALC) | ✅ |
| bool prefix | ⚠️ Deadlock risk | ✅ No issue |
| Generic direct patch | ❌ ImportGenericParameter | ✅ No issue |

## 6. File Layout

```
D:\GodotHotReload\
├── addons/GodotHotReload/
│   ├── plugin.cfg              ← v1.0.0, English
│   └── GodotHotReloadPlugin.cs ← Editor UI only (Tool attribute)
├── src/
│   ├── HotReloadRuntime.cs     ← Core engine (game process)
│   ├── BoundaryDemo.cs         ← Comprehensive test sample
│   ├── Main.cs / Main.tscn     ← Minimal bootstrap scene
│   └── Docs/                   ← README.md + Limitations.md + LICENSE
├── .hotswap/                   ← Build output (gitignored)
│   ├── v1/ GodotHotReload.dll
│   ├── v2/ GodotHotReload.dll
│   └── trigger                 ← Editor → Game IPC marker
└── GodotHotReload.csproj       ← Lib.Harmony 2.4.2, Godot.NET.Sdk 4.7.2
```

## 7. Technical Risks & Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Instance method cross-ALC call fails | High (v1.1) | DynamicMethod IL-emit (same as Unity version); or compile into default ALC with LoadFrom |
| dotnet build takes >60s | Medium | Timeout + error message; consider Roslyn in-proc compilation later |
| Compilation locks bin/Debug/ | Low | Already compile to separate temp dir (.hotswap/vN/) |
| Godot source generator new methods | Low | Name-based filter + CompilerGeneratedAttribute check |
| ALC memory leak (not unloading) | Medium | On next reload, call alc.Unload() + GC.Collect() before creating new ALC |
| Field guard hash collision | Very Low | Use full type name + field count + field types for hash |
