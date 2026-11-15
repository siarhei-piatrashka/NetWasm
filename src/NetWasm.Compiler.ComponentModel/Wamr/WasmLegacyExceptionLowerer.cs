using System;
using System.Diagnostics.CodeAnalysis;
using NetWasm.Compiler.ComponentModel.Merging;
using static NetWasm.Compiler.ComponentModel.Wamr.WasmLegacyReader;

namespace NetWasm.Compiler.ComponentModel.Wamr;

/// <summary>
/// Lowers branch-based exception handlers in a fully linked, validated wasm32 core
/// module to legacy try/catch for WAMR's EH-enabled classic interpreter.
/// </summary>
/// <remarks>
/// This is a binary transformation, not a Wasm type validator. It checks binary
/// bounds and control structure, but callers must validate the input module.
/// Exception references, GC, SIMD, threads, memory64, and stack switching are
/// rejected rather than approximated. No function or other module index changes.
/// Ordinary WAMR builds without exception handling cannot execute the result.
/// </remarks>
public sealed partial class WasmLegacyExceptionLowerer
{
    /// <summary>
    /// Returns an independent module with try_table lowered to legacy EH.
    /// Unsupported or malformed input raises a ComponentContract diagnostic.
    /// Unchanged sections (including custom sections) retain their original bytes,
    /// except that target_features entries advertising exnref are removed.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "The lowerer is an instance-based compiler service.")]
    public byte[] Lower(ReadOnlySpan<byte> module)
    {
        var reader = new WasmLegacyReader(module);
        if (!reader.Take(8).SequenceEqual("\0asm\x01\0\0\0"u8))
            throw Invalid("expected a version 1 core WebAssembly module");
        var output = new WasmMergeSink(module.Length);
        output.Write(module[..8]);
        var lastRank = 0;
        uint functions = 0, bodies = 0;
        while (!reader.AtEnd)
        {
            var start = reader.Position;
            var id = reader.Byte();
            var section = reader.Take(reader.Unsigned());
            if (id != 0)
            {
                var rank = Rank(id);
                if (rank <= lastRank) throw Invalid("duplicate or out-of-order core section");
                lastRank = rank;
            }
            var payload = new WasmLegacyReader(section);
            WasmMergeSink? replacement = null;
            switch (id)
            {
                case 0:
                    replacement = Custom(ref payload);
                    break;
                case 1:
                    for (var n = payload.Count(); n > 0; n--)
                    {
                        if (payload.Byte() != 0x60) throw Unsupported("GC or non-function type");
                        ValueTypes(ref payload);
                        ValueTypes(ref payload);
                    }
                    break;
                case 2:
                    Imports(ref payload);
                    break;
                case 3:
                    functions = payload.Count();
                    for (var n = functions; n > 0; n--) payload.Unsigned();
                    break;
                case 4:
                    for (var n = payload.Count(); n > 0; n--) Table(ref payload);
                    break;
                case 5:
                    for (var n = payload.Count(); n > 0; n--) Limits(ref payload, memory: true);
                    break;
                case 6:
                    for (var n = payload.Count(); n > 0; n--)
                    {
                        GlobalType(ref payload);
                        Constant(ref payload);
                    }
                    break;
                case 7:
                    for (var n = payload.Count(); n > 0; n--)
                    {
                        payload.Name();
                        if (payload.Byte() > 4) throw Invalid("invalid export kind");
                        payload.Unsigned();
                    }
                    break;
                case 8:
                case 12:
                    payload.Unsigned();
                    break;
                case 9:
                    Elements(ref payload);
                    break;
                case 10:
                    replacement = Code(ref payload, out bodies);
                    break;
                case 11:
                    Data(ref payload);
                    break;
                case 13:
                    for (var n = payload.Count(); n > 0; n--) Tag(ref payload);
                    break;
            }
            payload.End();
            if (replacement is not null && !replacement.Written.SequenceEqual(section))
                output.Section(id, replacement);
            else
                output.Write(module[start..reader.Position]);
        }
        if (functions != bodies) throw Invalid("function and code section counts differ");
        return output.ToArray();
    }

    private static int Rank(byte id) => id switch
    {
        >= 1 and <= 5 => id,
        13 => 6,
        >= 6 and <= 9 => id + 1,
        12 => 11,
        10 => 12,
        11 => 13,
        _ => throw Unsupported($"core section {id}"),
    };

    private static WasmMergeSink? Custom(ref WasmLegacyReader reader)
    {
        var name = reader.Name();
        if (!name.SequenceEqual("target_features"u8))
        {
            reader.Take((uint)(reader.Bytes.Length - reader.Position));
            return null;
        }
        var entries = new WasmMergeSink();
        uint kept = 0;
        var changed = false;
        for (var n = reader.Count(); n > 0; n--)
        {
            var start = reader.Position;
            if (reader.Byte() is not (0x2b or 0x2d)) throw Invalid("invalid target_features prefix");
            var feature = reader.Name();
            if (feature.SequenceEqual("exnref"u8))
            {
                changed = true;
                continue;
            }
            entries.Write(reader.Bytes[start..reader.Position]);
            kept++;
        }
        if (!changed) return null;
        var result = new WasmMergeSink();
        result.Name("target_features");
        result.Unsigned(kept);
        result.Write(entries.Written);
        return result;
    }

    private static void Imports(ref WasmLegacyReader reader)
    {
        for (var n = reader.Count(); n > 0; n--)
        {
            reader.Name();
            reader.Name();
            switch (reader.Byte())
            {
                case 0: reader.Unsigned(); break;
                case 1: Table(ref reader); break;
                case 2: Limits(ref reader, memory: true); break;
                case 3: GlobalType(ref reader); break;
                case 4: Tag(ref reader); break;
                default: throw Unsupported("import kind");
            }
        }
    }

    private static void ValueTypes(ref WasmLegacyReader reader)
    {
        for (var n = reader.Count(); n > 0; n--) ValueType(ref reader);
    }

    private static void ValueType(ref WasmLegacyReader reader)
    {
        var type = reader.Byte();
        if (type is 0x63 or 0x64)
        {
            HeapType(ref reader);
            throw Unsupported("typed references");
        }
        SimpleType(type);
    }

    private static void SimpleType(byte type)
    {
        if (type is 0x69 or 0x74) throw Unsupported("exnref/noexn reference types");
        if (type is >= 0x7b and <= 0x7f or 0x70 or 0x6f) return;
        throw Unsupported($"value type 0x{type:x2}");
    }

    private static void HeapType(ref WasmLegacyReader reader)
    {
        var heap = reader.Signed(33);
        if (heap is -0x17 or -0x0c) throw Unsupported("exnref/noexn reference types");
        if (heap is not (-0x10 or -0x11)) throw Unsupported("typed or GC heap references");
    }

    private static void BlockType(ref WasmLegacyReader reader)
    {
        var type = reader.Signed(33);
        if (type >= 0 || type == -0x40) return;
        if (type is -0x1d or -0x1c)
        {
            HeapType(ref reader);
            throw Unsupported("typed reference block type");
        }
        if (type < -0x40) throw Invalid("invalid block type");
        SimpleType((byte)(type & 0x7f));
    }

    private static void GlobalType(ref WasmLegacyReader reader)
    {
        ValueType(ref reader);
        if (reader.Byte() > 1) throw Invalid("invalid global mutability");
    }

    private static void Table(ref WasmLegacyReader reader)
    {
        var start = reader.Position;
        ValueType(ref reader);
        if (reader.Bytes[start] is not (0x70 or 0x6f)) throw Invalid("invalid table reference type");
        Limits(ref reader, memory: false);
    }

    private static void Limits(ref WasmLegacyReader reader, bool memory)
    {
        var flags = reader.Unsigned();
        if ((flags & 4) != 0) throw Unsupported("memory64/table64");
        if (flags > 1) throw Unsupported(memory ? "shared or custom-page memory" : "table limits");
        var minimum = reader.Unsigned();
        if ((flags & 1) != 0 && reader.Unsigned() < minimum) throw Invalid("maximum limit is below minimum");
    }

    private static void Tag(ref WasmLegacyReader reader)
    {
        if (reader.Byte() != 0) throw Invalid("invalid tag attribute");
        reader.Unsigned();
    }

    private static void Constant(ref WasmLegacyReader reader)
    {
        while (true)
        {
            var opcode = reader.Byte();
            switch (opcode)
            {
                case 0x0b: return;
                case 0x23:
                case 0xd2: reader.Unsigned(); break;
                case 0x41: reader.Signed(32); break;
                case 0x42: reader.Signed(64); break;
                case 0x43: reader.Take(4); break;
                case 0x44: reader.Take(8); break;
                case 0xd0: HeapType(ref reader); break;
                // Extended constant expressions.
                case 0x6a: case 0x6b: case 0x6c:
                case 0x7c: case 0x7d: case 0x7e: break;
                case 0x0a: throw Unsupported("throw_ref");
                default: throw Unsupported($"constant-expression opcode 0x{opcode:x2}");
            }
        }
    }

    private static void Elements(ref WasmLegacyReader reader)
    {
        for (var n = reader.Count(); n > 0; n--)
        {
            var flags = reader.Unsigned();
            if (flags > 7) throw Unsupported("element segment flags");
            if (flags is 2 or 6) reader.Unsigned();
            if (flags is 0 or 2 or 4 or 6) Constant(ref reader);
            if (flags is 1 or 2 or 3)
            {
                if (reader.Byte() != 0) throw Invalid("invalid element kind");
            }
            if (flags is 5 or 6 or 7) ValueType(ref reader);
            for (var count = reader.Count(); count > 0; count--)
            {
                if (flags < 4) reader.Unsigned();
                else Constant(ref reader);
            }
        }
    }

    private static void Data(ref WasmLegacyReader reader)
    {
        for (var n = reader.Count(); n > 0; n--)
        {
            var flags = reader.Unsigned();
            if (flags > 2) throw Invalid("invalid data segment flags");
            if (flags == 2) reader.Unsigned();
            if (flags != 1) Constant(ref reader);
            reader.Take(reader.Unsigned());
        }
    }
}
