using System;
using System.Text;

namespace NetWasm.Compiler.ComponentModel.Wamr;

// Unlike the merger's index-only reader, lowering needs signed block types, strict
// integer widths, and independently bounded section/function readers.
internal ref struct WasmLegacyReader(ReadOnlySpan<byte> bytes)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public ReadOnlySpan<byte> Bytes { get; } = bytes;
    public int Position { get; private set; }
    public bool AtEnd => Position == Bytes.Length;

    public byte Byte() => Position < Bytes.Length
        ? Bytes[Position++]
        : throw Invalid("truncated binary");

    public ReadOnlySpan<byte> Take(uint length)
    {
        if (length > Bytes.Length - Position) throw Invalid("truncated binary");
        var result = Bytes.Slice(Position, (int)length);
        Position += (int)length;
        return result;
    }

    public uint Unsigned()
    {
        uint value = 0;
        for (var shift = 0; shift <= 28; shift += 7)
        {
            var next = Byte();
            if (shift == 28 && next > 15) throw Invalid("u32 LEB128 is out of range");
            value |= (uint)(next & 0x7f) << shift;
            if ((next & 0x80) == 0) return value;
        }
        throw Invalid("u32 LEB128 is out of range");
    }

    public long Signed(int bits)
    {
        ulong value = 0;
        for (var shift = 0; shift < bits; shift += 7)
        {
            var next = Byte();
            var payload = next & 0x7f;
            var remaining = bits - shift;
            if (remaining < 7)
            {
                var high = payload >> (remaining - 1);
                if ((next & 0x80) != 0 || (high != 0 && high != (1 << (8 - remaining)) - 1))
                    throw Invalid($"s{bits} LEB128 is out of range");
            }
            value |= (ulong)payload << shift;
            if ((next & 0x80) != 0) continue;
            if ((next & 0x40) != 0 && shift + 7 < 64)
                value |= ulong.MaxValue << (shift + 7);
            return unchecked((long)value);
        }
        throw Invalid($"s{bits} LEB128 is out of range");
    }

    public uint Count()
    {
        var count = Unsigned();
        if (count > Bytes.Length - Position) throw Invalid("vector extends past its enclosing binary");
        return count;
    }

    public ReadOnlySpan<byte> Name()
    {
        var name = Take(Unsigned());
        // GetCharCount validates without allocating an unbounded string.
        try { _ = Utf8.GetCharCount(name); }
        catch (DecoderFallbackException) { throw Invalid("name is not valid UTF-8"); }
        return name;
    }

    public void End()
    {
        if (!AtEnd) throw Invalid("trailing bytes in a section or function body");
    }

    public static Core.CompilerException Invalid(string message) =>
        ComponentException.Invalid($"WAMR legacy EH lowering: {message}");

    public static Core.CompilerException Unsupported(string shape) =>
        Invalid($"{shape} is not supported by the synchronous legacy EH command target");
}
