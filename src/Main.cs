using Godot;
using GodotHotReload;

/// <summary>
/// 热更边界测试 — Godot 版
/// 每 0.2 秒调用 StaticGreet()，验证热更后输出切换
/// </summary>
public partial class Main : Node
{
	float _t;
	int _round;

	public override void _Ready()
	{
		// 启动游戏进程内热更（仅在有源码的调试环境生效）
		AddChild(new HotReloadRuntime());
	}

	public override void _Process(double delta)
	{
		_t += (float)delta;
		if (_t < 0.2f) return;
		_t = 0f;

		StaticGreet();
		_round++;
		if (_round % 10 == 0)
			GD.Print($"[边界] Round={_round}");
	}

	// 热更目标：改版本号即可验证
	public static void StaticGreet() => GD.Print("[边界A static] 热更v9");
}
