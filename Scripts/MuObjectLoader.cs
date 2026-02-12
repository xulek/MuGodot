using Client.Data.OBJS;
using Godot;
using MuGodot.Objects.Worlds;

namespace MuGodot;

/// <summary>
/// Loads and places map objects (trees, rocks, buildings, etc.) from MU Online OBJS data.
/// Object model mapping and blend behavior are provided by world-specific rule sets.
/// </summary>
public class MuObjectLoader
{
    private readonly MuModelBuilder _modelBuilder;

    public MuObjectLoader(MuModelBuilder modelBuilder, MuTerrainBuilder terrainBuilder)
    {
        _modelBuilder = modelBuilder;
        _ = terrainBuilder;
    }

    /// <summary>
    /// Load and place all map objects for a world.
    /// Returns the parent node containing all placed objects.
    /// </summary>
    public async Task<Node3D> LoadObjectsAsync(
        int worldIndex,
        Node3D parent,
        bool enableAnimations = true,
        bool assignEditorOwnership = false)
    {
        var objPath = System.IO.Path.Combine(
            MuConfig.DataPath,
            $"World{worldIndex}",
            $"EncTerrain{worldIndex}.obj"
        );

        if (!System.IO.File.Exists(objPath))
        {
            GD.PrintErr($"Objects file not found: {objPath}");
            return parent;
        }

        var objReader = new OBJReader();
        var objData = await objReader.Load(objPath);

        GD.Print($"Loading {objData.Objects.Length} map objects for World{worldIndex}...");

        var worldRules = WorldObjectRulesResolver.Resolve(worldIndex);

        // Group objects by type to batch-load models.
        var objectsByType = new Dictionary<short, List<IMapObject>>();
        foreach (var obj in objData.Objects)
        {
            if (!objectsByType.TryGetValue(obj.Type, out var list))
            {
                list = new List<IMapObject>();
                objectsByType[obj.Type] = list;
            }

            list.Add(obj);
        }

        int placedCount = 0;
        int skippedTypes = 0;

        foreach (var (type, objects) in objectsByType)
        {
            var bmdFileName = worldRules.ResolveModelFileName(type);
            if (bmdFileName == null)
            {
                skippedTypes++;
                continue;
            }

            var modelPath = System.IO.Path.Combine($"Object{worldIndex}", bmdFileName);
            var bmd = await _modelBuilder.LoadBmdAsync(modelPath);
            if (bmd == null)
                continue;

            var materials = await _modelBuilder.LoadModelTexturesAsync(modelPath);
            MuAnimatedMeshController? animationController = null;
            Mesh? sharedMesh;

            bool useAnimatedMesh = enableAnimations && _modelBuilder.HasAnimatedAction(bmd, actionIndex: 0);
            if (useAnimatedMesh)
            {
                animationController = new MuAnimatedMeshController
                {
                    Name = $"Anim_{worldIndex}_{type}"
                };

                parent.AddChild(animationController);
                if (assignEditorOwnership && Engine.IsEditorHint() && parent.Owner != null)
                    animationController.Owner = parent.Owner;

                // Blend rules must be applied before materials are baked into cached animated frames.
                worldRules.ApplyBlendRules(type, materials, bmd.Meshes.Length);

                animationController.Initialize(
                    _modelBuilder,
                    bmd,
                    materials,
                    actionIndex: 0,
                    animationSpeed: worldRules.GetAnimationSpeed(type));

                sharedMesh = animationController.GetCurrentMesh();
                if (sharedMesh == null)
                    continue;
            }
            else
            {
                var mesh = _modelBuilder.BuildMesh(bmd, actionIndex: 0, framePos: 0f);
                if (mesh == null)
                    continue;

                worldRules.ApplyBlendRules(type, materials, mesh.GetSurfaceCount());
                sharedMesh = mesh;
            }

            foreach (var obj in objects)
            {
                var instance = new MeshInstance3D
                {
                    Mesh = sharedMesh,
                    Name = $"Obj_{type}_{placedCount}"
                };

                int surfaceCount = sharedMesh.GetSurfaceCount();
                for (int i = 0; i < materials.Length && i < surfaceCount; i++)
                    instance.SetSurfaceOverrideMaterial(i, materials[i]);

                // Convert MU position to Godot coordinates:
                // MU: X=right, Y=forward, Z=up -> Godot: X=right, Y=up, Z=-forward
                var pos = obj.Position;
                instance.Position = new Vector3(
                    pos.X * MuConfig.WorldToGodot,
                    pos.Z * MuConfig.WorldToGodot,
                    -pos.Y * MuConfig.WorldToGodot
                );

                // Apply rotation: OBJ angles are in degrees, using intrinsic XYZ Euler order.
                // Build quaternion in MU space then transform to Godot coordinates.
                instance.Basis = new Basis(MuAngleToGodotQuat(obj.Angle));

                // Apply scale (model vertices are already in Godot units via MuToGodot).
                float scale = obj.Scale;
                if (scale > 0.001f)
                    instance.Scale = new Vector3(scale, scale, scale);

                parent.AddChild(instance);
                if (assignEditorOwnership && Engine.IsEditorHint() && parent.Owner != null)
                    instance.Owner = parent.Owner;

                worldRules.ApplyInstanceRules(type, instance);

                animationController?.RegisterInstance(instance);
                placedCount++;
            }
        }

        GD.Print($"Placed {placedCount} objects ({objectsByType.Count} unique types, {skippedTypes} unmapped)");
        return parent;
    }

    /// <summary>
    /// Convert MU OBJ rotation (degrees, intrinsic XYZ Euler) to a Godot quaternion.
    /// Replicates MathUtils.AngleQuaternion from the original client, then transforms
    /// the quaternion from MU coordinate space to Godot coordinate space.
    /// </summary>
    private static Quaternion MuAngleToGodotQuat(System.Numerics.Vector3 angleDeg)
    {
        const float deg2Rad = MathF.PI / 180f;
        float halfPitch = angleDeg.X * deg2Rad * 0.5f;
        float halfYaw = angleDeg.Y * deg2Rad * 0.5f;
        float halfRoll = angleDeg.Z * deg2Rad * 0.5f;

        float sr = MathF.Sin(halfPitch);
        float cr = MathF.Cos(halfPitch);
        float sp = MathF.Sin(halfYaw);
        float cp = MathF.Cos(halfYaw);
        float sy = MathF.Sin(halfRoll);
        float cy = MathF.Cos(halfRoll);

        // MU quaternion (intrinsic XYZ / extrinsic ZYX)
        float qw = cr * cp * cy + sr * sp * sy;
        float qx = sr * cp * cy - cr * sp * sy;
        float qy = cr * sp * cy + sr * cp * sy;
        float qz = cr * cp * sy - sr * sp * cy;

        // Transform MU→Godot: Godot.X=MU.X, Godot.Y=MU.Z, Godot.Z=-MU.Y
        return new Quaternion(qx, qz, -qy, qw);
    }
}
