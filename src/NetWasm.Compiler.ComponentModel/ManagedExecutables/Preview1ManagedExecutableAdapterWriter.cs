// Copyright (c) 2026 Zion Sati and NetWasm contributors (https://github.com/zion-sati/NetWasm).
// SPDX-License-Identifier: LicenseRef-NetWasm-Community-1.0

using System;
using NetWasm.Compiler.Core.ManagedExecutables;

namespace NetWasm.Compiler.ComponentModel.ManagedExecutables;

// Adapts the managed process entry to the WASI Preview 1 command ABI.
public sealed class Preview1ManagedExecutableAdapterWriter(IWasmTextModuleWriter modules)
{
    private readonly IWasmTextModuleWriter _modules = modules ??
        throw new ArgumentNullException(nameof(modules));

    public void Write(string outputPath, ComponentTarget target, ManagedExecutableEntryPointAbi entryPoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(entryPoint);
        if (target.Width != "wasm32")
            throw ComponentException.Invalid("WASI Preview 1 commands require wasm32");
        if (entryPoint.CompletionShape != ManagedExecutableCompletionShape.Synchronous)
            throw ComponentException.Invalid("WASI Preview 1 commands require a synchronous Main");
        if (entryPoint.ParameterShape is not (ManagedExecutableParameterShape.None or ManagedExecutableParameterShape.StringArray))
            throw new ArgumentOutOfRangeException(nameof(entryPoint), "Unsupported managed parameter shape.");

        var source = entryPoint.ReturnShape switch
        {
            ManagedExecutableReturnShape.Void =>
                "(module " +
                "(import \"netwasm.application.v1\" \"run\" (func $run)) " +
                "(func (export \"_start\") call $run))",
            ManagedExecutableReturnShape.ExitCode =>
                "(module " +
                "(import \"netwasm.application.v1\" \"run\" (func $run (result i32))) " +
                "(import \"wasi_snapshot_preview1\" \"proc_exit\" (func $exit (param i32))) " +
                "(func (export \"_start\") (local $code i32) " +
                "call $run local.tee $code if local.get $code call $exit unreachable end))",
            _ => throw new ArgumentOutOfRangeException(nameof(entryPoint), "Unsupported managed return shape."),
        };
        _modules.Write(source, outputPath);
    }
}
