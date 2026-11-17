using System.Runtime.InteropServices;
using System.Text;

namespace EarTailPhysics;

// Private, bounded copies of parsed resource descriptors, not game-owned handles.
public unsafe sealed class NativeMemoryView : IDisposable
{
    private readonly List<nint> blocks = [];
    public byte* Allocate(int size)
    {
        if (size is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(size));
        var p = (byte*)NativeMemory.AllocZeroed((nuint)size);
        if (p == null) throw new OutOfMemoryException();
        blocks.Add((nint)p);
        return p;
    }
    public byte* Copy(byte* from, int length)
    {
        if (from == null) throw new InvalidDataException("Null native descriptor");
        var p = Allocate(length);
        new ReadOnlySpan<byte>(from, length).CopyTo(new Span<byte>(p, length));
        return p;
    }
    public static string Name(byte* p)
    {
        if (p == null) return "";
        var span = new ReadOnlySpan<byte>(p, 32);
        var end = span.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("Unterminated bone name");
        return Encoding.ASCII.GetString(span[..end]);
    }
    public static void SetName(byte* p, string name)
    {
        if (name.Length > 31) throw new ArgumentOutOfRangeException(nameof(name));
        new Span<byte>(p, 32).Clear();
        Encoding.ASCII.GetBytes(name, new Span<byte>(p, 31));
    }
    public void Dispose()
    {
        foreach (var p in blocks) NativeMemory.Free((void*)p);
        blocks.Clear();
    }
}
