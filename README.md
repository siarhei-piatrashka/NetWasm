# NetWasm

NetWasm makes it easy to build .NET apps into WebAssembly.

## MSBuild options

### Disable SIMD (simd128)

If you need the produced `.wasm` to not contain SIMD instructions (e.g., for older runtimes/engines), set:

```xml
<PropertyGroup>
  <NetWasmNoSimd>true</NetWasmNoSimd>
</PropertyGroup>
```

When enabled, NetWasm will avoid enabling `simd128`/`relaxed-simd` target features in the toolchain so the output uses scalar (non-SIMD) code paths.

---

> Note: the rest of this README remains unchanged.

