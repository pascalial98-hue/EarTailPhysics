namespace EarTailPhysics;

// Validate from a live callback object. Cached addresses are identity tokens only.
public static unsafe class RuntimeBinding
{
    public static bool OwnsModule(nint module, nint localDraw, out nint skeleton)
    {
        skeleton = 0;
        if (module == 0 || localDraw == 0) return false;
        var liveSkeleton = *(nint*)(module + 0x98);
        if (liveSkeleton == 0 || *(nint*)(liveSkeleton + 0xB8) != localDraw) return false;
        skeleton = liveSkeleton;
        return true;
    }

    public static nint ModuleForDraw(nint draw, nint localDraw)
    {
        // Reject another character before dereferencing it.
        if (draw == 0 || draw != localDraw) return 0;
        var module = *(nint*)(draw + 0x158);
        return OwnsModule(module, localDraw, out _) ? module : 0;
    }

    public static nint TailChain(nint module, int simulatorIndex, int chainIndex, out nint simulator)
    {
        simulator = 0;
        if (module == 0 || simulatorIndex < 0 || chainIndex < 0) return 0;
        var begin = *(nint*)(module + 0xA0);
        var end = *(nint*)(module + 0xA8);
        if (begin == 0 || end < begin || (end - begin) % 8 != 0 || (end - begin) / 8 > 97
            || simulatorIndex >= (end - begin) / 8) return 0;
        var sim = *(nint*)(begin + simulatorIndex * 8);
        if (sim == 0 || *(nint*)(sim + 0x18) != *(nint*)(module + 0x98)) return 0;
        var chains = *(nint*)(sim + 0x78);
        var chainEnd = *(nint*)(sim + 0x80);
        if (chains == 0 || chainEnd < chains || (chainEnd - chains) % 8 != 0 || (chainEnd - chains) / 8 > 64
            || chainIndex >= (chainEnd - chains) / 8) return 0;
        var chain = *(nint*)(chains + chainIndex * 8);
        if (chain == 0 || *(nint*)(chain + 0x10) != *(nint*)(module + 0x98)
            || *(ushort*)(chain + 0xE8) != 5) return 0;
        simulator = sim;
        return chain;
    }
}
