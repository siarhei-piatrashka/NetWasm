using System.Diagnostics;
using System.Text;
using NetWasm.Compiler.ComponentModel.Wamr;
using NetWasm.Compiler.Core;

namespace NetWasm.Compiler.ComponentModel.Tests.Wamr;

public sealed class WasmLegacyExceptionLowererTests
{
    private static readonly WasmLegacyExceptionLowerer Lowerer = new();

    public static TheoryData<string, string, int, int> Handlers => new()
    {
        {
            """
            block (result i32)
              try_table (result i32) (catch 0 0)
                local.get 0
                if
                  i32.const 42
                  throw 0
                end
                i32.const 7
              end
            end
            """,
            """
            block (result i32)
              try (result i32)
                local.get 0
                if
                  i32.const 42
                  throw 0
                end
                i32.const 7
              catch 0
                br 1
              end
            end
            """, 7, 42
        },
        {
            """
            block (result i32)
              try_table (result i32) (catch 0 0)
                block (result i32)
                  try_table (result i32) (catch 1 0)
                    local.get 0
                    if
                      i32.const 42
                      throw 0
                    end
                    i32.const 23
                    throw 1
                  end
                end
              end
            end
            """,
            """
            block (result i32)
              try (result i32)
                block (result i32)
                  try (result i32)
                    local.get 0
                    if
                      i32.const 42
                      throw 0
                    end
                    i32.const 23
                    throw 1
                  catch 1
                    br 1
                  end
                end
              catch 0
                br 1
              end
            end
            """, 23, 42
        },
        {
            """
            block (result i32)
              block
                try_table (catch 0 1) (catch 1 1) (catch_all 0)
                  local.get 0
                  if
                    i32.const 11
                    throw 0
                  end
                  i32.const 22
                  throw 1
                end
              end
              i32.const 99
            end
            """,
            """
            block (result i32)
              block
                try
                  local.get 0
                  if
                    i32.const 11
                    throw 0
                  end
                  i32.const 22
                  throw 1
                catch 0
                  br 2
                catch 1
                  br 2
                catch_all
                  br 1
                end
              end
              i32.const 99
            end
            """, 22, 11
        },
        { "try_table (result i32) i32.const 7 end", "block (result i32) i32.const 7 end", 7, 7 },
        {
            """
            block (result i32)
              i32.const 5
              try_table (param i32) (result i32) (catch 0 0)
                i32.const 1
                i32.add
              end
            end
            """,
            """
            block (result i32)
              i32.const 5
              try (param i32) (result i32)
                i32.const 1
                i32.add
              catch 0
                br 1
              end
            end
            """, 6, 6
        },
        {
            """
            block (result i32)
              try_table (result i32) (catch 0 0)
                block (result i32)
                  i32.const 17
                  br 1
                end
              end
            end
            """,
            """
            block (result i32)
              try (result i32)
                block (result i32)
                  i32.const 17
                  br 1
                end
              catch 0
                br 1
              end
            end
            """, 17, 17
        },
        {
            """
            block
              try_table (catch_all 0) (catch_all 0)
                throw 2
              end
            end
            i32.const 99
            """,
            """
            block
              try
                throw 2
              catch_all
                br 1
              end
            end
            i32.const 99
            """, 99, 99
        },
        {
            """
            block
              try_table (catch_all 0)
                local.get 0
                if
                  throw 2
                end
              end
            end
            i32.const 99
            """,
            """
            block
              try
                local.get 0
                if
                  throw 2
                end
              catch_all
                br 1
              end
            end
            i32.const 99
            """, 99, 99
        },
        {
            """
            block (result i32)
              block
                try_table (catch_all 0) (catch 0 1)
                  i32.const 13
                  throw 0
                end
              end
              i32.const 99
            end
            """,
            """
            block (result i32)
              block
                try
                  i32.const 13
                  throw 0
                catch_all
                  br 1
                end
              end
              i32.const 99
            end
            """, 99, 99
        },
        {
            """
            block (result i32)
              block (result i32)
                try_table (catch 0 0) (catch 0 1)
                  i32.const 42
                  throw 0
                end
                unreachable
              end
              i32.const 1
              i32.add
            end
            """,
            """
            block (result i32)
              block (result i32)
                try
                  i32.const 42
                  throw 0
                catch 0
                  br 1
                catch 0
                  br 2
                end
                unreachable
              end
              i32.const 1
              i32.add
            end
            """, 43, 43
        },
        {
            """
            block
              try_table (result i32 i64) (catch_all 0)
                i32.const 17
                i64.const 23
              end
              drop
              return
            end
            i32.const 99
            """,
            """
            block
              try (result i32 i64)
                i32.const 17
                i64.const 23
              catch_all
                br 1
              end
              drop
              return
            end
            i32.const 99
            """, 17, 17
        },
    };

    [Theory]
    [MemberData(nameof(Handlers))]
    public void LowersHandlersWithoutMovingOriginalLabelsOrResults(
        string source, string expected, int result0, int result1)
    {
        using var tools = new WatFiles();
        var original = tools.Parse(Module(source), "source");
        tools.Validate(original, "original");

        var lowered = Lowerer.Lower(original);

        Assert.Equal(tools.Parse(Module(expected), "expected"), lowered);
        tools.Validate(lowered, "lowered");
        tools.Execute(lowered, result0, result1);
        Assert.Equal(lowered, Lowerer.Lower(lowered));
    }

    [Fact]
    public void WidensCatchBranchDepthAndFunctionAndSectionSizes()
    {
        using var tools = new WatFiles();
        var blocks = string.Concat(Enumerable.Repeat("block ", 128));
        var ends = string.Concat(Enumerable.Repeat("end ", 128));
        var original = tools.Parse($"(module (func {blocks}try_table (catch_all 127) nop end {ends}))");
        var expected = tools.Parse($"(module (func {blocks}try nop catch_all br 128 end {ends}))", "expected");

        var lowered = Lowerer.Lower(original);

        Assert.Equal(expected, lowered);
        tools.Validate(lowered, "lowered");
    }

    [Fact]
    public void LeavesSimpleModuleAndOpaqueBytesUnchanged()
    {
        using var tools = new WatFiles();
        var original = tools.Parse("""
            (module
              (memory 1)
              (table 1 funcref)
              (global i64 (i64.const -1))
              (data (i32.const 0) "\1f\0a\69\63")
              (elem (i32.const 0) func 0)
              (func (local i32 i64)
                i32.const 31 drop
                i64.const -2147483648 drop
                f32.const 31 drop
                f64.const -31 drop
                i32.const 0 i32.load drop
                i32.const 0 i32.const 0 i32.const 1 memory.copy))
            """);
        original = [.. original, .. Section(0, [0x01, (byte)'x', 0x1f, 0x0a, 0x69])];
        tools.Validate(original, "original");

        Assert.Equal(original, Lowerer.Lower(original));
    }

    [Fact]
    public void RemovesOnlyExnrefFeatureAndPreservesCustomSectionsAndNoncanonicalLengths()
    {
        byte[] opaque = [0, 0x85, 0, 1, (byte)'x', 0x69, 0x1f, 0x0a];
        var before = Section(0, FeaturePayload(("+", "exception-handling"), ("+", "exnref"), ("+", "bulk-memory")));
        var after = Section(0, FeaturePayload(("+", "exception-handling"), ("+", "bulk-memory")));
        var module = BinaryModule([.. opaque, .. before]);

        Assert.Equal(BinaryModule([.. opaque, .. after]), Lowerer.Lower(module));
    }

    [Theory]
    [InlineData("unreachable throw_ref", "throw_ref")]
    [InlineData("block try_table (catch_ref 0 0) nop end end", "catch_ref")]
    [InlineData("block try_table (catch_all_ref 0) nop end end", "catch_all_ref")]
    [InlineData("ref.null exn drop", "exnref")]
    [InlineData("ref.null noexn drop", "exnref")]
    [InlineData("v128.const i32x4 0 0 0 0 drop", "SIMD")]
    public void RejectsUnsupportedInstructionsInsteadOfWeakeningSemantics(string body, string diagnostic)
    {
        using var tools = new WatFiles();
        var module = tools.Parse($"(module (tag) (func {body}))");

        var error = Assert.Throws<CompilerException>(() => Lowerer.Lower(module));

        Assert.Equal(DiagnosticCode.ComponentContract, error.Diagnostic.Code);
        Assert.Contains(diagnostic, error.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("(func (param exnref))")]
    [InlineData("(func (local exnref))")]
    [InlineData("(func (local (ref null exn)))")]
    [InlineData("(global exnref (ref.null exn))")]
    [InlineData("(import \"h\" \"g\" (global exnref))")]
    [InlineData("(table 1 exnref)")]
    public void RejectsExceptionReferenceTypesOutsideTryTable(string definition)
    {
        using var tools = new WatFiles();
        var module = tools.Parse($"(module {definition})");

        var error = Assert.Throws<CompilerException>(() => Lowerer.Lower(module));

        Assert.Contains("exnref", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    public static TheoryData<byte[]> Malformed => new()
    {
        new byte[] { 0, 97 },
        BinaryModule([10, 0xff, 0xff, 0xff, 0xff, 0x10]),
        BinaryModule(Section(10, [1, 5, 0, 0x0b])),
        Function([0, 0x02, 0x40, 0x0b]), // Missing function end.
        Function([0, 0x05, 0x0b]), // Else without if.
        Function([0, 0x0b, 0x01]), // Bytes after function end.
        Function([0, 0x41, 0x80, 0x80, 0x80, 0x80, 0x10, 0x1a, 0x0b]),
        Function([0, 0x1f, 0x40, 1, 2, 1, 0x0b, 0x0b]), // Catch outside function.
        Function([0, 0x1f, 0x40, 1, 4, 0, 0x0b, 0x0b]), // Unknown catch kind.
        Function([0, 0x1f, 0x40, 2, 2, 0]), // Truncated clause vector.
        BinaryModule([.. Section(3, [0]), .. Section(3, [0])]),
        BinaryModule(Section(3, [1, 0])), // Missing code.
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void ReportsMalformedInputAsComponentDiagnostic(byte[] module)
    {
        var error = Assert.Throws<CompilerException>(() => Lowerer.Lower(module));

        Assert.Equal(DiagnosticCode.ComponentContract, error.Diagnostic.Code);
        Assert.Contains("WAMR legacy EH lowering", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    private static string Module(string body) => $$"""
        (module
          (tag (param i32))
          (tag (param i32))
          (tag)
          (func (export "run") (param i32) (result i32)
            {{body}}))
        """;

    private static byte[] Function(byte[] body) => BinaryModule([
        .. Section(1, [1, 0x60, 0, 0]),
        .. Section(3, [1, 0]),
        .. Section(10, [1, .. U((uint)body.Length), .. body]),
    ]);

    private static byte[] BinaryModule(byte[] sections) => [0, 97, 115, 109, 1, 0, 0, 0, .. sections];
    private static byte[] Section(byte id, byte[] payload) => [id, .. U((uint)payload.Length), .. payload];
    private static byte[] Name(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return [.. U((uint)bytes.Length), .. bytes];
    }

    private static byte[] FeaturePayload(params (string Prefix, string Name)[] features) =>
        [.. Name("target_features"), .. U((uint)features.Length),
            .. features.SelectMany(f => new byte[] { (byte)f.Prefix[0] }.Concat(Name(f.Name)))];

    private static byte[] U(uint value)
    {
        var bytes = new List<byte>();
        do
        {
            var next = (byte)(value & 0x7f);
            value >>= 7;
            bytes.Add(value == 0 ? next : (byte)(next | 0x80));
        } while (value != 0);
        return [.. bytes];
    }

    private sealed class WatFiles : IDisposable
    {
        private readonly string _directory = Path.Combine("WamrTestArtifacts", Guid.NewGuid().ToString("N"));

        public WatFiles() => Directory.CreateDirectory(_directory);

        public byte[] Parse(string wat, string name = "input")
        {
            var source = Path.Combine(_directory, name + ".wat");
            var binary = Path.Combine(_directory, name + ".wasm");
            File.WriteAllText(source, wat);
            Run("parse", source, "-o", binary);
            return File.ReadAllBytes(binary);
        }

        public void Validate(byte[] module, string name)
        {
            var path = Path.Combine(_directory, name + ".wasm");
            File.WriteAllBytes(path, module);
            Run("validate", "--features", "all", path);
        }

        public void Execute(byte[] module, int result0, int result1)
        {
            var path = Path.Combine(_directory, "execute.wasm");
            File.WriteAllBytes(path, module);
            RunProcess(Environment.GetEnvironmentVariable("NETWASM_NODE_PATH") ?? "node",
                "-e", """
                const assert = require('node:assert/strict');
                const fs = require('node:fs');
                const module = new WebAssembly.Module(fs.readFileSync(process.argv[1]));
                const instance = new WebAssembly.Instance(module);
                assert.equal(instance.exports.run(0), Number(process.argv[2]));
                assert.equal(instance.exports.run(1), Number(process.argv[3]));
                """, path,
                result0.ToString(System.Globalization.CultureInfo.InvariantCulture),
                result1.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void Run(params string[] args) =>
            RunProcess(Environment.GetEnvironmentVariable("NETWASM_WASM_TOOLS_PATH") ?? "wasm-tools", args);

        private static void RunProcess(string executable, params string[] args)
        {
            using var process = Process.Start(new ProcessStartInfo(executable, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail($"{executable} did not finish in 30 seconds");
            }
            Assert.True(process.ExitCode == 0, $"{output.Result}\n{error.Result}");
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
