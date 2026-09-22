# Hot Reload Live — Godot Asset Store 发布文案

## 资产信息（上传表单填写）

| 字段 | 值 |
|---|---|
| **Asset Name** | Hot Reload Live |
| **Category** | Tools → Editor Tools |
| **Version** | 1.1.0 |
| **Godot Version** | 4.7 |
| **License** | MIT |
| **Repository URL** | （可选，放 GitHub 链接） |
| **Issues URL** | （可选） |
| **Icon** | 256×256 PNG |

---

## 简短描述（Short Description，列表页显示，150 字以内）

```
Runtime C# hot reload for Godot 4.7+ (.NET 8). Save a .cs file and your game updates instantly — no restart, no domain reload. Supports static + instance methods, generics, async, iterators, with a field-layout safety guard and one-click Restore All.
```

---

## 完整描述（Long Description，资产详情页）

```
# Hot Reload Live

**Save a `.cs` file — watch it run.**

Hot Reload Live is a runtime C# hot reload plugin for Godot 4.7+ (.NET 8). It watches your project's source files, compiles changes into an isolated AssemblyLoadContext, diffs method signatures, and patches the running game via Harmony — all in-process.

No manual reload. No scene reset. No state loss.

---

## Features

| Capability | Supported |
|---|---|
| Static methods (void / return / ref / out / overloads) | ✅ |
| Instance methods (cross-ALC DynamicMethod IL stub) | ✅ |
| Generic methods (object covers all ref types + 8 common value types) | ✅ |
| async / Task MoveNext (CoreCLR-safe patch) | ✅ |
| IEnumerable / iterator MoveNext | ✅ |
| Field layout guard — skips patch on structural change | ✅ |
| Restore All — one-click unpatch | ✅ |
| Editor plugin menu (Restart Watcher / Manual Compile / Restore All) | ✅ |
| Isolated ALC — no file locks on `.godot/mono/temp/bin/` | ✅ |
| Old ALC auto-unload (memory leak prevention) | ✅ |

---

## How It Works

```
User saves MyClass.cs
  → FileSystemWatcher (500ms debounce)
  → dotnet build → .hotswap/vN/ (isolated, no .godot lock)
  → AssemblyLoadContext.LoadFromStream (collectible, no file lock)
  → ComputeFieldHash check (skip patch if field layout changed)
  → Method diff (DispatchKey = full name + param types)
  → Harmony patch → DynamicMethod IL stub
  → Game runs new code on the NEXT frame
```

**Instance method trick**: a DynamicMethod stub forwards the old-ALC object reference directly as the new-ALC type's `this` pointer — no `castclass`, no type check. This bypasses the cross-ALC type-identity barrier (`ALC1.MyClass ≠ ALC2.MyClass`) that breaks naive `MethodInfo.Invoke`. Safe because the field-layout guard guarantees identical memory layout.

**Generic method trick**: Harmony can't patch open generic definitions. We patch constructed instantiations — `object` covers all reference types (CoreCLR JIT shares code), plus 8 common value types (`int`, `float`, `double`, `bool`, `long`, `byte`, `char`, `decimal`).

---

## Installation

1. Copy `addons/GodotHotReload/` into your project's `addons/` folder.
2. Add to your `.csproj`:
   ```xml
   <ItemGroup>
     <PackageReference Include="Lib.Harmony" Version="2.4.2" />
   </ItemGroup>
   ```
   (Harmony 2.4.2 embeds MonoMod/Cecil internally — no other dependencies.)
3. Project → Project Settings → Plugins → Enable **Hot Reload Live**.
4. Add the runtime node to your bootstrap scene's `_Ready()`:
   ```csharp
   AddChild(new GodotHotReload.HotReloadRuntime());
   ```
5. Press F5. Edit any `.cs` file in `src/`. Save. Watch the console.

---

## Editor Menu

| Menu Item | Action |
|---|---|
| **Hot Reload Live → Restart Watcher** | Forces an immediate rebuild (writes `.hotswap/trigger`) |
| **Hot Reload Live → Manual Compile** | Same as Restart Watcher |
| **Hot Reload Live → Restore All** | Unpatches everything (writes `.hotswap/restore`) |

The editor plugin communicates with the game process purely via marker files — no network, no cross-process reflection.

---

## Requirements

- **Godot 4.7+** (.NET / mono build — NOT GDScript, NOT Godot 3.x)
- **.NET 8 SDK** (for `dotnet build`)
- **Lib.Harmony 2.4.2** NuGet package

---

## Limitations (full details in Docs/Limitations.md)

- **Cannot change field layout** (add/remove/rename instance fields) — guard skips the patch; restart required
- **Cannot add new types or change inheritance**
- **Generic methods**: covers `object` + 8 common value types; uncommon value types (e.g. `Guid`, custom structs) run the old version until restart
- **async/iterator entry methods** are skipped (only `MoveNext` is patched) — this is correct behavior, the real logic lives after `await`/`yield`
- Godot-generated bridge methods (`SetGodotClassPropertyValue`, indexer `Set`/`Get`) are never patched

---

## Changelog

### v1.1.0 (2025)

- ✅ **Instance methods** — DynamicMethod IL-emit bypasses cross-ALC type identity wall
- ✅ **Generic methods** — enumeration of constructed instantiations (object + 8 value types)
- ✅ **async / Task MoveNext** — CoreCLR-safe Harmony patch
- ✅ **IEnumerable / iterator MoveNext** — patched
- ✅ **Compiler-generated type filter** — engine no longer self-patches `<>c` lambdas or `<Method>d__NN` state machines

### v1.0.0

- ✅ Static methods (void / return / ref / out / overloads)
- ✅ Field layout guard
- ✅ Restore All
- ✅ 500ms debounce watcher
- ✅ Isolated ALC + auto-unload

---

## License

MIT. Includes Lib.Harmony 2.4.2 by Andreas Pardeike (MIT).

---

## Documentation Included

- `Docs/README.md` — full install + usage guide
- `Docs/Limitations.md` — hard limits, soft limits, roadmap, troubleshooting
- `ARCHITECTURE.md` — deep-dive into ALC + DynamicMethod design
- `src/BoundaryDemo.cs` — end-to-end test demonstrating every supported feature
```

---

## Godot Asset Store 技术标签

`editor` `tools` `csharp` `hot-reload` `runtime` `debug` `productivity` `dotnet` `harmony`

## 截图 / 视频建议

| 类型 | 内容 |
|---|---|
| **Icon (256×256)** | 简洁 logo，背景 #478cbf（Godot 蓝），白色闪电 ⚡ |
| **Screenshot 1** | Console 日志：`✅ Patched: instance Player.TakeDamage` + 切换瞬间 v1→v2 |
| **Screenshot 2** | 编辑器插件菜单 3 项 |
| **Screenshot 3** | BoundaryDemo 输出：static/instance/generic/ref/out/async/iterator 全 v2 |
| **Video (optional)** | 15 秒录屏：编辑代码 → Ctrl+S → 实时热更 |
```
