// Copyright (c) 2026 Zion Sati and NetWasm contributors (https://github.com/zion-sati/NetWasm).
// SPDX-License-Identifier: LicenseRef-NetWasm-Community-1.0

using System.Collections.Immutable;
using NetWasm.Compiler.Core;

namespace NetWasm.Compiler.Metadata;

public sealed record MetadataCompilationSnapshot(
    ImmutableArray<ManagedAssembly> Assemblies,
    AssemblyIdentity EntryAssemblyIdentity,
    ImmutableArray<TypeDefinitionModel> Types,
    ImmutableArray<MethodDefinitionModel> Methods,
    ImmutableArray<MethodDefinitionModel> EntryAssemblyMethods,
    ImmutableDictionary<string, string>? ReferenceAssemblyAliases = null)
{
    public bool Simd => Types.IsEmpty || Types[0].Simd;
    /// <summary>Original declarations, isolated from executable types and methods.</summary>
    public ImmutableArray<ManagedAssembly> OriginalContractAssemblies { get; init; } = [];
}
