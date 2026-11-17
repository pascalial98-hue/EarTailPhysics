using System.Numerics;

namespace EarTailPhysics;

// Native Load recreates simulators from file wind (normally zero). Preserve the live
// input for the job-boundary fallback; the native wind callback continues to own updates.
public sealed unsafe class NativeWindState
{
    private readonly record struct Entry(uint Group, Vector3 Input, Vector3 Smoothed);
    private readonly Entry[] entries;
    private NativeWindState(Entry[] entries) => this.entries = entries;

    private static int Count(nint module, out nint begin)
    {
        begin = *(nint*)(module + 0xA0);
        var end = *(nint*)(module + 0xA8);
        if (begin == 0 && end == 0) return 0;
        if (begin == 0 || end < begin || (end - begin) % 8 != 0 || (end - begin) / 8 > 97)
            throw new InvalidDataException("Invalid native simulator vector");
        return (int)((end - begin) / 8);
    }

    public static NativeWindState Capture(nint module)
    {
        var count = Count(module, out var begin);
        var result = new Entry[count];
        for (var i = 0; i < count; i++)
        {
            var sim = *(nint*)(begin + i * 8);
            if (sim == 0 || *(nint*)(sim + 0x18) != *(nint*)(module + 0x98))
                throw new InvalidDataException("Simulator ownership changed");
            result[i] = new(*(uint*)(sim + 0x10), *(Vector3*)(sim + 0x40), *(Vector3*)(sim + 0x430));
        }
        return new(result);
    }

    public void Restore(nint module, int tailIndex)
    {
        var count = Count(module, out var begin);
        for (var i = 0; i < Math.Min(count, entries.Length); i++)
        {
            var sim = *(nint*)(begin + i * 8);
            if (sim == 0 || *(nint*)(sim + 0x18) != *(nint*)(module + 0x98)) continue;
            var entry = entries[i];
            var group = *(uint*)(sim + 0x10);
            var tailRemap = i == tailIndex && (entry.Group is 14 or 142) && (group is 14 or 142);
            if (group != entry.Group && !tailRemap) continue;
            if (ContactMath.Finite(entry.Input)) *(Vector3*)(sim + 0x40) = entry.Input;
            if (ContactMath.Finite(entry.Smoothed)) *(Vector3*)(sim + 0x430) = entry.Smoothed;
        }
    }
}
