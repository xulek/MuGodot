using Godot;

namespace MuGodot.Objects.Worlds;

internal static class ObjectMaterialBlendHelper
{
    public static void SetAlphaBlend(StandardMaterial3D material)
    {
        material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        material.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
    }

    public static void SetAlphaCutout(StandardMaterial3D material)
    {
        material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
        material.AlphaScissorThreshold = 0.01f; // Matches MonoGame ReferenceAlpha≈2/255.
        material.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
        material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
        material.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
    }

    public static void SetOpaque(StandardMaterial3D material)
    {
        material.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
        material.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
        material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
        var c = material.AlbedoColor;
        material.AlbedoColor = new Color(c.R, c.G, c.B, 1f);
    }
}
