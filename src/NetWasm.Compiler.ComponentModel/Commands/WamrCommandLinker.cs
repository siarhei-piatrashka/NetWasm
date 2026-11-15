using System;
using System.IO;
using NetWasm.Compiler.ComponentModel.Optimization;
using NetWasm.Compiler.ComponentModel.Raw;
using NetWasm.Compiler.Core.ManagedExecutables;

namespace NetWasm.Compiler.ComponentModel.Wamr;

public sealed class WamrCommandLinker(
    IRawModuleLinkInputValidator inputs,
    IComponentPackageWorkspaceFactory workspaces,
    IEmscriptenEnvironmentShimWriter environment,
    IWasmTextModuleWriter modules,
    IComponentCoreModuleMergeRunner merge,
    IComponentCoreModuleOptimizer optimizer,
    IWasmOptimizationSettingsResolver settings,
    IByteFileReader reader,
    IByteFileWriter writer,
    IFileMover files,
    IWasmTools tools)
{
    public void Link(RawModuleLinkRequest request, ManagedExecutableEntryPointAbi entryPoint)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entryPoint);
        if (request.Target.Width != "wasm32" ||
            entryPoint.CompletionShape != ManagedExecutableCompletionShape.Synchronous)
            throw ComponentException.Invalid("WAMR command output requires a synchronous wasm32 Main");
        inputs.Validate(request);
        using var workspace = workspaces.Create(request.OutputPath);
        var options = settings.Resolve(request.Optimization, request.OptimizationOverrides);
        var environmentPath = Path.Combine(workspace.TemporaryDirectory, "environment.wasm");
        var adapterPath = Path.Combine(workspace.TemporaryDirectory, "command.wasm");
        var mergedPath = Path.Combine(workspace.TemporaryDirectory, "merged.wasm");
        var selectedPath = Path.Combine(workspace.TemporaryDirectory, "selected.wasm");
        environment.Write(environmentPath, request.Target);
        modules.Write(CreateAdapter(entryPoint), adapterPath);
        merge.Run(new(
            request.ApplicationModulePath,
            request.RuntimeModulePath,
            environmentPath,
            selectedPath,
            request.Target,
            ManagedExecutableAdapterModulePath: adapterPath)
        {
            ApplicationInlineAnnotations = options.ManagedInlineAnnotations,
            RuntimeInlineAnnotations = options.RuntimeInlineAnnotations,
            Intermediates = new(
                Path.Combine(workspace.TemporaryDirectory, "application.wasm"),
                Path.Combine(workspace.TemporaryDirectory, "runtime.wasm"),
                mergedPath),
            Exports = new(null, [])
            {
                KeptExports = [new("_start", 0), new("memory", 2)],
            },
        });
        optimizer.Optimize(
            selectedPath, workspace.LinkedModulePath, request.Target, options.BackendPreset,
            options.Converge, options.LowMemoryUnused);
        writer.Write(workspace.LinkedModulePath,
            new WasmLegacyExceptionLowerer().Lower(reader.Read(workspace.LinkedModulePath)));
        var validation = tools.Run(
            "validate",
            "--features=-all,mutable-global,saturating-float-to-int,sign-extension,bulk-memory,reference-types,multi-value,exceptions,legacy-exceptions",
            workspace.LinkedModulePath);
        if (validation.ExitCode != 0)
            throw ComponentException.Tool(
                "WAMR classic-interpreter feature validation failed: " + validation.StandardError.Trim());
        files.Move(workspace.LinkedModulePath, request.OutputPath);
    }

    private static string CreateAdapter(ManagedExecutableEntryPointAbi entryPoint)
    {
        var (result, code) = entryPoint.ReturnShape switch
        {
            ManagedExecutableReturnShape.Void => ("", "i32.const 0"),
            ManagedExecutableReturnShape.ExitCode => ("(result i32)", ""),
            _ => throw ComponentException.Invalid("unsupported WAMR command return type"),
        };
        return "(module " +
            $"(import \"netwasm.application.v1\" \"run\" (func $run {result})) " +
            "(import \"wasi_snapshot_preview1\" \"proc_exit\" (func $exit (param i32))) " +
            $"(func (export \"_start\") call $run {code} call $exit unreachable))";
    }
}
