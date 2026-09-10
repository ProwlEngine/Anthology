# Browser sample ports

The desktop samples running on WebGPU in a browser, on the same render graph.

```
./serve.sh                       # builds HelloTriangle, serves on :8080
node run-node.mjs HelloTriangle  # drives 3 frames against a mock GPU, no browser needed
```

`run-node.mjs` is the quick check. Everything except the GPU is real — the .NET runtime, the interop
marshalling, the shim, the backend, the render graph and the ahead-of-time shaders — so it catches
anything structurally wrong with a frame without needing a display. Set `TRACE=1` to see every
WebGPU call tagged with the frame it happened in, which is how the wasted render pass and the bind
group churn were found.

## How a port differs from its desktop original

Passes, pipelines and views are copied across unchanged; that is the point. Only the entry point
moves, and only where the browser forces it:

- **The device arrives asynchronously**, because requesting a WebGPU adapter and device are promises.
- **Shaders are compiled during the build**, by `Tools/ShaderPrecompile`, because Slang is a native
  library and cannot run in WebAssembly. The sample fetches the generated WGSL and its manifest.
- **The browser owns the frame loop**, so the host arms `requestAnimationFrame` and returns instead
  of blocking until a window closes.
- **Images are decoded by the browser** rather than Magick.NET, and the frame timer reports into the
  page rather than positioning a console cursor.

`Mesh` and `ModelLoader` are linked straight out of `Samples/Shared` with no changes at all.

Requires the `wasm-tools` workload (`sudo dotnet workload install wasm-tools`).
