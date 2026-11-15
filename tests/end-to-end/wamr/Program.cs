using System;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("Hello from C# on WAMR");
        Console.WriteLine("arguments=" + string.Join(",", args));
        int[] values = new int[] { 20, 22 };
        try
        {
            GC.Collect();
            int zero = args.Length;
            Console.WriteLine(values[0] + values[1]);
            Console.WriteLine(42 / zero);
        }
        catch (DivideByZeroException)
        {
            Console.WriteLine("caught");
        }
        finally
        {
            Console.WriteLine("finally");
        }
        return 0;
    }
}
