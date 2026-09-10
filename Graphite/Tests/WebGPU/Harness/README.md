# WebGPU harnesses

Three ways to exercise the interop boundary, in increasing order of how much is real.

## 1. Shim only, no browser

```
node shim.test.mjs
```

Runs `Graphite/Platform/WebGPU/Interop/graphite-webgpu.js` against a mock WebGPU under plain Node. Covers the bookkeeping a
browser cannot conveniently assert: handle allocation and reuse, what `release` destroys, and the
handle-to-object substitution inside every JSON descriptor. Needs nothing but Node.

## 2. Interop boundary, no GPU

```
dotnet build InteropProof/InteropProof.csproj
node InteropProof/run-node.mjs
```

Runs the real .NET WebAssembly app against a mock WebGPU. The runtime, the `[JSImport]` marshalling,
the shim and the ahead-of-time shader loading are all real; only the GPU is faked. This is what
catches problems a compile cannot: reflection-free JSON, `Span<byte>` byte layout across the
boundary, whether an empty `Span<int>` really omits the dynamic offsets argument. It prints every
WebGPU call the C# code made, with the leading bytes of each upload.

## 3. Everything real

```
./run.sh                                    # shim driven from JavaScript, serves on :8731
```

or, for the C# path on a real GPU:

```
./InteropProof/serve.sh                     # builds and serves on :8080
```

A green **PASS** banner means every call succeeded and the device reported no errors. Both pages also
set `window.__proof` / `window.__harness` so an automated check can read the result.

Requires the `wasm-tools` workload (`sudo dotnet workload install wasm-tools`) for anything that
builds an AppBundle.
