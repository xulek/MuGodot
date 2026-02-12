using Client.Data;
using Godot;

namespace MuGodot.Objects.Worlds;

public sealed class FallbackWorldObjectRules : IWorldObjectRules
{
    public static FallbackWorldObjectRules Instance { get; } = new();

    private FallbackWorldObjectRules()
    {
    }

    public string? ResolveModelFileName(short type)
    {
        if (type < 0 || !Enum.IsDefined(typeof(ModelType), (ushort)type))
            return null;

        var enumName = ((ModelType)(ushort)type).ToString();

        // Skip "Unknown*" entries.
        if (enumName.StartsWith("Unknown", StringComparison.Ordinal))
            return null;

        return $"{enumName}.bmd";
    }

    public float GetAnimationSpeed(short type)
    {
        _ = type;
        return 4.0f;
    }

    public void ApplyBlendRules(short type, StandardMaterial3D[] materials, int surfaceCount)
    {
        _ = type;
        _ = materials;
        _ = surfaceCount;
    }

    public void ApplyInstanceRules(short type, MeshInstance3D instance)
    {
        _ = type;
        _ = instance;
    }
}
