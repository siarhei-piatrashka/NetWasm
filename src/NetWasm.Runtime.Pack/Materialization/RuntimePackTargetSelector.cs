// Copyright (c) 2026 Zion Sati and NetWasm contributors (https://github.com/zion-sati/NetWasm).
// SPDX-License-Identifier: MIT

using System;
using NetWasm.Runtime.Pack.Planning;

namespace NetWasm.Runtime.Pack.Materialization;

internal sealed class RuntimePackTargetSelector(IRuntimeOptimizationPolicyResolver policies) : IRuntimePackTargetSelector
{
    private readonly IRuntimeOptimizationPolicyResolver _policies = policies ?? throw new ArgumentNullException(nameof(policies));

    public RuntimePackTarget Select(RuntimePackManifest manifest, string target, string? garbageCollector,
        RuntimeWasmOptimization optimization = RuntimeWasmOptimization.Oz,
        bool simd = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var policy = _policies.Resolve(optimization);
        var selected = garbageCollector ?? manifest.DefaultGarbageCollector;
        if (!RuntimeGarbageCollectors.IsSupported(selected))
            throw new InvalidOperationException(RuntimeGarbageCollectors.ValidationMessage);
        if (!manifest.TargetLookup.TryGetValue((target, selected), out var result))
            throw new InvalidOperationException("The requested NetWasm runtime target and collector pair is unavailable.");
        if (!policy.PreferNativeSpeed && simd)
            return result;
        var speed = (simd ? result.Speed : policy.PreferNativeSpeed ? result.ScalarSpeed : result.Scalar) ??
            throw new InvalidOperationException(simd
                ? "The NetWasm runtime speed assets are unavailable."
                : "NetWasmSimd=false requires a runtime pack containing scalar native assets.");
        return result with
        {
            RuntimeFootprintBytes = speed.RuntimeFootprintBytes,
            RuntimeArchive = speed.RuntimeArchive,
            CollectorArchive = speed.CollectorArchive,
            PlatformArchives = speed.PlatformArchives,
            NativeValidation = !simd && result.NativeValidation is { } profile
                ? profile with { Features = profile.Features.Remove("simd") }
                : result.NativeValidation,
        };
    }
}
