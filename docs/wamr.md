# WAMR and ESP32-C6

The experimental `NetWasmWamr` target produces a **WIT-free wasm32 command**
for WAMR's **classic interpreter with legacy exception handling enabled**.
The existing component output remains the default. This is not qualification
of arbitrary C# applications or ESP32-C6 hardware.

## Why `unknown binary version` occurs

Both formats start with the Wasm magic bytes, but their next four bytes differ:

| Output | First eight bytes | WAMR 2.4.5 |
| --- | --- | --- |
| Default `OutputType=Exe` component | `00 61 73 6d 0d 00 01 00` | `unknown binary version` |
| Linked core module | `00 61 73 6d 01 00 00 00` | Header accepted; instruction support still matters |

NetWasm's default artifact is a WASI 0.3 Component Model binary. WAMR expects
core Wasm, optionally with WASI Preview 1 imports. Changing the version bytes
cannot convert a component into a core module. Do not deploy the intermediate
`<AssemblyName>.core.wasm`: it still needs runtime linking and host bindings.
`NetWasmRawWasm=true` is the JavaScript-host path, not a WAMR target.

There is a second incompatibility: NetWasm normally emits standardized
`try_table` exception handling. WAMR 2.4.5's ordinary binary rejects exception
tags, while its official `gc-eh` binary supports legacy EH and rejects
`try_table` (opcode `0x1f`). These failures were reproduced on Linux using
released NetWasm SDK 0.14.2. Changing collectors does not solve them.

## Experimental command target

Use an SDK built with these changes; the released 0.14.2 SDK does not implement
this property. No WIT file or exported-method attributes are needed:

```xml
<Project Sdk="NetWasm.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>netwasm0.1</TargetFramework>
    <NetWasmWamr>true</NetWasmWamr>
  </PropertyGroup>
</Project>
```

Write an ordinary synchronous `Main`, then publish and run the final artifact:

```sh
dotnet publish -c Release -o publish
/path/to/eh-enabled/iwasm --interp publish/YourApp.wasm
```

For `dotnet run`, set `NetWasmIwasmPath` to the EH-enabled executable's absolute
path. Publishing produces a core module exporting `_start` and `memory`, not
a component, JavaScript launcher or user-managed canonical ABI.

The target automatically supplies WASI Preview 1 bindings for console streams,
command-line arguments, environment variables, process exit, clocks and secure
random bytes. Host errors throw; unsupported filesystem, network and timer
services retain explicit platform-not-supported behavior.

The normal linear-memory managed collector remains enabled. Final linking
lowers supported `try_table` handlers to legacy `try`/`catch` and branches,
preserving their payloads and control flow. It does not replace managed
exceptions with traps or disable `catch`/`finally`. Incidental memory
initialization and copying use scalar or bulk-memory operations rather than
SIMD. Existing output modes retain their existing emission policies.

### Verified scope and remaining work

On Linux x86-64, a Release command built with checkout compiler/task assemblies
and SDK targets ran successfully in the official WAMR 2.4.5 `gc-eh` interpreter.
Its ordinary C# `Main(string[])` printed console output, allocated an array,
retained it across `GC.Collect()`, caught division by zero and ran `finally`.
The same module also ran in a Linux build of Espressif's
`esp_based_on_v2.4.0` fork at `8f806e0f2c02a768d2c35044f2964f769612d9bc`,
with classic interpretation, legacy EH and reference types enabled, but SIMD,
WasmGC, AOT and threads disabled.
The witness is in `tests/end-to-end/wamr`; with no arguments it prints:

```text
Hello from C# on WAMR
arguments=
42
caught
finally
```

This target remains experimental:

- Debug/O0 can retain SIMD-bearing library helpers and is not yet qualified;
  final feature validation rejects unsupported modules rather than publishing
  an incorrectly labeled compatible artifact. Use Release for the witness.
- Async `Main`, reference-carrying EH (`catch_ref`, `catch_all_ref`, `throw_ref`)
  and unsupported instructions are rejected, not silently weakened.
- Nested exceptions, filters, rethrows, finalizers, allocation failure, all
  Preview 1 services and application exit codes need broader integration
  qualification. Passing the witness is not full C# conformance.
- The ordinary no-EH WAMR executable is not supported.

## Espressif WAMR configuration

Espressif's `esp_based_on_v2.4.0` branch contains legacy EH support, but its
ESP-IDF defaults select the fast interpreter and disable EH. A firmware build
must use the **classic interpreter**, enable `WAMR_BUILD_EXCE_HANDLING=1`
before WAMR's CMake configuration, and provide the required WASI Preview 1
imports. Legacy EH is not supported by its fast interpreter or AOT engine.
NetWasm's collector does not require the separate WasmGC proposal.

This configuration and the generated module still require verification on the
actual ESP32-C6 firmware. GPIO, UART and other ESP-IDF facilities need explicit
native WAMR registrations and corresponding guest bindings.

## Memory limits

An earlier scalar witness declared three 64-KiB memory pages (192 KiB)
initially, despite being only 4,477 bytes on disk. Its 2-GiB maximum was a growth
limit, not an up-front allocation. File size is not a RAM budget.

The [runtime layout](targets-and-outputs.md#per-application-runtime-and-memory-layout)
includes a fixed native stack inside linear memory. Budget WAMR's execution
stack, metadata, interpreter and ESP-IDF/FreeRTOS memory separately. Choose
realistic memory limits and test allocation failure on the device.
