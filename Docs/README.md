# Hot Reload Live for Godot

> Save a `.cs` file — watch it run. No restart. No domain reload. Zero stuttering.

**Hot Reload Live** is a runtime C# hot reload asset for **Godot 4.7+ (.NET 8)**. It watches your `src/` folder, compiles changed files into an isolated AssemblyLoadContext, diffs the method signatures, and patches the running game via Harmony — all in-process.

**Version**: 1.1.0  
**Engine**: Godot 4.7.2 (.NET / .NET 8 / Godot.NET.Sdk 4.7.2)  
**License**: MIT (this asset) + MIT (Lib.Harmony 2.4.2 by Andreas Pardeike)

---

## Features

| Capability | Status |
|---|---|
| Static methods (void / return / ref / out / overloads) | ✅ |
| **Instance methods** (cross-ALC DynamicMethod IL stub) | ✅ |
| **Generic methods** (object covers all ref types + 8 common value types) | ✅ |
| **async / Task MoveNext** (CoreCLR-safe patch) | ✅ |
| **IEnumerable / iterator MoveNext** | ✅ |
| Field layout guard (hash check — skips patch if types changed) | ✅ |
| Restore All (editor menu / code) | ✅ |
| Editor-triggered compile via marker file | ✅ |
| Isolated ALC — no file locks on `.godot/mono/temp/bin/` | ✅ |

---

## Install

### 1. Add the addon

Copy `addons/GodotHotReload/` into your project's `addons/` folder.

### 2. Add Lib.Harmony NuGet reference

Hot Reload Live depends on **Lib.Harmony 2.4.2**. Add this to your `.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="Lib.Harmony" Version="2.4.2" />
</ItemGroup>
```

No other packages needed — Harmony 2.4.2 embeds MonoMod/Cecil internally.

### 3. Enable the plugin

Godot → Project → Project Settings → Plugins → Enable **Hot Reload Live**.

### 4. Add HotReloadRuntime to your scene

```csharp
public override void _Ready()
{
    AddChild(new GodotHotReload.HotReloadRuntime());
}
```

That's it. Run F5, edit any `.cs` file in `src/`, save — and watch your game update live.

---

## How It Works

```
User saves MyClass.cs
       │
       ▼
FileSystemWatcher → 500ms silent debounce
       │
       ▼
dotnet build -p:OutputPath=.hotswap/vN/    ← isolated, no .godot lock
       │
       ▼
AssemblyLoadContext.LoadFromStream(dll,pdb) ← collectible, no file lock
       │
       ▼
ComputeFieldHash(new) != ComputeFieldHash(old)? → WARN + SKIP (guard!)
       │
       ▼
Diff: oldMethods ∩ newMethods (DispatchKey = full name + param types)
       │
       ├── IsGenericMethodDefinition?
       │     └─ MakeGenericMethod(object, int, float, ...) × N → Harmony patch each
       │
       ├── IsIterator/Async ENTRY? → SKIP (Harmony corrupts state machine IL)
       │     └─ MoveNext still gets patched below
       │
       └── Regular method → BuildGeneralStub (DynamicMethod IL)
             └─ instance methods: Ldarg_0 → no castclass → bypass cross-ALC type identity
             └── Harmony bool prefix: return Dispatch(__originalMethod, __instance, __args)
```

### Why DynamicMethod?

On .NET 8 CoreCLR, cross-ALC `MethodInfo.Invoke(instance, args)` throws `TargetException: Object does not match target type` because `ALC1.MyClass ≠ ALC2.MyClass` (type identity includes the assembly).

**DynamicMethod** solves this: we emit raw IL that pushes the old-ALC object ref directly as the new-ALC type's `this` pointer — no `castclass`, no type check, just a managed pointer transfer. This is safe because `ComputeFieldHash` guarantees the field layout hasn't changed.

### Why a separate output directory?

Godot locks `.godot/mono/temp/bin/Debug/` during runtime. Compiling to `.hotswap/vN/` avoids file locks entirely, and `AssemblyLoadContext.LoadFromStream(dllStream)` never touches the filesystem.

---

## Editor Plugin Menu

After enabling the plugin, you'll see three menu items under **Project → Hot Reload Live**:

| Menu Item | Action |
|---|---|
| **Hot Reload Live → Restart Watcher** | Touches `.hotswap/trigger` → forces immediate rebuild |
| **Hot Reload Live → Manual Compile** | Same as Restart Watcher |
| **Hot Reload Live → Restore All** | Writes `.hotswap/restore` → game process calls `Harmony.UnpatchAll()` |

The plugin communicates with the game process purely via marker files — no network, no reflection IPC, no cross-process calls.

---

## Console Output

Typical session:

```
[Hot Reload Live] ✅ Runtime started — watching D:/MyGame/
[Hot Reload Live] Changed: Player.cs
[Hot Reload Live] 📦 Building v1 …
[Hot Reload Live] 📂 Built: D:/MyGame/.hotswap/v1/MyGame.dll
[Hot Reload Live] 🔍 Old methods: 14 / New: 14
[Hot Reload Live] ✅ Patched: static Player.TakeDamage
[Hot Reload Live] ✅ Patched: instance Player.Heal
[Hot Reload Live] ✅ Generic Player.GetEntity`1 → 9 instantiations
[Hot Reload Live] 🎉 v1: 11 new / 0 updated / 1 failed / 9 generic-instantiations
```

---

## License

This asset is released under the **MIT License**.

Includes: **Lib.Harmony 2.4.2** by Andreas Pardeike — also MIT Licensed. See `LICENSE_Harmony`.

---

## See Also

- `Docs/Limitations.md` — what doesn't work and why
- `ARCHITECTURE.md` — deep-dive into the ALC + DynamicMethod design
- `src/BoundaryDemo.cs` — comprehensive end-to-end test (all supported features)
