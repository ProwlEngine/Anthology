using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

using Prowl.Graphite.ShaderDef.Precompiled;

namespace Prowl.Graphite.Samples.Web;


/// <summary>
/// Browser counterpart to <c>FileLoader</c>: assets arrive over HTTP rather than off disk.
/// </summary>
public static class WebAssets
{
    private static readonly HashSet<string> s_loadedModules = [];
    private static HttpClient? s_http;


    private static HttpClient Http => s_http ??= new HttpClient { BaseAddress = new Uri(WebHost.BaseUrl()) };


    /// <summary>
    /// Imports a JavaScript module once.
    /// </summary>
    /// <remarks>
    /// The path is resolved against the .NET runtime module, which lives in _framework, not against
    /// the page. That is why callers pass a path starting with "../" to reach a file deployed beside
    /// index.html.
    /// </remarks>
    public static async Task LoadModuleAsync(string moduleName, string modulePath)
    {
        if (!s_loadedModules.Add(moduleName))
            return;

        await JSHost.ImportAsync(moduleName, modulePath);
    }


    /// <summary>Fetches an asset as bytes.</summary>
    public static Task<byte[]> LoadBytesAsync(string path) => Http.GetByteArrayAsync(path);


    /// <summary>Fetches an asset as text.</summary>
    public static Task<string> LoadTextAsync(string path) => Http.GetStringAsync(path);


    /// <summary>
    /// Loads a shader compiled ahead of time by <c>Tools/ShaderPrecompile</c>.
    /// </summary>
    /// <remarks>
    /// The desktop samples run the Slang compiler at startup. That cannot happen here, because Slang
    /// is a native library, so the manifest and its WGSL are produced during the build and fetched
    /// like any other asset. Everything after this point is identical: the caller still applies the
    /// fixed-function state the shader source does not describe.
    /// </remarks>
    /// <param name="device">Device to create the program on.</param>
    /// <param name="manifestPath">Path to the generated .shader.json.</param>
    /// <returns>The description, ready for pipeline state to be filled in.</returns>
    public static async Task<ShaderDescription> LoadShaderAsync(GraphicsDevice device, string manifestPath)
    {
        ShaderManifest manifest = ShaderManifest.FromJson(await LoadTextAsync(manifestPath));

        string directory = manifestPath.Contains('/')
            ? manifestPath[..(manifestPath.LastIndexOf('/') + 1)]
            : string.Empty;

        Dictionary<string, byte[]> sources = [];
        foreach (ShaderManifest.StageEntry stage in manifest.Stages)
            sources[stage.File] = await LoadBytesAsync(directory + stage.File);

        return manifest.ToDescription(file => sources[file]);
    }
}
