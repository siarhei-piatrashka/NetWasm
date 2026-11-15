using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WebAssembly;
using System.Text;

[assembly: NetWasmPlatform(typeof(NetWasm.Wamr.WamrPlatform))]

namespace NetWasm.Wamr;

public sealed class WamrPlatform : NetWasmPlatform
{
    public override IStandardStreams StandardStreams => new WamrStreams();
    public override IProcessEnvironment ProcessEnvironment => new WamrEnvironment();
    public override IProcessExit ProcessExit => new WamrExit();
    public override IClock Clock => new WamrClock();
    public override IRandomSource Random => new WamrRandom();
}

internal static unsafe class Preview1
{
    internal const string Module = "wasi_snapshot_preview1";

    [DllImport(Module, EntryPoint = "fd_write"), WasmImportLinkage]
    internal static extern int Write(int descriptor, uint* vectors, int count, uint* written);

    [DllImport(Module, EntryPoint = "fd_read"), WasmImportLinkage]
    internal static extern int Read(int descriptor, uint* vectors, int count, uint* read);

    [DllImport(Module, EntryPoint = "fd_fdstat_get"), WasmImportLinkage]
    internal static extern int Stat(int descriptor, byte* status);

    [DllImport(Module, EntryPoint = "args_sizes_get"), WasmImportLinkage]
    internal static extern int ArgumentSizes(uint* count, uint* size);

    [DllImport(Module, EntryPoint = "args_get"), WasmImportLinkage]
    internal static extern int Arguments(uint* pointers, byte* buffer);

    [DllImport(Module, EntryPoint = "environ_sizes_get"), WasmImportLinkage]
    internal static extern int EnvironmentSizes(uint* count, uint* size);

    [DllImport(Module, EntryPoint = "environ_get"), WasmImportLinkage]
    internal static extern int Environment(uint* pointers, byte* buffer);

    [DllImport(Module, EntryPoint = "proc_exit"), WasmImportLinkage]
    internal static extern void Exit(int code);

    [DllImport(Module, EntryPoint = "clock_time_get"), WasmImportLinkage]
    internal static extern int Time(int clock, ulong precision, ulong* nanoseconds);

    [DllImport(Module, EntryPoint = "random_get"), WasmImportLinkage]
    internal static extern int Random(byte* buffer, uint size);

    internal static void Check(int error)
    {
        if (error != 0)
            throw new IOException("WASI Preview 1 error " + error);
    }

    internal static IOException InvalidResponse() => new("Invalid WASI Preview 1 host response.");
}

internal sealed unsafe class WamrStreams : IStandardStreams
{
    public void WriteOutput(ReadOnlySpan<byte> bytes) => Write(1, bytes);
    public void WriteError(ReadOnlySpan<byte> bytes) => Write(2, bytes);

    private static void Write(int descriptor, ReadOnlySpan<byte> bytes)
    {
        fixed (byte* start = bytes)
        {
            uint* vector = stackalloc uint[2];
            uint remaining = (uint)bytes.Length;
            byte* position = start;
            while (remaining != 0)
            {
                vector[0] = (uint)position;
                vector[1] = remaining;
                uint written;
                Preview1.Check(Preview1.Write(descriptor, vector, 1, &written));
                if (written == 0 || written > remaining)
                    throw Preview1.InvalidResponse();
                remaining -= written;
                position += written;
            }
        }
    }

    public int ReadInput(Span<byte> destination)
    {
        if (destination.IsEmpty)
            return 0;
        fixed (byte* pointer = destination)
        {
            uint* vector = stackalloc uint[2];
            vector[0] = (uint)pointer;
            vector[1] = (uint)destination.Length;
            uint read;
            Preview1.Check(Preview1.Read(0, vector, 1, &read));
            if (read > (uint)destination.Length)
                throw Preview1.InvalidResponse();
            return (int)read;
        }
    }

    public bool IsTerminal(StandardStream stream)
    {
        int descriptor = stream switch
        {
            StandardStream.Input => 0,
            StandardStream.Output => 1,
            StandardStream.Error => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(stream)),
        };
        byte* status = stackalloc byte[24];
        Preview1.Check(Preview1.Stat(descriptor, status));
        return status[0] == 2;
    }
}

internal sealed unsafe class WamrEnvironment : IProcessEnvironment
{
    public string[] GetArguments() => ReadStrings(arguments: true);

    public KeyValuePair<string, string>[] GetVariables()
    {
        string[] entries = ReadStrings(arguments: false);
        var result = new KeyValuePair<string, string>[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            int separator = entries[i].IndexOf('=');
            if (separator <= 0)
                throw Preview1.InvalidResponse();
            result[i] = new(entries[i].Substring(0, separator), entries[i].Substring(separator + 1));
        }
        return result;
    }

    public string? GetInitialDirectory() => null;

    private static string[] ReadStrings(bool arguments)
    {
        uint count;
        uint size;
        Preview1.Check(arguments
            ? Preview1.ArgumentSizes(&count, &size)
            : Preview1.EnvironmentSizes(&count, &size));
        var pointers = new uint[checked((int)count)];
        var bytes = new byte[checked((int)size)];
        var strings = new string[pointers.Length];
        fixed (uint* addresses = pointers)
        fixed (byte* buffer = bytes)
        {
            Preview1.Check(arguments
                ? Preview1.Arguments(addresses, buffer)
                : Preview1.Environment(addresses, buffer));
            for (int i = 0; i < pointers.Length; i++)
            {
                uint offset = pointers[i] - (uint)buffer;
                if (offset >= size)
                    throw Preview1.InvalidResponse();
                int start = (int)offset;
                int end = start;
                while (end < bytes.Length && bytes[end] != 0)
                    end++;
                if (end == bytes.Length)
                    throw Preview1.InvalidResponse();
                strings[i] = Encoding.UTF8.GetString(bytes, start, end - start);
            }
        }
        return strings;
    }
}

internal sealed class WamrExit : IProcessExit
{
    public void Exit(int exitCode)
    {
        Preview1.Exit(exitCode);
        throw new InvalidOperationException("WASI proc_exit returned.");
    }
}

internal sealed unsafe class WamrClock : IClock
{
    public DateTime GetUtcNow()
    {
        ulong nanoseconds;
        Preview1.Check(Preview1.Time(0, 1, &nanoseconds));
        return DateTime.UnixEpoch.AddTicks(checked((long)(nanoseconds / 100)));
    }

    public long GetTimestamp()
    {
        ulong nanoseconds;
        Preview1.Check(Preview1.Time(1, 1, &nanoseconds));
        return checked((long)nanoseconds);
    }
}

internal sealed unsafe class WamrRandom : IRandomSource
{
    public void Fill(Span<byte> destination)
    {
        fixed (byte* pointer = destination)
            Preview1.Check(Preview1.Random(pointer, (uint)destination.Length));
    }
}
