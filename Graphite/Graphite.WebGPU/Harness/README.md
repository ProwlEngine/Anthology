# WebGPU shim harness

A browser page that drives `Interop/graphite-webgpu.js` through the same call sequence
`WgpuInterop` makes from C#, using WGSL and a manifest generated from
`Samples/HelloTriangle/Shader.slang` by `Tools/ShaderPrecompile`.

```
node shim.test.mjs   # mock WebGPU under Node: no browser, no GPU
./run.sh             # real browser page, serves on :8731
```

`shim.test.mjs` runs the shim against a mock WebGPU and checks the bookkeeping the browser cannot
conveniently assert: handle allocation and reuse, what `release` destroys, and the
handle-to-object substitution inside every JSON descriptor. It needs nothing but Node.

For the browser page:

Open the printed URL in a browser with WebGPU. A green **PASS** banner means every shim call
succeeded, the triangle rendered, and the device reported no errors. The page also sets
`window.__harness = { ok, log }` so an automated check can read the result.

## What this covers

- The handle table: allocation, reuse, and that `endRenderPass` and `submit` release what they own.
- The JSON descriptor shapes the cold path accepts.
- The WebGPU calls themselves, against real generated WGSL.

## What this does not cover

C#-to-JavaScript marshalling. That needs the `wasm-tools` workload and a .NET WebAssembly app,
which arrives with the browser sample host.
