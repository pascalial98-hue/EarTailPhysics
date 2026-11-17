namespace EarTailPhysics;

// CN 2026.11.03 parsed layouts, verified against the loader's copy operations.
// Runtime descriptors use pointers (104-byte simulators / 56-byte chains);
// node records retain the PHYB 84-byte layout.
public unsafe sealed class DescriptorBuilder : IDisposable
{
    public NativeMemoryView Memory { get; } = new();
    public byte* Handle { get; }
    public int TailSimulatorIndex { get; private set; } = -1;
    public int TailChainIndex { get; private set; } = -1;
    public int LeftEarSimulatorIndex { get; private set; } = -1;
    public int LeftEarChainIndex { get; private set; } = -1;
    public int RightEarSimulatorIndex { get; private set; } = -1;
    public int RightEarChainIndex { get; private set; } = -1;
    public bool HasEars { get; private set; }
    public bool HasTail => TailSimulatorIndex >= 0;
    private static readonly string[] TailNames = ["n_sippo_b", "n_sippo_c", "n_sippo_d", "n_sippo_e", ""];

    public DescriptorBuilder(byte* original, Settings cfg, bool earBones)
    {
        Handle = Memory.Copy(original, 0x120);
        try
        {
            var count = original[0xD0];
            if (count is < 1 or > 96) throw new InvalidDataException("Unsupported simulator count");
            var source = *(byte**)(original + 0xD8);
            var sims = Memory.Allocate((count + 1) * 104);
            new ReadOnlySpan<byte>(source, count * 104).CopyTo(new Span<byte>(sims, count * 104));
            *(byte**)(Handle + 0xD8) = sims;
            if (!cfg.Enabled) return;
            int earChains = 0;
            for (var si = 0; si < count; si++)
            {
                var sim = sims + si * 104;
                int nc = sim[2];
                if (nc > 64) throw new InvalidDataException("Unsupported chain count");
                if (nc == 0) continue;
                var chains = Memory.Copy(*(byte**)(sim + 0x38), nc * 56);
                *(byte**)(sim + 0x38) = chains;
                for (var ci = 0; ci < nc; ci++)
                {
                    var chain = chains + ci * 56;
                    int nn = *(ushort*)(chain + 2);
                    if (nn is < 1 or > 64) throw new InvalidDataException("Unsupported node count");
                    var nodes = *(byte**)(chain + 0x30);
                    var first = NativeMemoryView.Name(nodes);
                    if (first is "j_mimi_l" or "j_mimi_r")
                    {
                        earChains++;
                        if (cfg.Ears && nn == 2 && NativeMemoryView.Name(nodes + 84) == "")
                        {
                            nodes = Memory.Copy(nodes, nn * 84);
                            *(byte**)(chain + 0x30) = nodes;
                            TuneEar(chain, nodes, cfg.EarGrade);
                            if (first == "j_mimi_l") { LeftEarSimulatorIndex = si; LeftEarChainIndex = ci; }
                            else { RightEarSimulatorIndex = si; RightEarChainIndex = ci; }
                            HasEars = true;
                        }
                    }
                    if (first != TailNames[0] || nn != 5 || cfg.TailStrength == 0) continue;
                    for (var i = 0; i < 5; i++)
                        if (NativeMemoryView.Name(nodes + i * 84) != TailNames[i])
                            throw new InvalidDataException("Non-standard tail chain");
                    if (HasTail) throw new InvalidDataException("Ambiguous duplicate tail chains");
                    nodes = Memory.Copy(nodes, 5 * 84);
                    *(byte**)(chain + 0x30) = nodes;
                    TailSimulatorIndex = si;
                    TailChainIndex = ci;
                    TuneTail(chain, nodes, cfg);
                    // Real terrain queries replace the flat character-relative plane.
                    sim[36] &= 0xF7;
                    if (cfg.GroundCollision || cfg.SceneCollision) sim[37] = 142;
                    if (cfg.BodyCollision > 0) AddBodyCollision(original, sim, chain, nodes, cfg.BodyCollision);
                }
            }
            if (cfg.Ears && earBones && earChains == 0)
            {
                CreateEars(sims + count * 104, cfg.EarGrade);
                Handle[0xD0] = (byte)(count + 1);
                HasEars = true;
                LeftEarSimulatorIndex = RightEarSimulatorIndex = count;
                LeftEarChainIndex = 0; RightEarChainIndex = 1;
            }
        }
        catch { Memory.Dispose(); throw; }
    }

    private static void Float(byte* p, int offset, float value) => *(float*)(p + offset) = value;

    private static void TuneEar(byte* chain, byte* nodes, int grade)
    {
        var e = Presets.Ears[grade];
        Float(chain, 4, e.Damping); Float(chain, 8, e.Speed);
        Float(nodes + 84, 36, e.Attraction); Float(nodes + 84, 40, e.Wind);
    }

    private static void TuneTail(byte* chain, byte* nodes, Settings cfg)
    {
        var t = Presets.Tails[cfg.TailType, cfg.TailStrength - 1];
        Float(chain, 4, t.Damping); Float(chain, 8, t.Speed);
        float[] nativeAttraction = [.05f, .025f, .0125f];
        for (var i = 0; i < 3; i++)
        {
            var n = nodes + (i + 1) * 84;
            Float(n, 36, nativeAttraction[i] * t.Attraction[i]);
            Float(n, 48, t.Angles[i] * MathF.PI / 180);
        }
        Float(nodes + 4 * 84, 36, MathF.Min(*(float*)(nodes + 4 * 84 + 36), t.Terminal));
        if (cfg.TailType == 1)
        {
            Float(nodes + 84, 40, .00055f);
            Float(nodes + 168, 40, .00095f);
        }
    }

    private void CreateEars(byte* sim, int grade)
    {
        sim[2] = 2;
        Float(sim, 12, -10);
        *(ushort*)(sim + 32) = 2; *(ushort*)(sim + 34) = 1;
        sim[36] = 0x13; sim[37] = 130;
        var chains = Memory.Allocate(112);
        *(byte**)(sim + 0x38) = chains;
        for (var side = 0; side < 2; side++)
        {
            var c = chains + side * 56;
            *(ushort*)(c + 2) = 2;
            Float(c, 20, 1); Float(c, 24, .12f);
            var nodes = Memory.Allocate(168);
            *(byte**)(c + 0x30) = nodes;
            NativeMemoryView.SetName(nodes, side == 0 ? "j_mimi_l" : "j_mimi_r");
            Float(nodes, 32, 1); Float(nodes, 40, 1); Float(nodes, 44, 1);
            Float(nodes, 48, MathF.PI / 2);
            Float(nodes, 52, -.26f); Float(nodes, 56, -.95f); Float(nodes, 60, -.17f);
            Float(nodes + 84, 32, 1); Float(nodes + 84, 48, MathF.PI); Float(nodes + 84, 52, 1);
            TuneEar(c, nodes, grade);
        }
    }

    private void AddBodyCollision(byte* original, byte* sim, byte* chain, byte* nodes, int size)
    {
        // Reuse the current race's native leg anchors, retaining all other physics.
        int capsules = original[0xE8], spheres = original[0xEC];
        if (capsules > 240 || spheres > 250) throw new InvalidDataException("Collider capacity");
        var oldCaps = *(byte**)(original + 0xF0);
        string[] anchors = ["asi_l_a", "asi_r_a", "asi_l_c", "asi_r_c"];
        var found = new int[4];
        for (var i = 0; i < 4; i++)
        {
            found[i] = -1;
            for (var j = 0; j < capsules; j++)
                if (NativeMemoryView.Name(oldCaps + j * 124) == anchors[i]) found[i] = j;
            if (found[i] < 0) throw new InvalidDataException("Native leg collider missing");
        }
        var cap = Memory.Allocate((capsules + 4) * 124);
        new ReadOnlySpan<byte>(oldCaps, capsules * 124).CopyTo(new Span<byte>(cap, capsules * 124));
        string[] names = ["etp_upper_l", "etp_upper_r", "etp_lower_l", "etp_lower_r", "etp_pelvis"];
        for (var i = 0; i < 4; i++)
        {
            var dest = cap + (capsules + i) * 124;
            new ReadOnlySpan<byte>(oldCaps + found[i] * 124, 124).CopyTo(new Span<byte>(dest, 124));
            NativeMemoryView.SetName(dest, names[i]);
            Float(dest, 120, i < 2 ? .07f : .055f);
        }
        Handle[0xE8] = (byte)(capsules + 4); *(byte**)(Handle + 0xF0) = cap;
        var sphere = Memory.Allocate((spheres + 1) * 80);
        if (spheres > 0) new ReadOnlySpan<byte>(*(byte**)(original + 0x110), spheres * 80).CopyTo(new Span<byte>(sphere, spheres * 80));
        var pelvis = sphere + spheres * 80;
        NativeMemoryView.SetName(pelvis, names[4]); NativeMemoryView.SetName(pelvis + 32, "j_kosi"); Float(pelvis, 76, .12f);
        Handle[0xEC] = (byte)(spheres + 1); *(byte**)(Handle + 0x110) = sphere;
        // Only isolated native standard-tail simulators are accepted for this feature.
        if (sim[2] != 1 || sim[0] != 0 || sim[1] != 0 || *(ushort*)chain != 0)
            throw new InvalidDataException("Existing tail collision routing: disable the physical mod first");
        var refs = Memory.Allocate(5 * 36);
        for (var i = 0; i < 5; i++) { NativeMemoryView.SetName(refs + i * 36, names[i]); *(uint*)(refs + i * 36 + 32) = 1; }
        sim[0] = 5; *(byte**)(sim + 40) = refs;
        for (var i = 0; i < 3; i++)
        {
            Float(nodes + (i + 2) * 84, 32, Presets.Radii[size][i]);
            *(uint*)(nodes + (i + 2) * 84 + 76) = 31;
        }
        Float(chain, 16, .2f);
    }
    public void Dispose() => Memory.Dispose();
}
