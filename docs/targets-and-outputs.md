# Targets and output formats

The WebAssembly memory width and the packaging format are separate choices.
See the [support status and roadmap](support-status.md) for the canonical
classification of this boundary and the remaining work.

| Output | wasm32 | wasm64 / Memory64 |
| --- | --- | --- |
| `compile` core module | Supported | Supported |
| Raw core module + JavaScript manifest/adapter | Supported | Supported with checked `BigInt` address transport |
| `componentize` Component output | Supported with WASI 0.3 | Rejected with `NW1010` |

`wasm32` uses 32-bit addresses and managed references. `wasm64` uses Memory64
`i64` address/reference transport while semantic IDs and lengths retain their
defined widths. Select the width with `--target`; the default is wasm32. The
runtime layout and native runtime target must match the application target.

## Browser and runtime requirements

NetWasm output uses the standardized WebAssembly exception-handling
instructions (`try_table` and `exnref`), fixed-width SIMD, bulk memory,
reference types, multi-value, non-trapping float-to-int conversions,
sign-extension operations and mutable globals. Exception handling is the most
recent of these, so it decides the oldest browsers that run NetWasm output. In
a browser that predates it, `open()` and `run()` fail with a
`NetWasmUnsupportedBrowserError` that names the missing features and the
versions below.

| Browser | wasm32 | wasm64 (Memory64) |
| --- | --- | --- |
| Chrome and Edge | 137 (May 2025) | 137 |
| Firefox | 131 (October 2024) | 134 (January 2025) |
| Safari on macOS and iOS | 18.4 (March 2025) | Not supported |

Node.js runs NetWasm output from 24.15. The SDK's local launcher uses the
Node.js from `NetWasm.HostTools`, which meets this; the requirement applies
when you host raw output in your own Node.js. Browser deployments use a single
linear memory, so they do not depend on multi-memory support. The versions come
from the [WebAssembly feature status](https://webassembly.org/features/) table.

## Per-application runtime and memory layout

The runtime pack supplies relocatable archives rather than fixed-address
runtime tiers. Each application records its static-data end in
`runtime-layout.json`; runtime materialization aligns the runtime base using
the manifest's current 16-byte alignment and links the matching target archive
and system-library closure. The manifest validates 4-byte pointers for wasm32
and 8-byte pointers for wasm64, and the linker machine, archive, and layout
must agree.

The native stack is fixed at 64 KiB. The default initial heap is 64 KiB and
initial memory is rounded to the 64 KiB WebAssembly page size. The heap can
grow up to the selected maximum when the host permits it; the stack does not
grow with the heap. Current policy caps are 2 GiB for wasm32 and 8 GiB for
wasm64, not pre-reserved runtime capacity or a wasm64 architectural limit.

Raw core output is the explicit JavaScript host path. The generated manifest
and adapter describe consumer-supplied imports/exports; browser APIs are not
implicit. This path supports both widths.

Component packaging uses the wasm32 + WASI 0.3 (`cm32p2`) toolchain. The CLI accepts the target spelling `wasm64`, but the capability
check fails before packaging with `NW1010` because the current stable external
encoder/toolchain does not provide an end-to-end Memory64 Component path. The
request is never silently downgraded to wasm32. Internal `cm64p2` names do not
change that support boundary.

WASI 0.3 is independent: it neither solves nor waits on the missing `cm64p2`
Memory64 Component encoder, canonical ABI, runtime, and host chain. User-defined
WIT functions may be `async func`, bound to `Task`. Use wasm32
for Components and wasm64 for a raw core module under its explicit host
contract.

## WASI Preview 1 investigation (not implemented)

The proposed target is a **wasm32 core command module**, runnable without a
Component Model or JavaScript host. Its only external imports, if any, must
be from `wasi_snapshot_preview1`. A `.wasm` extension or an intermediate core
module alone does not establish that contract. Preview 1 is not supported
today; this section is a plan, not a new build option.

### Current pipeline and constraints

- `src/NetWasm.Sdk/Sdk/Sdk.targets` defaults `NetWasmTarget` to `wasm32`;
  this property selects address width, not a WASI version. In
  `NetWasm.Toolchain.targets`, the SDK routes ordinary executables through
  `NetWasmComponentizeDependsOn`, and `NetWasmRawWasm=true` through
  `NetWasmRawBuildDependsOn`. Both write `NetWasmOutputPath`. The task targets
  in `src/NetWasm.Compiler.Tasks/buildTransitive/NetWasm.Compiler.Tasks.targets`
  materialize the runtime and then componentize or link the raw module.
  The compiler CLI's `-platform` likewise selects width; `nwc componentize`
  is a separate operation. Neither is a Preview 1 selector.
- The default component path uses WASI 0.3; its intermediate runtime uses
  `cm32p2`-prefixed component imports. Raw output has an explicit JavaScript
  host contract, not a WASI command ABI. The runtime import policy and raw
  import validators deliberately reject Preview 1. Do not relax those
  checks for existing targets.
- The pinned Emscripten/LLVM toolchain and runtime pack build relocatable
  archives, linked against an Emscripten system-library closure. The runtime
  intentionally replaces libc functions that would import Preview 1 in
  `src/NetWasm.Runtime/runtime_posix_unavailable.c`; component console,
  environment, clock, random and exit code use WASI 0.3 interfaces. Linking
  this closure unchanged cannot provide a standalone Preview 1 host contract.
  Internal `netwasm.application.v1` imports also need to be resolved by
  linking, not left for the WASI runtime.
- NetWasm emits modern Wasm features including exception handling; Preview 1
  describes the host ABI, not an engine feature level. A candidate runtime
  must support the emitted instructions. Managed threads, component/WIT
  services, JavaScript imports and the existing asynchronous host facilities
  cannot be assumed available under Preview 1. Filesystem access requires
  explicit preopens; networking and other unsupported APIs need a defined
  failure or exclusion, not an implicit component host.

### Recommended approach

Add a distinct, explicitly selected SDK output mode, for example a future
`NetWasmOutputKind=WasiPreview1` for executables; the exact property name is
to be finalized during implementation. It is **not** available now. Leave
the unset value and `NetWasmRawWasm` routing unchanged, keep
`NetWasmTarget=wasm32` as the width selection, and reject incompatible
combinations (including wasm64, WIT/component and JavaScript-host modes)
with a clear diagnostic rather than silently falling back.

Prototype a separate runtime/system-library closure using a pinned
[wasi-sdk](https://github.com/WebAssembly/wasi-sdk) targeting
`wasm32-wasip1`, with compatible LLVM/LLD and Wasm exception support verified
against the compiler output. This supplies a Preview 1 libc and command
startup; it does not by itself convert NetWasm's component imports. An
alternative is to retain the Emscripten closure and implement a dedicated
Preview 1 bridge, but its standalone stubs, Emscripten-specific symbols and
deliberate import exclusions make that a larger ABI audit. Neither option has
been proven end to end. Pin and package any new tools, sysroot, archives and
licenses separately. wasi-sdk disables C++ exceptions by default and requires
extra configuration for `setjmp`/`longjmp`; NetWasm's runtime pack builds
exception and longjmp support, so check both ABIs and libraries as well as
static native libraries, linking/optimization and target feature compatibility
before committing to one. The wasi-sdk Preview 1 target also lacks networking
and wasm64 support.

The new path needs target-specific runtime implementations for the supported
subset: map console output to `fd_write`, arguments/environment, clocks,
random and process exit to their Preview 1 equivalents as needed; define
errors for unavailable features. Avoid the existing 0.3 canonical ABI and
async stream machinery. Provide a command `_start` that initializes the
linked application/runtime once, invokes the managed entry point, handles its
exit status and failures, and respects collection/finalization requirements.
Resolve application/runtime cross-imports internally and reject unresolved
non-Preview 1 imports in the *final* artifact. Preserve the existing runtime
layout, ABI and collector invariants; do not assume a libc startup object alone
initializes NetWasm.

### Implementation and qualification steps

1. Establish a minimal synchronous console-command scope and document the
   supported API surface, exit behavior, capability grants and explicit
   failures. Prototype the sysroot and feature compatibility before promising
   broader API or native-library support.
2. Add the separate selector, validation, build routing and isolated runtime
   pack/identity and incremental-build inputs. Keep output paths and deployment
   metadata distinct enough to prevent stale artifacts when switching modes.
   Do not run componentization, `jco`, a JS adapter or component deployment
   for this mode.
3. Implement and link the Preview 1 runtime bridge and command entry point;
   audit the reachable CoreLib and native-library imports, GC lifecycle and
   error paths. Add a target-specific final-import allowlist and tests for
   rejected combinations and unexpected imports, including 0.2/0.3,
   `wasi_unstable`, JS and unresolved NetWasm imports.
4. Add unit/routing tests and an end-to-end console fixture that checks
   arguments, stdout, exit status, initialization and a failure path. Test
   filesystem preopens and clocks/random only when those APIs are claimed.
   Run against the project's pinned Wasmtime version, recording the version,
   required feature settings and any blockers; do not infer support merely
   from the Preview 1 ABI.
5. Before and after the change, use clean builds of the same
   `dotnet new netwasm-app` fixture with the same pinned tools: run
   `dotnet build`, `dotnet run`, and `dotnet publish`; also build and run the
   existing `NetWasmRawWasm=true` executable path. Compare artifact names,
   format, imports, deployment manifests/sidecars and observable output and
   exit status; compare hashes where deterministic and investigate any
   differences. Run existing SDK routing and component/raw end-to-end tests.
   The selector being absent must not change existing commands or outputs.

### Artifact and smoke-test gate

After implementation, for a freshly built `app.wasm` and a console fixture
that prints `42`, use the pinned `wasm-tools` and Wasmtime, for example:

```sh
od -An -tx1 -N8 app.wasm
wasm-tools validate app.wasm --features all
wasm-tools print app.wasm > app.wat
wasmtime --version
wasmtime run app.wasm > stdout.txt
cat stdout.txt
```

Require the eight header bytes `00 61 73 6d 01 00 00 00` (core version 1,
not a component), successful validation, a core `(module ...)` in the
printed text, and a `_start` command export. Inspect **all** final imports in
`app.wat`: allow only functions from `wasi_snapshot_preview1`, with no
component, JavaScript, or other host imports. An import-free module is allowed
in general, but this console fixture must actually exercise Preview 1
`fd_write`; do not pass it on a vacuous import check. Assert exit code zero
and exact expected stdout (`42` plus newline), and check a deliberately
nonzero exit separately. Run `wasmtime run` directly on the core module, with
no component adapter or host and no preopens for this fixture. Validation with
`--features all` is not proof that Wasmtime supports those features: runtime
instantiation and execution are mandatory. Pin the tested Wasmtime version and
record any necessary flags in the eventual automated test.

## Contributor and qualification rule

Do not triage the known `cm64p2` result as a compiler failure. When a test asks
the current pinned toolchain to package a wasm64 Component and receives
`NW1010` for the unavailable Memory64 Component capability, record that cell as
externally blocked and stop that cell. Do not retry with hidden flags, silently
downgrade it to wasm32, fork or upgrade external tools, substitute raw-core
output for the Component assertion, or infer that WASI 0.3 changes the result.

This stop rule applies only to wasm64 **Component packaging**. The matching raw
wasm64 core module remains a first-class target and must still be compiled,
validated, executed, and semantically asserted. Revisit the Component cell only
when NetWasm deliberately updates its pinned Component toolchain/capability
contract or the project explicitly starts a `cm64p2` enablement phase.
