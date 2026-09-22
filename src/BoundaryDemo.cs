using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Comprehensive boundary test for Godot Hot Reload Live v1.1 (full feature set).
/// </summary>
public partial class BoundaryDemo : Node
{
	float _t;
	int _round;

	// --- Instance field (covered by ComputeFieldHash guard) ---
	public int InstanceCounter;

	// --- Static tests ---

	public static void StaticGreet() => GD.Print("[Boundary] static void -> v1");

	public static int StaticAdd(int a, int b)
	{
		GD.Print("[Boundary] static ret -> v1");
		return a + b;
	}

	public static void StaticOutTest(out int result)
	{
		GD.Print("[Boundary] static out -> v1");
		result = 42;
	}

	public static void StaticOverload(string s) => GD.Print("[Boundary] overload(string) -> v1");
	public static void StaticOverload(int i)    => GD.Print("[Boundary] overload(int) -> v1");

	public static void StaticGeneric<T>(T value) => GD.Print($"[Boundary] generic<{typeof(T).Name}> -> v1, value={value}");

	// --- Instance tests (cross-ALC safe via DynamicMethod IL stub) ---

	public void InstanceGreet() => GD.Print($"[Boundary] instance void -> v1 (counter={InstanceCounter++})");

	public int InstanceAdd(int a, int b)
	{
		GD.Print("[Boundary] instance ret -> v1");
		return a + b;
	}

	public void InstanceRefTest(ref string label)
	{
		label = "[Boundary] instance ref -> v1";
	}

	// --- Async test (MoveNext gets patched; entry method skipped) ---

	public async Task AsyncGreetAsync()
	{
		GD.Print("[Boundary] async entry -> v1");
		await Task.Delay(1); // forces a state machine yield
		GD.Print("[Boundary] async after await -> v1");
	}

	// --- Iterator test (MoveNext gets patched; entry method skipped) ---

	public IEnumerable<int> IteratorGreet()
	{
		GD.Print("[Boundary] iterator entry -> v1");
		yield return 1;
		GD.Print("[Boundary] iterator after yield -> v1");
		yield return 2;
	}

	// ================================================================
	// Test runner
	// ================================================================

	public override void _Process(double delta)
	{
		_t += (float)delta;
		if (_t < 0.2f) return;
		_t = 0f;

		// Static
		StaticGreet();
		StaticAdd(3, 4);
		StaticOutTest(out int o);
		StaticOverload("hi");
		StaticOverload(42);
		StaticGeneric<string>("hello");
		StaticGeneric<int>(99);

		// Instance
		InstanceGreet();
		InstanceAdd(10, 20);
		string label = "";
		InstanceRefTest(ref label);
		GD.Print("  ref result: " + label);

		// Async (fire-and-forget; await/after-await logs come later)
		_ = AsyncGreetAsync();

		// Iterator
		foreach (var n in IteratorGreet())
			GD.Print("  iterator value: " + n);

		_round++;
		if (_round % 10 == 0)
			GD.Print($"[Boundary] === Round={_round}: all calls completed ===");
	}
}

// Example of generic class
public class GenericBox<T>
{
	public T Value;
	public void Set(T v) { Value = v; }
}
