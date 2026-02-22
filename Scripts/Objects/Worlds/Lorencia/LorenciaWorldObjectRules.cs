using Godot;
using static MuGodot.Objects.Worlds.ObjectMaterialBlendHelper;

namespace MuGodot.Objects.Worlds.Lorencia;

public sealed class LorenciaWorldObjectRules : IWorldObjectRules
{
    public static LorenciaWorldObjectRules Instance { get; } = new();

    private LorenciaWorldObjectRules()
    {
    }

    public string? ResolveModelFileName(short type)
    {
        return type switch
        {
            >= 0 and <= 12 => $"Tree{(type + 1):D2}.bmd",
            >= 20 and <= 27 => $"Grass{(type - 20 + 1):D2}.bmd",
            >= 30 and <= 34 => $"Stone{(type - 30 + 1):D2}.bmd",
            >= 40 and <= 42 => $"StoneStatue{(type - 40 + 1):D2}.bmd",
            43 => "SteelStatue01.bmd",
            >= 44 and <= 46 => $"Tomb{(type - 44 + 1):D2}.bmd",
            >= 50 and <= 51 => $"FireLight{(type - 50 + 1):D2}.bmd",
            52 => "Bonfire01.bmd",
            55 => "DoungeonGate01.bmd",
            >= 56 and <= 57 => $"MerchantAnimal{(type - 56 + 1):D2}.bmd",
            58 => "TreasureDrum01.bmd",
            59 => "TreasureChest01.bmd",
            60 => "Ship01.bmd",
            >= 65 and <= 67 => $"SteelWall{(type - 65 + 1):D2}.bmd",
            68 => "SteelDoor01.bmd",
            >= 69 and <= 74 => $"StoneWall{(type - 69 + 1):D2}.bmd",
            >= 75 and <= 78 => $"StoneMuWall{(type - 75 + 1):D2}.bmd",
            80 => "Bridge01.bmd",
            >= 81 and <= 84 => $"Fence{(type - 81 + 1):D2}.bmd",
            85 => "BridgeStone01.bmd",
            90 => "StreetLight01.bmd",
            >= 91 and <= 93 => $"Cannon{(type - 91 + 1):D2}.bmd",
            95 => "Curtain01.bmd",
            >= 96 and <= 97 => $"Sign{(type - 96 + 1):D2}.bmd",
            >= 98 and <= 101 => $"Carriage{(type - 98 + 1):D2}.bmd",
            >= 102 and <= 103 => $"Straw{(type - 102 + 1):D2}.bmd",
            105 => "Waterspout01.bmd",
            >= 106 and <= 109 => $"Well{(type - 106 + 1):D2}.bmd",
            110 => "Hanging01.bmd",
            111 => "Stair01.bmd",
            >= 115 and <= 119 => $"House{(type - 115 + 1):D2}.bmd",
            120 => "Tent01.bmd",
            >= 121 and <= 126 => $"HouseWall{(type - 121 + 1):D2}.bmd",
            >= 127 and <= 129 => $"HouseEtc{(type - 127 + 1):D2}.bmd",
            >= 130 and <= 132 => null, // LightObject - no model
            133 => null,               // RestPlaceObject - dummy
            >= 140 and <= 146 => $"Furniture{(type - 140 + 1):D2}.bmd",
            150 => "Candle01.bmd",
            >= 151 and <= 153 => $"Beer{(type - 151 + 1):D2}.bmd",
            _ => null,
        };
    }

    public float GetAnimationSpeed(short type)
    {
        _ = type;
        return 4.0f;
    }

    public void ApplyBlendRules(short type, StandardMaterial3D[] materials, int surfaceCount)
    {
        if (materials.Length == 0 || surfaceCount <= 0)
            return;

        int max = Math.Min(materials.Length, surfaceCount);
        if (max <= 0)
            return;

        bool forceAlphaBlendForWholeObject = false;
        bool forceOpaqueForWholeObject = false;
        bool forceAlphaCutoutForWholeObject = false;
        float? fixedAlpha = null;
        Span<int> additiveMeshes = stackalloc int[4];
        int additiveCount = 0;
        Span<int> alphaMeshes = stackalloc int[4];
        int alphaCount = 0;
        Span<int> opaqueMeshes = stackalloc int[4];
        int opaqueCount = 0;
        Span<int> hiddenMeshes = stackalloc int[2];
        int hiddenCount = 0;

        // Rules mirrored from Client.Main/Objects/Worlds/Lorencia/*.cs
        switch (type)
        {
            // Tree01..13, Grass01..08, Fence01..04:
            // In MonoGame these are effectively alpha-tested (RGBA mesh + AlphaTestEffect).
            case >= 0 and <= 12:
            case >= 20 and <= 27:
            case >= 56 and <= 57:
            case >= 65 and <= 68:
            case >= 81 and <= 84:
                forceAlphaCutoutForWholeObject = true;
                break;

            // Bridge01 => skip mesh 1 (DrawMesh early return)
            case 80:
                hiddenMeshes[hiddenCount++] = 1;
                break;

            // FireLight01/02 => BlendMesh = 2
            case 50:
            case 51:
                additiveMeshes[additiveCount++] = 2;
                break;

            // Bonfire01 => BlendMesh = 1
            case 52:
                additiveMeshes[additiveCount++] = 1;
                break;

            // StreetLight01 => BlendMesh = 1, Additive
            case 90:
                additiveMeshes[additiveCount++] = 1;
                break;

            // Waterspout01 => BlendMesh = 3, Additive
            case 105:
                additiveMeshes[additiveCount++] = 3;
                break;

            // Candle01 => BlendMesh = 1
            case 150:
                additiveMeshes[additiveCount++] = 1;
                break;

            // Carriage01..04 => mesh 2 uses InverseDestinationBlend (approx: alpha blend in Godot)
            case >= 98 and <= 101:
                alphaMeshes[alphaCount++] = 2;
                // Carriage04 (type 101) => mesh 1 forced opaque custom state
                if (type == 101)
                    opaqueMeshes[opaqueCount++] = 1;
                break;

            // HouseWall02 => BlendMesh = 4, Additive (special flicker mesh in reference client)
            case 122:
                additiveMeshes[additiveCount++] = 4;
                break;

            // Straw01 => force opaque blend/depth state
            case 102:
                forceOpaqueForWholeObject = true;
                break;
        }

        if (forceOpaqueForWholeObject)
        {
            for (int i = 0; i < max; i++)
            {
                var mat = materials[i];
                if (mat == null)
                    continue;

                SetOpaque(mat);
            }
        }

        if (forceAlphaCutoutForWholeObject)
        {
            for (int i = 0; i < max; i++)
            {
                var mat = materials[i];
                if (mat == null)
                    continue;

                SetAlphaCutout(mat);
            }
        }

        if (forceAlphaBlendForWholeObject || fixedAlpha.HasValue)
        {
            for (int i = 0; i < max; i++)
            {
                var mat = materials[i];
                if (mat == null)
                    continue;

                SetAlphaBlend(mat);
                if (fixedAlpha.HasValue)
                {
                    var c = mat.AlbedoColor;
                    mat.AlbedoColor = new Color(c.R, c.G, c.B, fixedAlpha.Value);
                }
            }
        }

        for (int i = 0; i < additiveCount; i++)
        {
            int meshIndex = additiveMeshes[i];
            if (meshIndex < 0 || meshIndex >= max)
                continue;

            var mat = materials[meshIndex];
            if (mat == null)
                continue;

            SetAlphaBlend(mat);
            mat.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
            mat.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled;
            if (type is 50 or 51 or 52 or 80)
            {
                mat.DisableFog = true;
                mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
            }
        }

        for (int i = 0; i < alphaCount; i++)
        {
            int meshIndex = alphaMeshes[i];
            if (meshIndex < 0 || meshIndex >= max)
                continue;

            var mat = materials[meshIndex];
            if (mat == null)
                continue;

            SetAlphaBlend(mat);
        }

        for (int i = 0; i < opaqueCount; i++)
        {
            int meshIndex = opaqueMeshes[i];
            if (meshIndex < 0 || meshIndex >= max)
                continue;

            var mat = materials[meshIndex];
            if (mat == null)
                continue;

            SetOpaque(mat);
        }

        for (int i = 0; i < hiddenCount; i++)
        {
            int meshIndex = hiddenMeshes[i];
            if (meshIndex < 0 || meshIndex >= max)
                continue;

            var mat = materials[meshIndex];
            if (mat == null)
                continue;

            SetAlphaBlend(mat);
            mat.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled;
            var c = mat.AlbedoColor;
            mat.AlbedoColor = new Color(c.R, c.G, c.B, 0f);
        }
    }

    public void ApplyInstanceRules(short type, MeshInstance3D instance)
    {
        if (instance == null)
            return;

        if (type is 50 or 51 or 52)
            LorenciaFireEmitter.AttachTo(instance, type);

        if (type == 80)
        {
            LorenciaFireEmitter.AttachTo(instance, type, variantIndex: 0, nodeName: "LorenciaBridgeFireA");
            LorenciaFireEmitter.AttachTo(instance, type, variantIndex: 1, nodeName: "LorenciaBridgeFireB");
        }

        // Candle01: small flickering warm-yellow light above the flame mesh.
        if (type == 150)
            LorenciaPointLightEmitter.AttachTo(instance, type);

        // StreetLight01: static warm-white lamp elevated above the lantern.
        if (type == 90)
            LorenciaPointLightEmitter.AttachTo(instance, type);

        // DungeonGate01: two flickering fire-colored lights on the flanking pillars.
        if (type == 55)
        {
            LorenciaPointLightEmitter.AttachTo(instance, type, variantIndex: 0, nodeName: "LorenciaDungeonGateLightA");
            LorenciaPointLightEmitter.AttachTo(instance, type, variantIndex: 1, nodeName: "LorenciaDungeonGateLightB");
        }
    }
}
