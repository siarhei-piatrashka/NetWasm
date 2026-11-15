using Microsoft.Extensions.DependencyInjection;
using NetWasm.Compiler.ComponentModel.Raw;
using NetWasm.Compiler.ComponentModel.Wamr;
using NetWasm.Compiler.Core.ManagedExecutables;

namespace NetWasm.Compiler.Tasks.ComponentModel;

internal sealed class WamrCommandBuildSession(
    ServiceProvider services,
    WamrCommandLinker linker) : IDisposable
{
    public void Link(RawModuleLinkRequest request, ManagedExecutableEntryPointAbi entryPoint) =>
        linker.Link(request, entryPoint);

    public void Dispose() => services.Dispose();
}
