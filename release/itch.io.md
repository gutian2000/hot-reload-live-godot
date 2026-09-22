# Hot Reload Live — itch.io 发布页文案

## 标题（60 字以内）

**Hot Reload Live — C# Hot Reload for Godot 4.7+ (.NET 8)**

## 一句话简介（Tagline，显示在封面图下）

> Save a `.cs` file — watch it run. No restart, no domain reload, zero stuttering.

## 价格建议

**$9.99**（对标 Unity 版 $49.99 的 1/5，Godot 生态定价更亲民）

---

## 正文（itch.io 支持 HTML / Markdown）

```html
<p align="center"><strong>Save, compile, play — without stopping.</strong></p>

<p>Hot Reload Live watches your C# source files, compiles them into an isolated AssemblyLoadContext, and patches your <em>running</em> game in real time. Edit a method body, hit Ctrl+S, and the next frame runs the new code. No manual reload. No scene reset. No state loss.</p>

<hr>

<h2>🔥 What you can change at runtime</h2>

<ul>
  <li><strong>Static methods</strong> — void, return values, ref/out params, overloads</li>
  <li><strong>Instance methods</strong> — full cross-ALC forwarding via DynamicMethod IL emit (no type-identity wall)</li>
  <li><strong>Generic methods</strong> — all reference types + 8 common value types (int, float, double, bool, long, byte, char, decimal)</li>
  <li><strong>async / Task</strong> — MoveNext state machine patched safely</li>
  <li><strong>IEnumerable / iterators</strong> — MoveNext patched, yields update live</li>
  <li><strong>Field layout guard</strong> — detects structural changes and skips the patch with a warning (no memory corruption)</li>
  <li><strong>Restore All</strong> — one-click unpatch, back to original binaries</li>
</ul>

<hr>

<h2>⚡ How it works</h2>

<p>Hot Reload Live compiles your project into <code>.hotswap/vN/</code> (never touching Godot's locked <code>.godot/mono/temp/bin/</code>), loads it into a collectible <code>AssemblyLoadContext</code>, diffs method signatures, and patches via Harmony. For instance methods, a DynamicMethod stub forwards the old object reference directly as the new type's <code>this</code> pointer — bypassing the cross-ALC type-identity barrier that breaks naive <code>MethodInfo.Invoke</code>.</p>

<hr>

<h2>📦 Requirements</h2>

<ul>
  <li>Godot 4.7+ (<strong>.NET / mono</strong> build only — not GDScript, not Godot 3.x)</li>
  <li>.NET 8 SDK (for <code>dotnet build</code>)</li>
  <li>One NuGet package: <code>Lib.Harmony 2.4.2</code> (bundled instructions)</li>
</ul>

<hr>

<h2>🚀 Quick Start (30 seconds)</h2>

<ol>
  <li>Drop <code>addons/GodotHotReload/</code> into your project</li>
  <li>Add <code>&lt;PackageReference Include="Lib.Harmony" Version="2.4.2" /&gt;</code> to your <code>.csproj</code></li>
  <li>Enable the plugin (Project → Project Settings → Plugins)</li>
  <li>In your bootstrap script's <code>_Ready()</code>: <code>AddChild(new GodotHotReload.HotReloadRuntime());</code></li>
  <li>F5, edit any <code>.cs</code>, save — done.</li>
</ol>

<hr>

<h2>🛡️ Safety first</h2>

<ul>
  <li><strong>Field layout guard</strong> — if you add/remove fields, the patch is skipped with a clear warning. Restart needed (same as any hot reload tool).</li>
  <li><strong>No file locks</strong> — builds go to <code>.hotswap/</code>, not Godot's temp dir.</li>
  <li><strong>ALC unload</strong> — old generations are unloaded to prevent memory leaks.</li>
  <li><strong>Godot bridge methods</strong> are never patched (explicit allowlist).</li>
</ul>

<hr>

<h2>📝 License</h2>

<p>MIT. Includes Lib.Harmony 2.4.2 (MIT, by Andreas Pardeike).</p>

<hr>

<p align="center">Full documentation included in the zip: <code>README.md</code>, <code>Limitations.md</code>, <code>ARCHITECTURE.md</code>.</p>
```

---

## itch.io 元数据（上传时填写）

| 字段 | 值 |
|---|---|
| **Kind of project** | Tools / Unity 插件 / 开发工具 |
| **Classification** | Game development tool |
| **Pricing** | $9.99（Pay what you want: No） |
| **Downloads** | `HotReloadLive_v1.1.0.zip` |
| **Operating systems** | Windows, macOS, Linux（.NET 8 跨平台） |
| **Tags** | godot, hot-reload, csharp, tool, editor, productivity, runtime |
| **Access** | 公开 |
| **Community** | 允许评论 |

## itch.io 封面图 / 截图建议

| 图 | 内容 |
|---|---|
| **Cover (630×500)** | Godot 编辑器 + Console 显示 `[Hot Reload Live] ✅ Runtime started` + 热更瞬间的 "v1 → v2" 切换 |
| **Screenshot 1 (1280×720)** | 保存文件前后 Console 对比 |
| **Screenshot 2 (1280×720)** | 编辑器插件菜单截图（3 个菜单项） |
| **GIF (optional)** | 10 秒录屏：改代码 → 保存 → Console 切换 v2 |
