using System;
using System.Collections.Generic;
using NetWasm.Compiler.ComponentModel.Merging;
using static NetWasm.Compiler.ComponentModel.Wamr.WasmLegacyReader;

namespace NetWasm.Compiler.ComponentModel.Wamr;

public sealed partial class WasmLegacyExceptionLowerer
{
    private readonly record struct Handler(bool All, uint Tag, uint Label);

    private sealed class Control(byte opcode, List<Handler>? handlers = null)
    {
        public byte Opcode { get; } = opcode;
        public List<Handler>? Handlers { get; } = handlers;
        public bool HasElse { get; set; }
        public bool InCatch { get; set; }
        public bool CatchAll { get; set; }
    }

    private static WasmMergeSink Code(ref WasmLegacyReader reader, out uint bodies)
    {
        var result = new WasmMergeSink(reader.Bytes.Length);
        var countStart = reader.Position;
        bodies = reader.Count();
        result.Write(reader.Bytes[countStart..reader.Position]);
        for (var n = bodies; n > 0; n--)
        {
            var start = reader.Position;
            var bytes = reader.Take(reader.Unsigned());
            var body = new WasmLegacyReader(bytes);
            var lowered = Body(ref body);
            body.End();
            if (lowered.Written.SequenceEqual(bytes))
                result.Write(reader.Bytes[start..reader.Position]);
            else
            {
                result.Unsigned((uint)lowered.Length);
                result.Write(lowered.Written);
            }
        }
        return result;
    }

    private static WasmMergeSink Body(ref WasmLegacyReader reader)
    {
        var result = new WasmMergeSink(reader.Bytes.Length);
        ulong locals = 0;
        for (var n = reader.Count(); n > 0; n--)
        {
            locals += reader.Unsigned();
            if (locals > uint.MaxValue) throw Invalid("too many function locals");
            ValueType(ref reader);
        }
        result.Write(reader.Bytes[..reader.Position]);
        var controls = new List<Control> { new(0xff) }; // Implicit function label.
        while (!reader.AtEnd)
        {
            var start = reader.Position;
            var opcode = reader.Byte();
            var top = controls[^1];
            switch (opcode)
            {
                case 0x02: // block
                case 0x03: // loop
                case 0x04: // if
                case 0x06: // legacy try (also permits lowering to be idempotent)
                    BlockType(ref reader);
                    controls.Add(new(opcode));
                    break;
                case 0x1f:
                {
                    var typeStart = reader.Position;
                    BlockType(ref reader);
                    var typeEnd = reader.Position;
                    var handlers = new List<Handler>();
                    var catchAll = false;
                    for (var n = reader.Count(); n > 0; n--)
                    {
                        var kind = reader.Byte();
                        if (kind is 1 or 3) throw Unsupported(kind == 1 ? "catch_ref" : "catch_all_ref");
                        if (kind is not (0 or 2)) throw Invalid($"invalid try_table catch kind {kind}");
                        var tag = kind == 0 ? reader.Unsigned() : 0;
                        var label = Label(ref reader, controls.Count);
                        // Standard EH permits clauses after catch_all. They can never
                        // match; legacy EH requires catch_all to be the final clause.
                        if (!catchAll) handlers.Add(new(kind == 2, tag, label));
                        catchAll |= kind == 2;
                    }
                    // This keeps exactly one control label, so branches in the original
                    // body, including branches to the try_table itself, do not move.
                    result.Byte(handlers.Count == 0 ? (byte)0x02 : (byte)0x06);
                    result.Write(reader.Bytes[typeStart..typeEnd]);
                    controls.Add(new(0x1f, handlers));
                    continue;
                }
                case 0x05:
                    if (top.Opcode != 0x04 || top.HasElse) throw Invalid("else without matching if");
                    top.HasElse = true;
                    break;
                case 0x07:
                case 0x19:
                    if (top.Opcode != 0x06 || top.CatchAll) throw Invalid("catch without matching legacy try");
                    if (opcode == 0x07) reader.Unsigned();
                    top.InCatch = true;
                    top.CatchAll = opcode == 0x19;
                    break;
                case 0x0b:
                    if (top.Handlers is { } catches)
                    {
                        foreach (var handler in catches)
                        {
                            result.Byte(handler.All ? (byte)0x19 : (byte)0x07);
                            if (!handler.All) result.Unsigned(handler.Tag);
                            result.Byte(0x0c);
                            // try_table catch targets are resolved outside its own label.
                            // The legacy catch is inside the replacement try.
                            result.Unsigned(handler.Label + 1);
                        }
                    }
                    else if (top.Opcode == 0x06 && !top.InCatch)
                        throw Invalid("legacy try has no handler");
                    controls.RemoveAt(controls.Count - 1);
                    if (controls.Count == 0)
                    {
                        result.Byte(opcode);
                        reader.End();
                        return result;
                    }
                    break;
                case 0x0c:
                case 0x0d:
                    Label(ref reader, controls.Count);
                    break;
                case 0x0e:
                    for (var n = reader.Count(); n > 0; n--) Label(ref reader, controls.Count);
                    Label(ref reader, controls.Count);
                    break;
                case 0x09:
                {
                    var label = Label(ref reader, controls.Count);
                    if (!controls[controls.Count - 1 - (int)label].InCatch)
                        throw Invalid("rethrow does not target a legacy catch");
                    break;
                }
                case 0x18:
                    throw Unsupported("legacy delegate");
                default:
                    Immediate(ref reader, opcode);
                    break;
            }
            result.Write(reader.Bytes[start..reader.Position]);
        }
        throw Invalid("function body is missing an end instruction");
    }

    private static uint Label(ref WasmLegacyReader reader, int count)
    {
        var label = reader.Unsigned();
        if (label >= count) throw Invalid($"branch label {label} is outside the enclosing control stack");
        return label;
    }

    // Immediate layouts follow Merging/WasmExpressionRemapper and
    // Compiler.Wasm/Units/WasmIndexOperandScanner. This decoder deliberately bounds
    // signed constants to their actual widths and fails closed on other proposals.
    private static void Immediate(ref WasmLegacyReader reader, byte opcode)
    {
        switch (opcode)
        {
            case 0x00: case 0x01: case 0x0f: case 0x1a: case 0x1b:
            case 0xd1:
                return;
            case 0x08: // throw
            case 0x10: case 0x12: // call, return_call
            case >= 0x20 and <= 0x26:
            case 0x3f: case 0x40:
            case 0xd2:
                reader.Unsigned();
                return;
            case 0x11: case 0x13: // (return_)call_indirect
                reader.Unsigned();
                reader.Unsigned();
                return;
            case 0x1c:
                ValueTypes(ref reader);
                return;
            case >= 0x28 and <= 0x3e:
                MemoryArgument(ref reader);
                return;
            case 0x41: reader.Signed(32); return;
            case 0x42: reader.Signed(64); return;
            case 0x43: reader.Take(4); return;
            case 0x44: reader.Take(8); return;
            case >= 0x45 and <= 0xc4:
                return;
            case 0xd0: HeapType(ref reader); return;
            case 0xfc: Miscellaneous(ref reader); return;
            case 0x0a: throw Unsupported("throw_ref");
            case 0xfd: throw Unsupported("SIMD instructions");
            case 0xfe: throw Unsupported("atomic/thread instructions");
            default: throw Unsupported($"opcode 0x{opcode:x2}");
        }
    }

    private static void MemoryArgument(ref WasmLegacyReader reader)
    {
        var alignment = reader.Unsigned();
        if (alignment > 0x7f) throw Invalid("invalid memory alignment flags");
        if ((alignment & 0x40) != 0) reader.Unsigned();
        reader.Unsigned(); // wasm32 offset
    }

    private static void Miscellaneous(ref WasmLegacyReader reader)
    {
        var opcode = reader.Unsigned();
        switch (opcode)
        {
            case <= 7: return; // Saturating conversions.
            case 8: case 10: case 12: case 14: // memory/table init/copy
                reader.Unsigned();
                reader.Unsigned();
                return;
            case 9: case 11: case 13: case 15: case 16: case 17:
                reader.Unsigned();
                return;
            default: throw Unsupported($"0xfc subopcode {opcode}");
        }
    }
}
