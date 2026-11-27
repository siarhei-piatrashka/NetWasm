// Copyright (c) 2026 Zion Sati and NetWasm contributors (https://github.com/zion-sati/NetWasm).
// SPDX-License-Identifier: MIT

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";

const [repository, target, directory] = process.argv.slice(2);
const { instantiateNetWasm } = await import(pathToFileURL(`${repository}/src/NetWasm.Runtime/browser-host.mjs`));
const { createRuntimeContractImports } = await import(pathToFileURL(`${repository}/tests/NetWasm.Runtime.Tests/runtime-contract-imports.mjs`));
const manifest = JSON.parse(await readFile(`${directory}/interop.json`, "utf8"));
const inputs = [-31, -7, -1, 0, 1, 3, 7, 100, 10000];
const observations = [];
for (const name of ["simd", "scalar"]) {
  const module = await WebAssembly.compile(await readFile(`${directory}/${name}.wasm`));
  let managed;
  const events = [];
  managed = await instantiateNetWasm({
    module,
    manifest,
    runtimeModules: createRuntimeContractImports(target, () => managed.instance.exports.memory),
    managedExceptionReporting: { reportImmediate: event => events.push(event) },
  });
  try {
    observations.push(inputs.map(input => managed.instance.exports.Run(input)));
    assert.deepEqual(events, []);
  } finally {
    managed.dispose();
  }
}
assert.deepEqual(observations[0], observations[1]);
assert.deepEqual(observations[0], [-233, -19, 34, 43, 52, 71, 107, 948, 90093]);
