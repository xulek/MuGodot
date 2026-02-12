using Godot;

namespace MuGodot.Objects.Worlds;

public interface IWorldObjectRules
{
    string? ResolveModelFileName(short type);

    float GetAnimationSpeed(short type);

    void ApplyBlendRules(short type, StandardMaterial3D[] materials, int surfaceCount);

    void ApplyInstanceRules(short type, MeshInstance3D instance);
}
