using Godot;
using GodotHotReload;

/// <summary>
/// Minimal bootstrap — adds HotReloadRuntime + runs BoundaryDemo.
/// Replace this scene with your own; just add HotReloadRuntime
/// as a child node in _Ready() to enable hot reload.
/// </summary>
public partial class Main : Node
{
	public override void _Ready()
	{
		// Enable runtime hot reload (game process)
		AddChild(new HotReloadRuntime());

		// Run comprehensive boundary test
		AddChild(new BoundaryDemo());
	}
}
