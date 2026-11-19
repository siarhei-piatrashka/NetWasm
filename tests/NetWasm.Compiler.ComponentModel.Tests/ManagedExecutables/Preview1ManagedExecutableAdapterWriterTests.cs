// Copyright (c) 2026 Zion Sati and NetWasm contributors (https://github.com/zion-sati/NetWasm).
// SPDX-License-Identifier: LicenseRef-NetWasm-Community-1.0

using NetWasm.Compiler.ComponentModel.ManagedExecutables;
using NetWasm.Compiler.Core;
using NetWasm.Compiler.Core.ManagedExecutables;

namespace NetWasm.Compiler.ComponentModel.Tests.ManagedExecutables;

public sealed class Preview1ManagedExecutableAdapterWriterTests
{
    [Theory]
    [InlineData(ManagedExecutableParameterShape.None, ManagedExecutableReturnShape.Void)]
    [InlineData(ManagedExecutableParameterShape.StringArray, ManagedExecutableReturnShape.Void)]
    [InlineData(ManagedExecutableParameterShape.None, ManagedExecutableReturnShape.ExitCode)]
    [InlineData(ManagedExecutableParameterShape.StringArray, ManagedExecutableReturnShape.ExitCode)]
    public void WritesACommandEntry(ManagedExecutableParameterShape parameters, ManagedExecutableReturnShape result)
    {
        var modules = new RecordingWasmTextModuleWriter();
        new Preview1ManagedExecutableAdapterWriter(modules).Write(
            "command.wasm", ComponentTarget.Wasm32Wasi02, new(parameters, result));

        Assert.Equal("command.wasm", modules.OutputPath);
        Assert.Contains("(export \"_start\")", modules.Source);
        Assert.Contains("(import \"netwasm.application.v1\" \"run\"", modules.Source);
        if (result == ManagedExecutableReturnShape.ExitCode)
        {
            Assert.Contains("(import \"wasi_snapshot_preview1\" \"proc_exit\"", modules.Source);
            Assert.Contains("local.get $code call $exit unreachable", modules.Source);
        }
        else
        {
            Assert.DoesNotContain("proc_exit", modules.Source);
        }
    }

    [Fact]
    public void RejectsUnsupportedCommands()
    {
        var modules = new RecordingWasmTextModuleWriter();
        var writer = new Preview1ManagedExecutableAdapterWriter(modules);
        Assert.Throws<CompilerException>(() => writer.Write(
            "command.wasm", ComponentTarget.Wasm64Wasi02,
            new(ManagedExecutableParameterShape.None, ManagedExecutableReturnShape.Void)));
        Assert.Throws<CompilerException>(() => writer.Write(
            "command.wasm", ComponentTarget.Wasm32Wasi02,
            new(ManagedExecutableParameterShape.None, ManagedExecutableReturnShape.Void,
                ManagedExecutableCompletionShape.Asynchronous)));
        Assert.Empty(modules.Source);
    }
}
