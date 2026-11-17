using System.Numerics;
using EarTailPhysics;

unsafe partial class Program
{
    static void TestRuntimeBindingAndWind()
    {
        using var mem = new NativeMemoryView();
        var draw = mem.Allocate(0x200);
        var skeleton = mem.Allocate(0x120);
        var module = mem.Allocate(0x590);
        *(byte**)(draw + 0x158) = module;
        *(byte**)(module + 0x98) = skeleton;
        *(byte**)(skeleton + 0xB8) = draw;
        Assert(RuntimeBinding.ModuleForDraw((nint)draw, (nint)draw) == (nint)module, "Wind entry can identify self without a job callback");
        Assert(RuntimeBinding.ModuleForDraw(1, (nint)draw) == 0, "Reject another draw before dereferencing it");
        Assert(!RuntimeBinding.OwnsModule(0, (nint)draw, out _), "Null module rejected");
        Assert(!RuntimeBinding.OwnsModule((nint)module, 0, out _), "No player means no takeover");
        *(nint*)(skeleton + 0xB8) = 1;
        Assert(RuntimeBinding.ModuleForDraw((nint)draw, (nint)draw) == 0, "Reject stale owner on redraw");
        *(byte**)(skeleton + 0xB8) = draw;

        var vector = mem.Allocate(3 * 8);
        *(byte**)(module + 0xA0) = vector;
        *(byte**)(module + 0xA8) = vector + 16;
        var oldTail = mem.Allocate(0x450);
        var oldOther = mem.Allocate(0x450);
        *(byte**)vector = oldTail; *(byte**)(vector + 8) = oldOther;
        *(byte**)(oldTail + 0x18) = skeleton; *(byte**)(oldOther + 0x18) = skeleton;
        *(uint*)(oldTail + 0x10) = 14; *(uint*)(oldOther + 0x10) = 8;
        var input = new Vector3(-.13657814f, 0, 2.7602878f); // Prior read-only Mod sample, unscaled.
        var smoothed = input * .8f;
        *(Vector3*)(oldTail + 0x40) = input; *(Vector3*)(oldTail + 0x430) = smoothed;
        *(Vector3*)(oldOther + 0x40) = Vector3.UnitX;
        var saved = NativeWindState.Capture((nint)module);

        // Model native Load destroying/replacing both simulators while reusing vector storage.
        new Span<byte>(oldTail, 0x450).Clear(); new Span<byte>(oldOther, 0x450).Clear();
        var newTail = mem.Allocate(0x450);
        var newOther = mem.Allocate(0x450);
        var newEars = mem.Allocate(0x450);
        *(byte**)vector = newTail; *(byte**)(vector + 8) = newOther; *(byte**)(vector + 16) = newEars;
        *(byte**)(module + 0xA8) = vector + 24;
        foreach (var sim in new nint[] { (nint)newTail, (nint)newOther, (nint)newEars })
            *(byte**)(sim + 0x18) = skeleton;
        *(uint*)(newTail + 0x10) = 142; *(uint*)(newOther + 0x10) = 8; *(uint*)(newEars + 0x10) = 130;
        *(Vector3*)(newEars + 0x40) = Vector3.UnitZ;
        *(float*)(newTail + 0x50) = .016f; newTail[0x444] = 1;
        saved.Restore((nint)module, 0);
        Assert(*(Vector3*)(newTail + 0x40) == input, "Reload preserves live wind, including contact group remap");
        Assert(*(Vector3*)(newTail + 0x430) == smoothed, "Reload preserves wind smoothing without retuning");
        Assert(*(Vector3*)(newOther + 0x40) == Vector3.UnitX, "Unmodified source simulators retain their own wind");
        Assert(*(Vector3*)(newEars + 0x40) == Vector3.UnitZ, "Appended ears do not inherit an unrelated simulator's wind");
        Near(*(float*)(newTail + 0x50), .016f, "Wind handoff does not alter timestep");
        Assert(newTail[0x444] == 1, "Wind handoff does not override animation controls");

        var chains = mem.Allocate(8);
        var chain = mem.Allocate(0x180);
        *(byte**)(newTail + 0x78) = chains; *(byte**)(newTail + 0x80) = chains + 8;
        *(byte**)chains = chain; *(byte**)(chain + 0x10) = skeleton; *(ushort*)(chain + 0xE8) = 5;
        Assert(RuntimeBinding.TailChain((nint)module, 0, 0, out var bound) == (nint)chain && bound == (nint)newTail,
            "Bind current tail after the simulator vector is reused");
        var replacement = mem.Allocate(0x180);
        *(byte**)chains = replacement; *(byte**)(replacement + 0x10) = skeleton; *(ushort*)(replacement + 0xE8) = 5;
        Assert(RuntimeBinding.TailChain((nint)module, 0, 0, out _) == (nint)replacement, "Refresh replaced chain before contact callbacks");
        *(nint*)(replacement + 0x10) = 1;
        Assert(RuntimeBinding.TailChain((nint)module, 0, 0, out _) == 0, "Do not bind a different skeleton's chain");
        Assert(RuntimeBinding.TailChain((nint)module, 0, 1, out _) == 0, "Reject absent chain");
        Assert(RuntimeBinding.TailChain((nint)module, 3, 0, out _) == 0, "Reject absent simulator");

        *(uint*)(newTail + 0x10) = 7; *(Vector3*)(newTail + 0x40) = Vector3.UnitY;
        saved.Restore((nint)module, 0);
        Assert(*(Vector3*)(newTail + 0x40) == Vector3.UnitY, "Do not restore wind into a mismatched simulator");
        *(uint*)(newTail + 0x10) = 14;
        *(Vector3*)(newTail + 0x40) = new(float.NaN, 0, 0);
        var invalid = NativeWindState.Capture((nint)module);
        *(Vector3*)(newTail + 0x40) = Vector3.Zero;
        invalid.Restore((nint)module, 0);
        Assert(*(Vector3*)(newTail + 0x40) == Vector3.Zero, "Nonfinite wind never propagates");
    }
}
