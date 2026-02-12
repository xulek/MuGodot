using Godot;
using System.Runtime.CompilerServices;

namespace MuGodot;

/// <summary>
/// Efficient grass renderer for terrain tiles using MultiMesh instancing.
/// Mirrors MonoGame GrassRenderer behavior: per-tile density + animated blade sway.
/// </summary>
public sealed class MuGrassRenderer
{
    private const int TerrainSize = MuConfig.TerrainSize;

    // Distances are in Godot world units (1 tile == 1 unit). MonoGame used 3000/4000/5000 with TerrainScale=100.
    private const float GrassNearSq = 30f * 30f;
    private const float GrassMidSq = 40f * 40f;
    private const float GrassFarSq = 50f * 50f;

    // Match MonoGame blade dimensions converted to Godot units.
    private const float GrassBladeBaseW = 1.30f; // 130 / 100 (full atlas width; actual blade uses GrassUWidth slice)
    private const float GrassBladeBaseH = 0.45f; // 45 / 100
    private const float GrassScaleMax = 1.5f;
    private const float HeightOffset = 0f; // 55 / 100
    private const float GrassUWidth = 0.30f;

    private static readonly Shader GrassShader = new()
    {
        Code = @"
shader_type spatial;
render_mode cull_disabled, unshaded, depth_draw_opaque;

uniform sampler2D grass_texture : source_color, filter_linear_mipmap, repeat_disable;
uniform float wind_time = 0.0;
uniform float wind_speed = 1.0;
uniform float wind_strength = 1.0;
uniform float alpha_scissor = 0.40;

varying vec2 grass_uv;

void vertex()
{
    // Bottom stays anchored, top sways with wind.
    float top = 1.0 - UV.y;
    float phase = INSTANCE_CUSTOM.b * 6.2831853;
    float amp = mix(0.015, 0.085, INSTANCE_CUSTOM.a) * wind_strength;

    float t = wind_time * wind_speed + phase;
    float sx = sin(t);
    float sz = cos(t * 0.77 + phase * 1.3);

    VERTEX.x += sx * amp * top;
    VERTEX.z += sz * amp * 0.35 * top;

    float u0 = INSTANCE_CUSTOM.r;
    float uw = INSTANCE_CUSTOM.g;
    grass_uv = vec2(mix(u0, u0 + uw, UV.x), UV.y);
}

void fragment()
{
    vec4 tex = texture(grass_texture, grass_uv);
    float a = tex.a * COLOR.a;
    if (a < alpha_scissor)
        discard;

    ALBEDO = tex.rgb * COLOR.rgb;
    // Keep grass in opaque/cutout path for stable depth ordering with transparent effects (fire).
}
"
    };

    private readonly struct GrassTile
    {
        public GrassTile(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; }
        public int Y { get; }
    }

    private readonly struct VisibleGrassTile
    {
        public VisibleGrassTile(int x, int y, float distSq)
        {
            X = x;
            Y = y;
            DistSq = distSq;
        }

        public int X { get; }
        public int Y { get; }
        public float DistSq { get; }
    }

    private readonly MuTerrainBuilder _terrain;
    private readonly List<GrassTile> _grassTiles = new(32768);
    private readonly List<VisibleGrassTile> _visibleTiles = new(8192);
    private readonly bool[] _grassMask = new bool[TerrainSize * TerrainSize];
    private readonly HashSet<byte> _grassTextureIndices = new() { 0 };

    private Node3D? _root;
    private MultiMeshInstance3D? _instanceNode;
    private MultiMesh? _multiMesh;
    private ShaderMaterial? _material;

    private float _refreshTimer;
    private float _windTime;
    private Vector2 _lastRebuildCameraPos;
    private bool _hasLastCameraPos;
    private bool _initialized;

    public int MaxInstances { get; set; } = 44000;
    public float RefreshIntervalSeconds { get; set; } = 0.20f;
    public float WindSpeed { get; set; } = 1.0f;
    public float WindStrength { get; set; } = 1.0f;
    public float GrassBrightness { get; set; } = 2.0f;
    public float AlphaCutoff { get; set; } = 0.40f;
    public float DensityScale { get; set; } = 1.5f;
    public float RebuildCameraMoveThreshold { get; set; } = 0.75f;

    public MuGrassRenderer(MuTerrainBuilder terrain)
    {
        _terrain = terrain;
    }

    public void ConfigureGrass(float brightness, params byte[] textureIndices)
    {
        GrassBrightness = brightness;

        if (textureIndices == null || textureIndices.Length == 0)
            return;

        _grassTextureIndices.Clear();
        for (int i = 0; i < textureIndices.Length; i++)
            _grassTextureIndices.Add(textureIndices[i]);
    }

    public async Task BuildAsync(int worldIndex, Node3D parent, Vector3 cameraPosition)
    {
        Clear();

        var texture = await LoadGrassTextureAsync(worldIndex);
        if (texture == null)
        {
            GD.Print($"[Grass] Texture missing for World{worldIndex}, skipping grass.");
            return;
        }

        BuildGrassTileList();
        if (_grassTiles.Count == 0)
        {
            GD.Print($"[Grass] No eligible grass tiles for World{worldIndex}.");
            return;
        }

        _root = new Node3D { Name = "Grass" };
        parent.AddChild(_root);

        var mesh = CreateBladeMesh();
        _material = new ShaderMaterial
        {
            Shader = GrassShader
        };
        _material.SetShaderParameter("grass_texture", texture);
        _material.SetShaderParameter("wind_speed", WindSpeed);
        _material.SetShaderParameter("wind_strength", WindStrength);
        _material.SetShaderParameter("alpha_scissor", Mathf.Clamp(AlphaCutoff, 0.05f, 0.95f));
        mesh.SurfaceSetMaterial(0, _material);

        _multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            InstanceCount = MaxInstances,
            VisibleInstanceCount = 0,
            Mesh = mesh
        };

        _instanceNode = new MultiMeshInstance3D
        {
            Name = "GrassInstances",
            Multimesh = _multiMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };

        _root.AddChild(_instanceNode);
        _initialized = true;

        RebuildVisibleInstances(cameraPosition);
        GD.Print($"[Grass] Initialized {_grassTiles.Count} candidate tiles for World{worldIndex}.");
    }

    public void Update(double delta, Vector3 cameraPosition)
    {
        if (!_initialized || _material == null || _multiMesh == null)
            return;

        _windTime += (float)delta;
        _material.SetShaderParameter("wind_time", _windTime);
        _material.SetShaderParameter("wind_speed", WindSpeed);
        _material.SetShaderParameter("wind_strength", WindStrength);
        _material.SetShaderParameter("alpha_scissor", Mathf.Clamp(AlphaCutoff, 0.05f, 0.95f));

        _refreshTimer += (float)delta;
        if (_refreshTimer < RefreshIntervalSeconds)
            return;

        _refreshTimer = 0f;
        var currentPos = new Vector2(cameraPosition.X, -cameraPosition.Z);
        if (_hasLastCameraPos)
        {
            float thresholdSq = RebuildCameraMoveThreshold * RebuildCameraMoveThreshold;
            if (_lastRebuildCameraPos.DistanceSquaredTo(currentPos) < thresholdSq)
                return;
        }

        _lastRebuildCameraPos = currentPos;
        _hasLastCameraPos = true;
        RebuildVisibleInstances(cameraPosition);
    }

    public void Clear()
    {
        _initialized = false;
        _refreshTimer = 0f;
        _windTime = 0f;
        _hasLastCameraPos = false;
        _grassTiles.Clear();

        if (_root != null && GodotObject.IsInstanceValid(_root))
            _root.QueueFree();

        _root = null;
        _instanceNode = null;
        _multiMesh = null;
        _material = null;
    }

    private void BuildGrassTileList()
    {
        _grassTiles.Clear();
        Array.Clear(_grassMask, 0, _grassMask.Length);

        for (int y = 0; y < TerrainSize - 1; y++)
        {
            for (int x = 0; x < TerrainSize - 1; x++)
            {
                byte baseTex = _terrain.GetBaseTextureIndexAt(x, y);
                if (_grassTextureIndices.Contains(baseTex))
                {
                    _grassTiles.Add(new GrassTile(x, y));
                    _grassMask[y * TerrainSize + x] = true;
                }
            }
        }
    }

    private void RebuildVisibleInstances(Vector3 cameraPosition)
    {
        if (_multiMesh == null)
            return;

        float camX = cameraPosition.X;
        float camY = -cameraPosition.Z;
        _lastRebuildCameraPos = new Vector2(camX, camY);
        _hasLastCameraPos = true;

        float focalPx = 0f;
        bool hasScreenCull = false;
        var viewport = _instanceNode?.GetViewport();
        var camera = viewport?.GetCamera3D();
        if (viewport != null && camera != null)
        {
            float viewH = Mathf.Max(1f, viewport.GetVisibleRect().Size.Y);
            float fovRad = Mathf.DegToRad(camera.Fov);
            focalPx = viewH / (2f * Mathf.Tan(fovRad * 0.5f));
            hasScreenCull = focalPx > 0.001f;
        }

        int centerX = Mathf.Clamp((int)MathF.Floor(camX), 0, TerrainSize - 1);
        int centerY = Mathf.Clamp((int)MathF.Floor(camY), 0, TerrainSize - 1);

        int farRadius = (int)MathF.Ceiling(MathF.Sqrt(GrassFarSq));
        int minX = Math.Max(0, centerX - farRadius);
        int maxX = Math.Min(TerrainSize - 2, centerX + farRadius);
        int minY = Math.Max(0, centerY - farRadius);
        int maxY = Math.Min(TerrainSize - 2, centerY + farRadius);

        _visibleTiles.Clear();

        for (int y = minY; y <= maxY; y++)
        {
            int rowBase = y * TerrainSize;
            for (int x = minX; x <= maxX; x++)
            {
                if (!_grassMask[rowBase + x])
                    continue;

                float txCenter = x + 0.5f;
                float tyCenter = y + 0.5f;
                float dx = camX - txCenter;
                float dy = camY - tyCenter;
                float distSq = dx * dx + dy * dy;

                if (distSq >= GrassFarSq)
                    continue;

                _visibleTiles.Add(new VisibleGrassTile(x, y, distSq));
            }
        }

        // Prioritize nearest tiles first so grass does not appear only in one camera-side
        // when hitting the per-frame instance budget.
        _visibleTiles.Sort(static (a, b) => a.DistSq.CompareTo(b.DistSq));

        int count = 0;
        for (int t = 0; t < _visibleTiles.Count; t++)
        {
            var tile = _visibleTiles[t];
            int x = tile.X;
            int y = tile.Y;
            float distSq = tile.DistSq;

            int grassPerTile = GetGrassCount(distSq);
            if (grassPerTile == 0)
                continue;
            grassPerTile = ScaleGrassCount(grassPerTile);
            if (grassPerTile == 0)
                continue;
            if (hasScreenCull)
                grassPerTile = AdjustGrassCountForScreenSize(distSq, grassPerTile, focalPx);
            if (grassPerTile == 0)
                continue;

            var tileLight = _terrain.GetTerrainLightColor(x, y);
            var lit = new Color(
                Mathf.Min(tileLight.R * GrassBrightness, 1f),
                Mathf.Min(tileLight.G * GrassBrightness, 1f),
                Mathf.Min(tileLight.B * GrassBrightness, 1f),
                1f);

            for (int i = 0; i < grassPerTile; i++)
            {
                if (count >= MaxInstances)
                {
                    _multiMesh.VisibleInstanceCount = count;
                    return;
                }

                float halfUV = GrassUWidth * 0.5f;
                float maxOffset = 0.5f - halfUV;

                float rx = (PseudoRandom(x, y, 17 + i) * 2f - 1f) * maxOffset;
                float ry = (PseudoRandom(x, y, 91 + i) * 2f - 1f) * maxOffset;
                float worldX = x + 0.5f + rx;
                float worldY = y + 0.5f + ry;

                float ground = _terrain.GetHeightInterpolated(worldX, worldY);
                float scale = Mathf.Lerp(1f, GrassScaleMax, PseudoRandom(x, y, 33 + i));
                float yaw = Mathf.DegToRad(45f + (PseudoRandom(x, y, 57 + i) - 0.5f) * 2f * 90f);

                var basis = Basis.FromEuler(new Vector3(0f, yaw, 0f)).Scaled(new Vector3(scale, scale, scale));
                var transform = new Transform3D(basis, new Vector3(worldX, ground + HeightOffset, -worldY));
                _multiMesh.SetInstanceTransform(count, transform);
                _multiMesh.SetInstanceColor(count, lit);

                float u0 = PseudoRandom(x, y, 123 + i) * (1f - GrassUWidth);
                float phase01 = PseudoRandom(x, y, 211 + i);
                float amp01 = PseudoRandom(x, y, 307 + i);
                _multiMesh.SetInstanceCustomData(count, new Color(u0, GrassUWidth, phase01, amp01));
                count++;
            }
        }

        _multiMesh.VisibleInstanceCount = count;
    }

    private static int GetGrassCount(float distSq)
    {
        if (distSq < GrassNearSq) return 10;
        if (distSq < GrassMidSq) return 4;
        if (distSq < GrassFarSq) return 2;
        return 0;
    }

    private int ScaleGrassCount(int count)
    {
        float scaled = count * Mathf.Max(0.1f, DensityScale);
        return scaled < 0.99f ? 0 : Mathf.Max(1, Mathf.RoundToInt(scaled));
    }

    private static int AdjustGrassCountForScreenSize(float distSq, int grassPerTile, float focalPx)
    {
        if (grassPerTile <= 0)
            return 0;

        float dist = Mathf.Sqrt(distSq);
        if (dist < 0.001f)
            return grassPerTile;
        float bladeHeight = GrassBladeBaseH * GrassScaleMax;
        float pixelHeight = bladeHeight * focalPx / dist;

        if (distSq > GrassMidSq)
            pixelHeight *= 0.75f;

        if (pixelHeight < 1.2f) return 0;
        if (pixelHeight < 2.0f) return Mathf.Max(1, grassPerTile / 6);
        if (pixelHeight < 3.0f) return Mathf.Max(1, grassPerTile / 4);
        if (pixelHeight < 4.5f) return Mathf.Max(1, grassPerTile / 3);
        if (pixelHeight < 6.0f) return Mathf.Max(1, grassPerTile / 2);
        return grassPerTile;
    }

    private async Task<ImageTexture?> LoadGrassTextureAsync(int worldIndex)
    {
        string file = worldIndex switch
        {
            3 => "TileGrass02.ozt",
            _ => "TileGrass01.ozt"
        };

        string path = System.IO.Path.Combine(MuConfig.DataPath, $"World{worldIndex}", file);
        return await MuTextureHelper.LoadTextureAsync(path, generateMipmaps: true);
    }

    private static ArrayMesh CreateBladeMesh()
    {
        // MonoGame blade width multiplies by GrassUWidth (sub-region in grass atlas).
        float halfW = GrassBladeBaseW * GrassUWidth * 0.5f;
        float h = GrassBladeBaseH;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        // Triangle 1
        st.SetUV(new Vector2(0f, 1f)); st.AddVertex(new Vector3(-halfW, 0f, 0f));
        st.SetUV(new Vector2(1f, 1f)); st.AddVertex(new Vector3(halfW, 0f, 0f));
        st.SetUV(new Vector2(0f, 0f)); st.AddVertex(new Vector3(-halfW, h, 0f));

        // Triangle 2
        st.SetUV(new Vector2(1f, 1f)); st.AddVertex(new Vector3(halfW, 0f, 0f));
        st.SetUV(new Vector2(1f, 0f)); st.AddVertex(new Vector3(halfW, h, 0f));
        st.SetUV(new Vector2(0f, 0f)); st.AddVertex(new Vector3(-halfW, h, 0f));

        st.Index();
        return st.Commit() ?? new ArrayMesh();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float PseudoRandom(int x, int y, int salt)
    {
        uint h = (uint)(x * 73856093 ^ y * 19349663 ^ salt * 83492791);
        h ^= h >> 13;
        h *= 0x165667B1u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / 16777215f;
    }
}
