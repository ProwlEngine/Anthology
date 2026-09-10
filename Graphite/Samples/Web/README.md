# Browser sample ports

The desktop samples running on WebGPU in a browser, on the same render graph.

Four ports: **HelloTriangle**, **TexturedQuad**, **Cube** and **CubeGrid**. PBRRenderer is out of
scope, because it depends on Clay, Photonic and Unwrapper from outside this repository.

```
./serve.sh                       # builds HelloTriangle, serves on :8080
./serve.sh Cube 8081             # any sample, any port
node run-node.mjs TexturedQuad   # drives 3 frames against a mock GPU, no browser needed
```

CubeGrid takes its cube count from the URL, which is a browser's version of a command line:
`?cubes=10000` matches the desktop sample. It defaults to 2000.

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
- **Combined texture-samplers are split.** WGSL has no `Sampler2D`, so each sample that uses a
  texture carries its own `Shader.slang` declaring a `Texture2D` and a `SamplerState` separately, and
  binds the sampler under its own name. This is the only change to any shader.

`Mesh` and `ModelLoader` are linked straight out of `Samples/Shared` with no changes at all.

Requires the `wasm-tools` workload (`sudo dotnet workload install wasm-tools`).

Known backend defects are tracked in `Graphite/Platform/WebGPU/WGPU_MISTAKES.md`.
