using System;
using System.Runtime.InteropServices;

internal static unsafe class Program
{
    [DllImport("esp", EntryPoint = "gpio_set_level"), WasmImportLinkage]
    private static extern int GpioSetLevel(int pin, int level);

    private static delegate* unmanaged<int, int> _callback;
    private static int _counter;

    private static int Main() { _counter = 100; return 0; }

    [UnmanagedCallersOnly(EntryPoint = "add")]
    public static int Add(int a, int b) => a + b;

    [UnmanagedCallersOnly(EntryPoint = "blink")]
    public static int Blink(int pin)
    {
        _counter++;
        return GpioSetLevel(pin, _counter & 1);
    }

    [UnmanagedCallersOnly(EntryPoint = "set_callback")]
    public static void SetCallback(nint cb) => _callback = (delegate* unmanaged<int, int>)cb;

    [UnmanagedCallersOnly(EntryPoint = "fire")]
    public static int Fire(int v) => _callback(v) + 1;

    [UnmanagedCallersOnly]
    private static int OnEvent(int v) => v * 2;

    [UnmanagedCallersOnly(EntryPoint = "get_handler")]
    public static nint GetHandler() => (nint)(delegate* unmanaged<int, int>)&OnEvent;

    [UnmanagedCallersOnly(EntryPoint = "divide")]
    public static int Divide(int a, int b)
    {
        try { return a / b; }
        catch (DivideByZeroException) { return -1; }
    }
}
