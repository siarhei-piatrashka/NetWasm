// Copyright (c) 2026 Zion Sati and NetWasm contributors (https://github.com/zion-sati/NetWasm).
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using NetWasm.TestInfrastructure;

namespace NetWasm.Runtime.Pack.Tests.Integration;

public sealed class SimdProjectIntegrationTests
{
    [Theory]
    [InlineData("wasm32", "O0", true)]
    [InlineData("wasm32", "O3", false)]
    [InlineData("wasm64", "O0", false)]
    [InlineData("wasm64", "O3", false)]
    [Trait("Category", "NativeSimdIntegration")]
    public async Task SameProjectRunsWithAndWithoutSimd(string target, string optimization, bool toggleBack)
    {
        using var assets = TestAssets.Create();
        var hostTools = Path.Combine(RequiredDirectory("NETWASM_NATIVE_INTEROP_HOST_TOOLS_ROOT"), "tools", "bin");
        var toolchain = Path.Combine(RequiredDirectory("NETWASM_NATIVE_INTEROP_TOOLCHAIN_ROOT"), "tools");
        var runtimeAssets = Environment.GetEnvironmentVariable("NETWASM_NATIVE_INTEROP_RUNTIME_ASSETS_ROOT")
            ?? Path.Combine(assets.Root, "src", "NetWasm.Runtime.Pack", "runtime");
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var node = Path.Combine(hostTools, "node" + suffix);
        var compiler = Path.Combine(assets.Root, "src", "NetWasm.Compiler.Cli", "bin", "Release", "net10.0", "nwc" + suffix);
        var taskOutput = Path.Combine(assets.Root, "src", "NetWasm.Compiler.Tasks", "bin", "Release", "net10.0");
        var runtimePackAssembly = Path.Combine(assets.Root, "src", "NetWasm.Runtime.Pack", "bin", "Release", "net10.0",
            "NetWasm.Runtime.Pack.dll");
        Assert.True(File.Exists(compiler), $"Build the Release compiler before running this test: {compiler}");
        Assert.True(Directory.Exists(taskOutput), $"Build the Release compiler tasks before running this test: {taskOutput}");
        Assert.True(File.Exists(runtimePackAssembly), $"Build the Release runtime pack before running this test: {runtimePackAssembly}");
        var fixture = Path.Combine(AppContext.BaseDirectory, "IntegrationAssets");
        File.Copy(Path.Combine(fixture, "SimdMath.csproj.txt"), Path.Combine(assets.Directory, "Math.csproj"));
        File.Copy(Path.Combine(fixture, "SimdMath.cs.txt"), Path.Combine(assets.Directory, "Math.cs"));
        var taskDirectory = Path.Combine(assets.Directory, "tasks", "net10.0");
        var targetsDirectory = Path.Combine(assets.Directory, "buildTransitive");
        Directory.CreateDirectory(taskDirectory);
        Directory.CreateDirectory(targetsDirectory);
        foreach (var file in Directory.EnumerateFiles(taskOutput))
            File.Copy(file, Path.Combine(taskDirectory, Path.GetFileName(file)));
        File.Copy(Path.Combine(assets.Root, "src", "NetWasm.Compiler.Tasks", "buildTransitive",
            "NetWasm.Compiler.Tasks.targets"), Path.Combine(targetsDirectory, "NetWasm.Compiler.Tasks.targets"));

        foreach (var simd in toggleBack ? new[] { true, false, true } : new[] { true, false })
        {
            var build = await Run(DotNetHost.Executable, assets.Directory,
                "build", "Math.csproj", "-c", "Release", "-t:Build;NetWasmCompilerTasksLinkRawModule",
                $"-p:Repository={assets.Root}", $"-p:CoreLib={assets.CoreLib}", $"-p:Compiler={compiler}",
                $"-p:RuntimeAssets={runtimeAssets}", $"-p:RuntimePackAssembly={runtimePackAssembly}",
                $"-p:HostTools={hostTools}", $"-p:HostExeSuffix={suffix}", $"-p:Toolchain={toolchain}",
                $"-p:NetWasmTarget={target}", $"-p:NetWasmOptimization={optimization}",
                $"-p:NetWasmSimd={simd.ToString().ToLowerInvariant()}", "--nologo", "-v:minimal");
            Assert.True(build.ExitCode == 0, build.Output);
            Assert.Contains($"simd={simd.ToString().ToLowerInvariant()}",
                File.ReadAllLines(Path.Combine(assets.Directory, "intermediate", target, "packaging.identity")));
            File.Copy(Path.Combine(assets.Directory, "final.wasm"),
                Path.Combine(assets.Directory, simd ? "simd.wasm" : "scalar.wasm"), overwrite: true);
        }

        var wasmTools = Path.Combine(toolchain, "wasm-tools", "run-wasm-tools.mjs");
        var wasmToolsModule = Path.Combine(toolchain, "wasm-tools", "wasm-tools.wasm");
        var scalar = Path.Combine(assets.Directory, "scalar.wasm");
        var vector = Path.Combine(assets.Directory, "simd.wasm");
        var scalarValidation = await Run(node, assets.Directory, wasmTools, wasmToolsModule,
            "validate", "--features=-simd,-relaxed-simd", scalar);
        Assert.True(scalarValidation.ExitCode == 0, scalarValidation.Output);
        var vectorValidation = await Run(node, assets.Directory, wasmTools, wasmToolsModule, "validate", vector);
        Assert.True(vectorValidation.ExitCode == 0, vectorValidation.Output);
        var noSimdValidation = await Run(node, assets.Directory, wasmTools, wasmToolsModule,
            "validate", "--features=-simd,-relaxed-simd", vector);
        Assert.NotEqual(0, noSimdValidation.ExitCode);
        Assert.Contains("SIMD support is not enabled", noSimdValidation.Output, StringComparison.OrdinalIgnoreCase);
        var execution = await Run(node, assets.Directory, Path.Combine(fixture, "simd-math-runner.mjs"),
            assets.Root, target, assets.Directory);
        Assert.True(execution.ExitCode == 0, execution.Output);
    }

    private static string RequiredDirectory(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        Assert.True(!string.IsNullOrWhiteSpace(path) && Directory.Exists(path),
            $"{variable} must name the prepared native interop tools directory.");
        return path;
    }

    private static async Task<(int ExitCode, string Output)> Run(string executable, string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, (await stdout) + (await stderr));
    }
}
