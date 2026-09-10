using System;
using System.Collections.Generic;

using Prowl.Slang;


namespace Prowl.Graphite.ShaderDef.Compiler;


/// <summary>
/// WGSL compiler for WebGPU backends.
/// </summary>
public class WebGPUCompiler : CompilerModule
{
    /// <summary>
    /// Bind group ceiling this module enforces, matching WebGPU's default maxBindGroups limit.
    /// Descriptor sets map onto bind groups one for one, so a shader that needs more than this
    /// cannot be used unmodified even though Vulkan would accept it.
    /// </summary>
    public const uint MaxBindGroups = 4;

    private TargetDescription _target;

    /// <inheritdoc/>
    public TargetDescription Target => _target;

    /// <inheritdoc/>
    public GraphicsBackend Backend => GraphicsBackend.WebGPU;


    /// <summary>
    /// New WebGPUCompiler.
    /// </summary>
    /// <param name="profileString">
    /// Slang has no WGSL-specific profile, so this resolves to an unknown profile and the WGSL
    /// emitter ignores it. It is kept for parity with the other modules.
    /// </param>
    public WebGPUCompiler(string profileString = "wgsl_1_0")
    {
        _target = new()
        {
            Profile = GlobalSession.FindProfile(profileString),
            Format = CompileTarget.Wgsl
        };
    }


    /// <inheritdoc/>
    public ShaderDescription CompileForTarget(ComponentType linkedComponent, int layoutIndex, DiagnosticHandler handler)
    {
        // Reflect before generating code. A combined texture-sampler crashes the WGSL emitter outright
        // (see Reject below), so the layout pass has to run first to turn that into a diagnostic.
        ResourceLayoutDescription[] layouts = Reflect(linkedComponent, layoutIndex);

        // WGSL entry points keep the names Slang gave them, and the pipeline names each stage
        // explicitly, so unlike SPIR-V there is nothing to rename to "main".
        ShaderDescription description = SlangReflector.BuildDescription(linkedComponent, layoutIndex, handler);
        description.ResourceLayouts = layouts;
        return description;
    }


    static ResourceLayoutDescription[] Reflect(ComponentType linkedComponent, int layoutIndex)
    {
        ShaderReflection layout = linkedComponent.GetLayout(layoutIndex, out _);

        ShaderStages programStages = ShaderStages.None;
        for (uint i = 0; i < layout.EntryPointCount; i++)
            programStages |= ToStage(layout.GetEntryPointByIndex(i).Stage);

        Dictionary<uint, List<ResourceLayoutElementDescription>> bySet = [];

        foreach (VariableLayoutReflection parameter in layout.Parameters)
            Collect(parameter, baseSpace: 0, programStages, bySet);

        if (layout.GlobalConstantBufferSize > 0)
        {
            UniformBlockField[] globalFields = SlangReflector.ReflectUniformFields(layout.GlobalParamsTypeLayout.ElementTypeLayout);
            if (globalFields.Length > 0)
            {
                Add(bySet, 0, "$Global", ResourceKind.UniformBuffer, programStages,
                    (int)layout.GlobalConstantBufferBinding, globalFields);
            }
        }

        List<ResourceLayoutDescription> layouts = [];
        foreach ((uint set, List<ResourceLayoutElementDescription> elements) in bySet)
        {
            if (set >= MaxBindGroups)
            {
                throw new InvalidOperationException(
                    $"Shader uses descriptor set {set}, but WebGPU allows only {MaxBindGroups} bind groups (0-{MaxBindGroups - 1}). " +
                    "Merge parameter blocks so the shader fits within the limit.");
            }

            layouts.Add(new ResourceLayoutDescription(set, [.. elements]));
        }

        return [.. layouts];
    }


    static void Collect(
        VariableLayoutReflection parameter, uint baseSpace, ShaderStages stages,
        Dictionary<uint, List<ResourceLayoutElementDescription>> bySet)
    {
        TypeLayoutReflection typeLayout = parameter.TypeLayout;

        if (typeLayout.Kind == TypeKind.ParameterBlock)
        {
            CollectParameterBlock(parameter, baseSpace, stages, bySet);
            return;
        }

        if (!SlangReflector.TryGetResourceKind(typeLayout, out ResourceKind kind))
            return;

        Reject(typeLayout, parameter.Name);

        uint set = baseSpace + parameter.GetBindingSpace(ParameterCategory.DescriptorTableSlot);
        int binding = (int)parameter.GetOffset(ParameterCategory.DescriptorTableSlot);

        UniformBlockField[] fields = kind == ResourceKind.UniformBuffer
            ? SlangReflector.ReflectUniformFields(typeLayout.ElementTypeLayout)
            : [];

        Add(bySet, set, parameter.Name, kind, stages, binding, fields);
    }


    static void CollectParameterBlock(
        VariableLayoutReflection block, uint baseSpace, ShaderStages stages,
        Dictionary<uint, List<ResourceLayoutElementDescription>> bySet)
    {
        TypeLayoutReflection typeLayout = block.TypeLayout;
        uint set = baseSpace + block.GetOffset(ParameterCategory.SubElementRegisterSpace);

        // Loose uniform data collapses into one implicit uniform buffer at the container's reserved binding.
        TypeLayoutReflection elementLayout = typeLayout.ElementTypeLayout;
        if (elementLayout.GetSize() > 0)
        {
            int uboBinding = (int)typeLayout.ContainerVarLayout.GetOffset(ParameterCategory.DescriptorTableSlot);
            Add(bySet, set, block.Name, ResourceKind.UniformBuffer, stages, uboBinding,
                SlangReflector.ReflectUniformFields(elementLayout));
        }

        // Resources inside the block bind into its own space, offsets already absolute within it.
        foreach (VariableLayoutReflection field in elementLayout.Fields)
            if (field.TypeLayout.Kind != TypeKind.Scalar
                && field.TypeLayout.Kind != TypeKind.Vector
                && field.TypeLayout.Kind != TypeKind.Matrix)
                Collect(field, set, stages, bySet);
    }


    // WGSL has no combined texture-sampler type: a texture and the sampler it is read through are
    // always separate bindings. Slang 2026.12 does not split them for this target, and emitting a
    // Sampler2D declared inside a parameter block or constant buffer segfaults the compiler rather
    // than reporting an error, so catch it here while there is still a name to point at.
    static void Reject(TypeLayoutReflection typeLayout, PropertyID name)
    {
        if (!SlangReflector.IsCombinedTextureSampler(typeLayout))
            return;

        throw new InvalidOperationException(
            $"'{PropertyID.ToString(name)}' is a combined texture-sampler (Sampler2D and friends), which WGSL does not have. " +
            "Declare the texture and the sampler separately (Texture2D plus SamplerState) for the WebGPU target.");
    }


    static ShaderStages ToStage(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => ShaderStages.Vertex,
        ShaderStage.Fragment => ShaderStages.Fragment,
        ShaderStage.Compute => ShaderStages.Compute,
        _ => ShaderStages.None
    };


    static void Add(
        Dictionary<uint, List<ResourceLayoutElementDescription>> bySet,
        uint set, PropertyID name, ResourceKind kind, ShaderStages stages, int binding,
        UniformBlockField[] fields)
    {
        if (!bySet.TryGetValue(set, out List<ResourceLayoutElementDescription>? elements))
            bySet[set] = elements = [];

        elements.Add(new ResourceLayoutElementDescription(
            name,
            kind,
            stages,
            binding,
            ResourceLayoutElementOptions.None,
            PropertyID.ToString(name) ?? string.Empty,
            fields));
    }
}
