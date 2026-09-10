using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Prowl.Graphite;
using Prowl.Graphite.ShaderDef;
using Prowl.Graphite.ShaderDef.Compiler;
using Prowl.Graphite.ShaderDef.Precompiled;

namespace ShaderPrecompile;


// Compiles Slang shaders ahead of time for targets that cannot run the Slang compiler themselves.
//
// The WebGPU backend is the case that needs this: Slang is a native library, so it cannot run inside
// WebAssembly. Each shader becomes one source file per stage plus a .shader.json manifest describing
// the entry points, vertex inputs and bind groups, which the browser loads instead of compiling.
//
// Usage:
//   dotnet run --project Tools/ShaderPrecompile -- --out <dir> [--backend WebGPU] <shader.slang> ...
internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: ShaderPrecompile --out <dir> [--backend WebGPU] <shader.slang> ...");
            return args.Length == 0 ? 1 : 0;
        }

        string? outDir = ValueOf(args, "--out");
        if (outDir == null)
        {
            Console.Error.WriteLine("error: --out <dir> is required.");
            return 1;
        }

        string backendName = ValueOf(args, "--backend") ?? nameof(GraphicsBackend.WebGPU);
        if (!Enum.TryParse(backendName, ignoreCase: true, out GraphicsBackend backend))
        {
            Console.Error.WriteLine($"error: unknown backend '{backendName}'.");
            return 1;
        }

        string[] inputs = [.. args.Where(a => !a.StartsWith('-') && !IsFlagValue(args, a))];
        if (inputs.Length == 0)
        {
            Console.Error.WriteLine("error: no input shaders given.");
            return 1;
        }

        Directory.CreateDirectory(outDir);

        int failed = 0;
        foreach (string input in inputs)
        {
            try
            {
                Compile(input, outDir, backend);
            }
            catch (Exception e)
            {
                // A shader that WGSL cannot express is a normal outcome worth reporting clearly,
                // rather than a stack trace, so the build log points at the shader to change.
                Console.Error.WriteLine($"error: {Path.GetFileName(input)}: {e.Message}");
                failed++;
            }
        }

        return failed == 0 ? 0 : 1;
    }


    static void Compile(string input, string outDir, GraphicsBackend backend)
    {
        string fullPath = Path.GetFullPath(input);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"shader not found at {fullPath}");

        string name = Path.GetFileNameWithoutExtension(fullPath);
        string sourceDir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();

        SlangShaderCompiler compiler = new();
        compiler.RegisterModule(ModuleFor(backend));
        compiler.BeginSession([new DirectoryInfo(sourceDir)]);

        ShaderPass pass = new() { State = new PassState(), InlineSlang = File.ReadAllText(fullPath) };
        ShaderDescription description = compiler.Compile(pass, [], backend);

        compiler.EndSession();

        (ShaderManifest manifest, List<(string File, byte[] Bytes)> sources) =
            ShaderManifest.FromDescription(description, backend, name, ExtensionFor(backend));

        foreach ((string file, byte[] bytes) in sources)
        {
            File.WriteAllBytes(Path.Combine(outDir, file), bytes);
            Console.WriteLine($"  {file} ({bytes.Length} bytes)");
        }

        string manifestFile = name + ".shader.json";
        File.WriteAllText(Path.Combine(outDir, manifestFile), manifest.ToJson());
        Console.WriteLine($"  {manifestFile}");
    }


    static CompilerModule ModuleFor(GraphicsBackend backend) => backend switch
    {
        GraphicsBackend.WebGPU => new WebGPUCompiler(),
        GraphicsBackend.Vulkan => new VulkanCompiler(),
        _ => throw new NotSupportedException($"No compiler module for backend {backend}.")
    };


    // WGSL is text; SPIR-V is a binary word stream. The extension keeps generated output honest
    // about which of the two a file holds.
    static string ExtensionFor(GraphicsBackend backend) => backend switch
    {
        GraphicsBackend.WebGPU => "wgsl",
        GraphicsBackend.Vulkan => "spv",
        _ => "bin"
    };


    static string? ValueOf(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }


    static bool IsFlagValue(string[] args, string arg)
    {
        int i = Array.IndexOf(args, arg);
        return i > 0 && args[i - 1].StartsWith("--");
    }
}
