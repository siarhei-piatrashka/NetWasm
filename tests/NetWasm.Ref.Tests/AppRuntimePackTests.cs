// Copyright (c) 2026 Zion Sati and NetWasm contributors (https://github.com/zion-sati/NetWasm).
// SPDX-License-Identifier: MIT

using System.Xml.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Security;

namespace NetWasm.Ref.Tests;

// NetWasm.App.Runtime carries every NetWasm.App library's implementation and symbols under framework/,
// outside NuGet's asset folders, and lists them for the NetWasm compiler through buildTransitive.
public sealed class AppRuntimePackTests
{
    private static string Root => FindRepositoryRoot();

    [Fact]
    public void PacksEveryImplementationAndItsSymbolsUnderFramework()
    {
        var project = XDocument.Load(Path.Combine(Root, "src/NetWasm.App.Runtime/NetWasm.App.Runtime.csproj"));
        var pack = project.Descendants("Target").Single(target => (string?)target.Attribute("Name") == "NetWasmAppRuntimePack");
        var frameworkFiles = pack.Descendants("_NetWasmAppRuntimeFrameworkFile").Single();

        Assert.Equal("$(MSBuildThisFileDirectory)../libraries/*/*.csproj",
            (string?)project.Descendants("NetWasmAppLibrary").Single().Attribute("Include"));
        Assert.Equal("@(_NetWasmAppImplementation);@(_NetWasmAppImplementationSymbols)", (string?)frameworkFiles.Attribute("Include"));
        Assert.Contains(pack.Descendants("PackagePath"), path =>
            path.Value == "framework/%(_NetWasmAppRuntimeFrameworkFile.Filename)%(_NetWasmAppRuntimeFrameworkFile.Extension)");
        Assert.Contains(pack.Descendants("PackagePath"), path => path.Value == "buildTransitive/NetWasm.App.Runtime.targets");
        Assert.DoesNotContain(pack.Descendants("PackagePath"), path => path.Value.StartsWith("lib/", StringComparison.Ordinal) ||
            path.Value.StartsWith("ref/", StringComparison.Ordinal));
        Assert.Contains(pack.Descendants("Error"), error => (string?)error.Attribute("Code") == "NWRF011");
        Assert.Equal("NetWasmAppRuntimePack", project.Descendants("NetWasmSdkContractPackTarget").Single().Value);
    }

    [Fact]
    public void ListsTheImplementationsForTheCompilerInANetwasmBuild()
    {
        var targets = XDocument.Load(Path.Combine(Root, "src/NetWasm.App.Runtime/buildTransitive/NetWasm.App.Runtime.targets"));
        var group = targets.Descendants("ItemGroup").Single(itemGroup => itemGroup.Element("NetWasmFrameworkAssembly") is not null);

        // A guest build under either target framework (net10.0-netwasm, or the deprecated netwasm0.1).
        Assert.Equal("'$(NetWasmGuestFramework)' == 'true'", (string?)group.Attribute("Condition"));
        Assert.Equal("$(MSBuildThisFileDirectory)../framework/*.dll",
            (string?)group.Element("NetWasmFrameworkAssembly")!.Attribute("Include"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectsNativeArchivesForTheSimdSetting(bool simd)
    {
        var directory = Path.Combine(Path.GetTempPath(), "netwasm-app-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var targets = Path.Combine(Root, "src/NetWasm.App.Runtime/buildTransitive/NetWasm.App.Runtime.targets");
            var project = Path.Combine(directory, "app.proj");
            File.WriteAllText(project, $"""
                <Project>
                  <PropertyGroup>
                    <NetWasmGuestFramework>true</NetWasmGuestFramework>
                    <NetWasmSimd>{simd.ToString().ToLowerInvariant()}</NetWasmSimd>
                  </PropertyGroup>
                  <Import Project="{SecurityElement.Escape(targets)}" />
                </Project>
                """);
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "msbuild", project, "-getItem:NativeLibrary", "-nologo" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            using var json = JsonDocument.Parse(output);
            var libraries = json.RootElement.GetProperty("Items").GetProperty("NativeLibrary").EnumerateArray().ToArray();
            Assert.Equal(4, libraries.Length);
            foreach (var library in libraries)
            {
                var path = library.GetProperty("Identity").GetString()!.Replace('\\', '/');
                var target = library.GetProperty("WasmTarget").GetString();
                Assert.Contains($"/native/{target}/{(simd ? "" : "scalar/")}", path, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetWasm.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Could not locate the NetWasm repository root.");
    }
}
