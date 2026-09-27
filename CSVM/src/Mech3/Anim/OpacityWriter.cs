using System;
using System.Collections.Generic;
using Godot;

namespace CSVM.Mech3.Anim;

/// <summary>Writes a subtree's opacity per instance. Every geometry node gets the
/// <c>csky_opacity</c> instance shader parameter. A mesh whose shader has no alpha path gets a
/// translucent twin of its material as that instance's surface override while the opacity is
/// partial. The shared, cached materials and meshes are never edited, so other instances of the
/// prototype draw as they were. The world runtime's opacity events and the projectile pool's flyout
/// fades both write through one of these.</summary>
internal sealed class OpacityWriter
{
    // Source material to its translucent twin, null when it cannot be made translucent. The twin
    // set recognises an override this writer installed. The shader-level cache lets materials
    // sharing one generated shader share one twin shader.
    private readonly Dictionary<ShaderMaterial, ShaderMaterial?> _fadeTwinCache = new();

    private readonly HashSet<Material> _fadeTwins = new();

    private readonly Dictionary<Shader, Shader?> _fadeShaderCache = new();

    /// <summary>Writes <paramref name="alpha"/> over the subtree and returns how many geometry
    /// nodes have an alpha path to show it. Opacity 1 removes the twins again.</summary>
    public int Apply(Node node, float alpha)
    {
        int n = 0;
        if (node is GeometryInstance3D g)
        {
            g.SetInstanceShaderParameter(SceneBuilder.OpacityParam, alpha);
            if (EnsureOpacityPath(g, alpha))
                n++;
        }
        // Walked by index: GetChildren() allocates a finalizable engine array per node, and this
        // runs over the whole subtree every frame of a fade (PERF-20).
        for (int i = 0, count = node.GetChildCount(); i < count; i++)
            n += Apply(node.GetChild(i), alpha);
        return n;
    }

    // Whether this mesh's shader reads the opacity parameter, and if not, whether a fade twin can
    // give it one. A partial opacity installs a per-surface override on THIS instance only.
    // ⚠ Never edit the shared material or mesh; both are cached across nodes. ⚠ Test for the USE
    // (SceneBuilder.OpacityTerm), never the uniform name or the include line. The uniform sits in the
    // shared preamble, so a name test is true even with no alpha path.
    private bool EnsureOpacityPath(GeometryInstance3D g, float alpha)
    {
        if (g is not MeshInstance3D mi || mi.Mesh is not { } mesh)
            return false;
        bool fading = !Mathf.IsEqualApprox(alpha, 1f);
        bool any = false;
        for (int i = 0; i < mesh.GetSurfaceCount(); i++)
        {
            if (mi.GetSurfaceOverrideMaterial(i) is { } installed && _fadeTwins.Contains(installed))
            {
                if (fading)
                    any = true;
                else
                    mi.SetSurfaceOverrideMaterial(i, null);
                continue;
            }
            if (mesh.SurfaceGetMaterial(i) is not ShaderMaterial { Shader: { } sh } sm)
                continue;
            if (sh.Code.Contains(SceneBuilder.OpacityTerm, StringComparison.Ordinal))
            {
                any = true;
                continue;
            }
            if (fading && FadeTwinOf(sm, sh) is { } twin)
            {
                mi.SetSurfaceOverrideMaterial(i, twin);
                any = true;
            }
        }
        return any;
    }

    private ShaderMaterial? FadeTwinOf(ShaderMaterial source, Shader shader)
    {
        if (_fadeTwinCache.TryGetValue(source, out var twin))
            return twin;
        if (!_fadeShaderCache.TryGetValue(shader, out var fadeShader))
            _fadeShaderCache[shader] = fadeShader = SceneBuilder.FadeShaderFor(shader);
        if (fadeShader != null)
        {
            twin = (ShaderMaterial)source.Duplicate();
            twin.Shader = fadeShader;
            _fadeTwins.Add(twin);
        }
        _fadeTwinCache[source] = twin;
        return twin;
    }
}
