using Godot;

namespace MuGodot.Objects.Worlds.Lorencia;

/// <summary>
/// Lightweight fire visual for Lorencia fire objects (FireLight01/02 and Bonfire01).
/// Uses three additive billboard layers and a flickering omni light.
/// </summary>
[Tool]
public sealed partial class LorenciaFireEmitter : Node3D
{
    private const int FlameLayers = 3;
    private const int FlamesPerLayer = 3;
    private const int TotalFlames = FlameLayers * FlamesPerLayer;
    private const float MuToGodot = MuConfig.WorldToGodot;

    private static readonly string[] EffectTextureExts =
    {
        ".jpg", ".jpeg", ".ozj", ".tga", ".ozt", ".png", ".ozp"
    };

    private readonly MeshInstance3D[] _flames = new MeshInstance3D[TotalFlames];
    private readonly Vector3[] _baseLocalPositions = new Vector3[TotalFlames];
    private readonly float[] _phase = new float[TotalFlames];
    private readonly float[] _drift = new float[TotalFlames];
    private readonly float[] _baseScales = new float[TotalFlames];

    private OmniLight3D? _light;
    private Vector3 _lightBasePos;
    private float _lightBaseEnergy;
    private float _lightBaseRange;
    private float _time;
    private float _timeOffset;
    private float _editorTickAccumulator;
    private bool _editorCulled;
    private bool _initializationRequested;
    private bool _initialized;

    public static bool EditorAnimationEnabled { get; set; } = false;
    public static bool EditorKeepLightsWhenAnimationDisabled { get; set; } = false;
    public static float EditorAnimationFps { get; set; } = 18f;
    public static bool EditorDistanceCulling { get; set; } = true;
    public static float EditorMaxActiveDistance { get; set; } = 65f;
    public static Vector3 EditorCameraPosition { get; private set; } = Vector3.Zero;

    [Export]
    public short SourceType { get; set; }

    [Export]
    public int VariantIndex { get; set; }

    private sealed class FlameAssets
    {
        public required QuadMesh QuadMesh { get; init; }
        public required StandardMaterial3D[] LayerMaterials { get; init; }
    }

    private readonly struct FireProfile
    {
        public FireProfile(
            float anchorRatio,
            float anchorOffsetXRatio,
            float baseLayerOffset,
            float midLayerOffset,
            float topLayerOffset,
            float spreadRatio,
            float baseScale,
            float midScale,
            float topScale,
            float lightRange,
            float lightEnergy,
            bool hasManualAnchor,
            Vector3 manualAnchor)
        {
            AnchorRatio = anchorRatio;
            AnchorOffsetXRatio = anchorOffsetXRatio;
            BaseLayerOffset = baseLayerOffset;
            MidLayerOffset = midLayerOffset;
            TopLayerOffset = topLayerOffset;
            SpreadRatio = spreadRatio;
            BaseScale = baseScale;
            MidScale = midScale;
            TopScale = topScale;
            LightRange = lightRange;
            LightEnergy = lightEnergy;
            HasManualAnchor = hasManualAnchor;
            ManualAnchor = manualAnchor;
        }

        public float AnchorRatio { get; }
        public float AnchorOffsetXRatio { get; }
        public float BaseLayerOffset { get; }
        public float MidLayerOffset { get; }
        public float TopLayerOffset { get; }
        public float SpreadRatio { get; }
        public float BaseScale { get; }
        public float MidScale { get; }
        public float TopScale { get; }
        public float LightRange { get; }
        public float LightEnergy { get; }
        public bool HasManualAnchor { get; }
        public Vector3 ManualAnchor { get; }
    }

    private static readonly object AssetLock = new();
    private static Task<FlameAssets?>? _assetsTask;

    public static void ResetSharedAssetsForReload()
    {
        lock (AssetLock)
        {
            _assetsTask = null;
        }
    }

    public static void AttachTo(MeshInstance3D instance, short sourceType, int variantIndex = 0, string? nodeName = null)
    {
        if (instance == null || !GodotObject.IsInstanceValid(instance))
            return;

        string resolvedNodeName = nodeName
            ?? (variantIndex == 0 ? "LorenciaFireEmitter" : $"LorenciaFireEmitter_{variantIndex}");

        var existing = instance.GetNodeOrNull<LorenciaFireEmitter>(resolvedNodeName);
        if (existing != null)
            return;

        var emitter = new LorenciaFireEmitter
        {
            Name = resolvedNodeName,
            SourceType = sourceType,
            VariantIndex = variantIndex
        };

        instance.AddChild(emitter);
        if (instance.Owner != null)
            emitter.Owner = instance.Owner;
    }

    public static void SetEditorCameraPosition(Vector3 position)
    {
        EditorCameraPosition = position;
    }

    public override void _Ready()
    {
        if (Engine.IsEditorHint() && !ShouldInitializeInEditor())
            return;

        RequestInitialization();
    }

    public override void _Process(double delta)
    {
        if (!_initialized)
        {
            if (Engine.IsEditorHint() && ShouldInitializeInEditor())
                RequestInitialization();

            return;
        }

        float dt = (float)delta;

        if (Engine.IsEditorHint())
        {
            if (!EditorAnimationEnabled)
            {
                if (_light != null && GodotObject.IsInstanceValid(_light))
                    _light.Visible = EditorKeepLightsWhenAnimationDisabled;

                return;
            }

            if (_light != null && GodotObject.IsInstanceValid(_light) && !_light.Visible)
                _light.Visible = true;

            if (EditorDistanceCulling)
            {
                float maxDistance = Mathf.Max(5f, EditorMaxActiveDistance);
                float maxDistanceSq = maxDistance * maxDistance;
                bool shouldCull = GlobalPosition.DistanceSquaredTo(EditorCameraPosition) > maxDistanceSq;
                if (shouldCull != _editorCulled)
                {
                    _editorCulled = shouldCull;
                    SetEmitterVisible(!_editorCulled);
                }

                if (_editorCulled)
                    return;
            }
            else if (_editorCulled)
            {
                _editorCulled = false;
                SetEmitterVisible(true);
            }

            float fps = Mathf.Clamp(EditorAnimationFps, 1f, 120f);
            float tickStep = 1f / fps;
            _editorTickAccumulator += dt;
            if (_editorTickAccumulator < tickStep)
                return;

            dt = _editorTickAccumulator;
            _editorTickAccumulator = 0f;
        }

        _time += dt;
        float t = _time + _timeOffset;

        for (int i = 0; i < _flames.Length; i++)
        {
            var flame = _flames[i];
            if (flame == null)
                continue;

            float phase = _phase[i];
            float drift = _drift[i];
            float burst = MathF.Max(0f, MathF.Sin(t * (4.4f + drift * 5.1f) + phase * 2.3f));
            float lateral = drift * (1.0f + burst * 1.4f);
            float swayX =
                (MathF.Sin(t * (2.7f + drift * 3.1f) + phase)
                 + MathF.Sin(t * (6.1f + drift * 4.7f) + phase * 0.4f) * 0.42f) * lateral;
            float swayZ =
                (MathF.Cos(t * (2.2f + drift * 2.8f) + phase * 1.1f)
                 + MathF.Cos(t * (5.3f + drift * 3.9f) + phase * 0.8f) * 0.36f) * lateral * 0.72f;
            float bob = MathF.Sin(t * (5.1f + drift * 4.0f) + phase * 0.9f) * 0.012f + burst * 0.008f;

            flame.Position = _baseLocalPositions[i] + new Vector3(swayX, bob, swayZ);

            float pulse =
                0.82f
                + MathF.Sin(t * (5.8f + drift * 2.6f) + phase) * 0.12f
                + MathF.Sin(t * (10.3f + drift * 1.6f) + phase * 0.7f) * 0.08f;
            float burstScale = 1f + burst * 0.22f;
            float sx = _baseScales[i] * MathF.Max(0.56f, pulse);
            float sy = _baseScales[i] * MathF.Max(0.46f, pulse * burstScale * 0.86f);
            flame.Scale = new Vector3(sx, sy, sx);
        }

        if (_light != null)
        {
            float lum = 0.92f
                        + MathF.Sin(t * 5.5f) * 0.20f
                        + MathF.Sin(t * 9.0f + _timeOffset) * 0.10f;
            _light.LightEnergy = _lightBaseEnergy * MathF.Max(0.35f, lum);
            _light.OmniRange = _lightBaseRange * (0.95f + MathF.Sin(t * 3.1f + _timeOffset) * 0.05f);
            _light.Position = _lightBasePos + new Vector3(
                MathF.Sin(t * 1.7f) * 0.015f,
                0f,
                MathF.Cos(t * 1.3f) * 0.015f);
        }
    }

    private static bool ShouldInitializeInEditor()
    {
        return EditorAnimationEnabled || EditorKeepLightsWhenAnimationDisabled;
    }

    private void RequestInitialization()
    {
        if (_initialized || _initializationRequested)
            return;

        _initializationRequested = true;
        _ = EnsureInitializedAsync();
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
            return;

        try
        {
            var host = GetParent() as MeshInstance3D;
            if (host == null || !GodotObject.IsInstanceValid(host))
                return;

            var assets = await GetAssetsAsync();
            if (assets == null)
            {
                GD.PrintErr("[Fire] Failed to initialize Lorencia fire emitter: missing fire textures.");
                return;
            }

            _timeOffset = Hash01((int)SourceType * 131 + GetInstanceId().GetHashCode() * 7) * 200f;
            BuildFlames(host, assets);
            _initialized = true;
        }
        finally
        {
            if (!_initialized)
                _initializationRequested = false;
        }
    }

    private void BuildFlames(MeshInstance3D host, FlameAssets assets)
    {
        var profile = GetProfile(SourceType, VariantIndex);
        var mesh = host.Mesh;
        var aabb = mesh != null
            ? mesh.GetAabb()
            : new Aabb(new Vector3(-0.5f, 0f, -0.5f), new Vector3(1f, 1f, 1f));

        float sizeX = MathF.Max(aabb.Size.X, 0.25f);
        float sizeY = MathF.Max(aabb.Size.Y, 0.25f);
        float sizeZ = MathF.Max(aabb.Size.Z, 0.25f);

        var anchor = profile.HasManualAnchor
            ? profile.ManualAnchor
            : new Vector3(
                aabb.Position.X + sizeX * (0.5f + profile.AnchorOffsetXRatio),
                aabb.Position.Y + sizeY * profile.AnchorRatio,
                aabb.Position.Z + sizeZ * 0.5f);

        float baseLayerY = sizeY * profile.BaseLayerOffset;
        float midLayerY = sizeY * profile.MidLayerOffset;
        float topLayerY = sizeY * profile.TopLayerOffset;
        float spread = MathF.Max(MathF.Min(sizeX, sizeZ) * profile.SpreadRatio, 0.04f);

        _light = new OmniLight3D
        {
            Name = "FireLight",
            LightColor = new Color(1f, 0.66f, 0.36f, 1f),
            OmniRange = profile.LightRange,
            LightEnergy = profile.LightEnergy,
            ShadowEnabled = false
        };
        _lightBasePos = anchor + new Vector3(0f, midLayerY, 0f);
        _lightBaseRange = profile.LightRange;
        _lightBaseEnergy = profile.LightEnergy;
        _light.Position = _lightBasePos;
        AddOwnedChild(_light);

        int index = 0;
        for (int layer = 0; layer < FlameLayers; layer++)
        {
            float layerY = layer switch
            {
                0 => baseLayerY,
                1 => midLayerY,
                _ => topLayerY
            };

            float layerScale = layer switch
            {
                0 => profile.BaseScale,
                1 => profile.MidScale,
                _ => profile.TopScale
            };

            for (int i = 0; i < FlamesPerLayer; i++)
            {
                float angle = Hash01(index * 239 + 17) * Mathf.Tau;
                float radius = spread * (0.35f + 0.65f * Hash01(index * 547 + 53));
                float jitterY = (Hash01(index * 887 + 97) - 0.5f) * sizeY * 0.03f;
                float x = MathF.Cos(angle) * radius;
                float z = MathF.Sin(angle) * radius;

                var flame = new MeshInstance3D
                {
                    Name = $"Flame_{layer}_{i}",
                    Mesh = assets.QuadMesh,
                    MaterialOverride = assets.LayerMaterials[layer],
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                };

                float scale = layerScale * (0.85f + 0.30f * Hash01(index * 311 + 131));
                flame.Scale = new Vector3(scale, scale, scale);
                flame.Position = anchor + new Vector3(x, layerY + jitterY, z);
                AddOwnedChild(flame);

                _flames[index] = flame;
                _baseLocalPositions[index] = flame.Position;
                _baseScales[index] = scale;
                _phase[index] = Hash01(index * 463 + 191) * Mathf.Tau;
                _drift[index] = 0.035f + 0.045f * Hash01(index * 601 + 223);
                index++;
            }
        }
    }

    private static FireProfile GetProfile(short type, int variantIndex)
    {
        return type switch
        {
            // FireLight01
            50 => new FireProfile(
                anchorRatio: 1.08f,
                anchorOffsetXRatio: 0.00f,
                baseLayerOffset: 0.00f,
                midLayerOffset: 0.10f,
                topLayerOffset: 0.22f,
                spreadRatio: 0.08f,
                baseScale: 0.55f,
                midScale: 0.70f,
                topScale: 0.80f,
                lightRange: 4.0f,
                lightEnergy: 1.7f,
                hasManualAnchor: false,
                manualAnchor: Vector3.Zero),

            // FireLight02
            51 => new FireProfile(
                anchorRatio: 1.03f,
                anchorOffsetXRatio: 0.08f,
                baseLayerOffset: 0.00f,
                midLayerOffset: 0.10f,
                topLayerOffset: 0.22f,
                spreadRatio: 0.07f,
                baseScale: 0.55f,
                midScale: 0.70f,
                topScale: 0.80f,
                lightRange: 4.0f,
                lightEnergy: 1.7f,
                hasManualAnchor: false,
                manualAnchor: Vector3.Zero),

            // Bonfire01
            52 => new FireProfile(
                anchorRatio: 0.42f,
                anchorOffsetXRatio: 0.00f,
                baseLayerOffset: 0.00f,
                midLayerOffset: 0.13f,
                topLayerOffset: 0.27f,
                spreadRatio: 0.20f,
                baseScale: 0.95f,
                midScale: 1.08f,
                topScale: 1.22f,
                lightRange: 4.8f,
                lightEnergy: 2.1f,
                hasManualAnchor: false,
                manualAnchor: Vector3.Zero),

            // Bridge01 has two fire sets in MonoGame (left/right side of the bridge).
            80 => new FireProfile(
                anchorRatio: 0.72f,
                anchorOffsetXRatio: 0.00f,
                baseLayerOffset: 0.00f,
                midLayerOffset: 0.13f,
                topLayerOffset: 0.26f,
                spreadRatio: 0.09f,
                baseScale: 0.62f,
                midScale: 0.78f,
                topScale: 0.88f,
                lightRange: 4.6f,
                lightEnergy: 2.0f,
                hasManualAnchor: true,
                manualAnchor: variantIndex == 0
                    ? MuToGodotPosition(100f, 205f, 48f)
                    : MuToGodotPosition(100f, -215f, 48f)),

            _ => new FireProfile(
                anchorRatio: 0.75f,
                anchorOffsetXRatio: 0.00f,
                baseLayerOffset: 0.00f,
                midLayerOffset: 0.12f,
                topLayerOffset: 0.24f,
                spreadRatio: 0.08f,
                baseScale: 0.55f,
                midScale: 0.75f,
                topScale: 0.92f,
                lightRange: 4.0f,
                lightEnergy: 1.7f,
                hasManualAnchor: false,
                manualAnchor: Vector3.Zero)
        };
    }

    private static Task<FlameAssets?> GetAssetsAsync()
    {
        lock (AssetLock)
        {
            _assetsTask ??= LoadAssetsAsync();
            return _assetsTask;
        }
    }

    private static async Task<FlameAssets?> LoadAssetsAsync()
    {
        var fire01 = await LoadEffectTextureAsync("firehik01");
        var fire02 = await LoadEffectTextureAsync("firehik02");
        var fire03 = await LoadEffectTextureAsync("firehik03");

        if (fire01 == null || fire02 == null || fire03 == null)
            return null;

        var quad = new QuadMesh
        {
            Size = new Vector2(0.76f, 0.94f)
        };

        return new FlameAssets
        {
            QuadMesh = quad,
            LayerMaterials = new[]
            {
                CreateFlameMaterial(fire03),
                CreateFlameMaterial(fire02),
                CreateFlameMaterial(fire01)
            }
        };
    }

    private static async Task<ImageTexture?> LoadEffectTextureAsync(string fileNoExt)
    {
        var path = ResolveEffectTexturePath(fileNoExt);
        if (path == null)
            return null;

        // Fire sprites use alpha edges; mipmaps can introduce bright fringes on transparent borders.
        return await MuTextureHelper.LoadTextureAsync(path, generateMipmaps: false);
    }

    private static string? ResolveEffectTexturePath(string fileNoExt)
    {
        var effectDir = System.IO.Path.Combine(MuConfig.DataPath, "Effect");
        if (!System.IO.Directory.Exists(effectDir))
            return null;

        for (int i = 0; i < EffectTextureExts.Length; i++)
        {
            var candidate = System.IO.Path.Combine(effectDir, fileNoExt + EffectTextureExts[i]);
            if (System.IO.File.Exists(candidate))
                return candidate;
        }

        var files = System.IO.Directory.GetFiles(effectDir);
        for (int i = 0; i < EffectTextureExts.Length; i++)
        {
            var wanted = fileNoExt + EffectTextureExts[i];
            for (int j = 0; j < files.Length; j++)
            {
                if (string.Equals(System.IO.Path.GetFileName(files[j]), wanted, StringComparison.OrdinalIgnoreCase))
                    return files[j];
            }
        }

        return null;
    }

    private static StandardMaterial3D CreateFlameMaterial(Texture2D texture)
    {
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            DisableFog = true,
            AlbedoTexture = texture,
            AlbedoColor = new Color(1f, 1f, 1f, 1f),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear
        };

        // Match MonoGame SpriteObject fire pass: depth read enabled, depth write disabled.
        mat.NoDepthTest = false;
        mat.DepthTest = BaseMaterial3D.DepthTestEnum.Default;

        return mat;
    }

    private void AddOwnedChild(Node child)
    {
        AddChild(child);

        // Never force editor scene ownership for runtime-generated fire nodes.
        // Otherwise they get serialized into Main.tscn and bloat scene load times.
        if (Engine.IsEditorHint() && Owner != null)
            child.Owner = Owner;
    }

    private void SetEmitterVisible(bool visible)
    {
        for (int i = 0; i < _flames.Length; i++)
        {
            var flame = _flames[i];
            if (flame != null && GodotObject.IsInstanceValid(flame))
                flame.Visible = visible;
        }

        if (_light != null && GodotObject.IsInstanceValid(_light))
            _light.Visible = visible;
    }

    private static Vector3 MuToGodotPosition(float x, float y, float z)
    {
        return new Vector3(x * MuToGodot, z * MuToGodot, -y * MuToGodot);
    }

    private static float Hash01(int n)
    {
        unchecked
        {
            uint h = (uint)n;
            h ^= h >> 16;
            h *= 0x7feb352d;
            h ^= h >> 15;
            h *= 0x846ca68b;
            h ^= h >> 16;
            return (h & 0x00FFFFFFu) / 16777215f;
        }
    }
}
