using Client.Data.ATT;
using Client.Data.MAP;
using Client.Data.OZB;
using Godot;

namespace MuGodot;

/// <summary>
/// Builds a Godot 3D terrain mesh from MU Online terrain data files.
/// Loads heightmap (OZB), texture mapping (MAP), attributes (ATT), and terrain textures.
/// </summary>
public class MuTerrainBuilder
{
    private const int Size = MuConfig.TerrainSize; // 256
    private const int SizeMask = Size - 1;
    private const byte WaterTextureIndex = 5;
    private static readonly Vector3 DefaultLightDirection = new Vector3(1f, 0f, 1f).Normalized();

    private static readonly Shader WaterOpaqueShader = new Shader
    {
        Code = @"
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque, unshaded;

uniform sampler2D albedo_texture : source_color;
uniform vec2 water_flow_direction = vec2(1.0, 0.0);
uniform float water_total = 0.0;
uniform float distortion_amplitude = 0.0;
uniform float distortion_frequency = 1.0;

varying vec2 flow_uv;

void vertex()
{
    vec2 uv = UV + water_flow_direction * water_total;
    float f = max(0.01, distortion_frequency);
    float wrap_period = 6.2831853 / f;
    float phase = mod(water_total, wrap_period);

    uv.x += sin((UV.x + phase) * f) * distortion_amplitude;
    uv.y += cos((UV.y + phase) * f) * distortion_amplitude;
    flow_uv = uv;
}

void fragment()
{
    vec4 tex = texture(albedo_texture, flow_uv);
    ALBEDO = tex.rgb * COLOR.rgb;
}
"
    };

    private static readonly Shader WaterAlphaShader = new Shader
    {
        Code = @"
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque, blend_mix, unshaded;

uniform sampler2D albedo_texture : source_color;
uniform vec2 water_flow_direction = vec2(1.0, 0.0);
uniform float water_total = 0.0;
uniform float distortion_amplitude = 0.0;
uniform float distortion_frequency = 1.0;

varying vec2 flow_uv;

void vertex()
{
    vec2 uv = UV + water_flow_direction * water_total;
    float f = max(0.01, distortion_frequency);
    float wrap_period = 6.2831853 / f;
    float phase = mod(water_total, wrap_period);

    uv.x += sin((UV.x + phase) * f) * distortion_amplitude;
    uv.y += cos((UV.y + phase) * f) * distortion_amplitude;
    flow_uv = uv;
}

void fragment()
{
    vec4 tex = texture(albedo_texture, flow_uv);
    ALBEDO = tex.rgb * COLOR.rgb;
    ALPHA = tex.a * COLOR.a;
}
"
    };

    private TerrainAttribute? _attributes;
    private TerrainMapping _mapping;
    private System.Drawing.Color[]? _heightMap;
    private System.Drawing.Color[]? _lightMap;
    private Color[]? _finalLightMap;
    private readonly Dictionary<int, ImageTexture> _textures = new();
    private readonly Dictionary<int, string> _textureMappingFiles;
    private readonly List<ShaderMaterial> _waterMaterials = new();
    private float _waterTotal = 0f;
    private Vector2 _waterFlowDirection = Vector2.Right;
    private Vector3 _lightDirection = DefaultLightDirection;
    private float _ambientLight = 0.25f;

    public float WaterSpeed { get; set; } = 0f;
    public float DistortionAmplitude { get; set; } = 0f;
    public float DistortionFrequency { get; set; } = 0f;
    public float AmbientLight
    {
        get => _ambientLight;
        set
        {
            _ambientLight = value;
            RebuildFinalLightMap();
        }
    }

    public Vector3 LightDirection
    {
        get => _lightDirection;
        set
        {
            _lightDirection = value.LengthSquared() < 0.0001f ? DefaultLightDirection : value.Normalized();
            RebuildFinalLightMap();
        }
    }

    public void SetStaticLighting(Vector3 lightDirection, float ambientLight)
    {
        _lightDirection = lightDirection.LengthSquared() < 0.0001f ? DefaultLightDirection : lightDirection.Normalized();
        _ambientLight = ambientLight;
        RebuildFinalLightMap();
    }

    public Vector2 WaterFlowDirection
    {
        get => _waterFlowDirection;
        set
        {
            _waterFlowDirection = value.LengthSquared() < 0.0001f
                ? Vector2.Right
                : value.Normalized();

            SyncWaterMaterialParameters();
        }
    }

    public MuTerrainBuilder()
    {
        _textureMappingFiles = GetDefaultTextureMappings();
    }

    /// <summary>
    /// Load all terrain data for a given world index.
    /// </summary>
    public async Task LoadAsync(int worldIndex)
    {
        var worldFolder = System.IO.Path.Combine(MuConfig.DataPath, $"World{worldIndex}");
        if (!System.IO.Directory.Exists(worldFolder))
        {
            GD.PrintErr($"World folder not found: {worldFolder}");
            return;
        }

        var tasks = new List<Task>();

        // Load ATT (walkability)
        var attPath = System.IO.Path.Combine(worldFolder, $"EncTerrain{worldIndex}.att");
        if (System.IO.File.Exists(attPath))
            tasks.Add(new ATTReader().Load(attPath).ContinueWith(t => _attributes = t.Result));

        // Load heightmap
        var heightPath = System.IO.Path.Combine(worldFolder, "TerrainHeight.OZB");
        if (System.IO.File.Exists(heightPath))
            tasks.Add(new OZBReader().Load(heightPath).ContinueWith(t => _heightMap = t.Result.Data));

        // Load terrain mapping (texture layers)
        var mapPath = System.IO.Path.Combine(worldFolder, $"EncTerrain{worldIndex}.map");
        if (System.IO.File.Exists(mapPath))
            tasks.Add(new MapReader().Load(mapPath).ContinueWith(t => _mapping = t.Result));

        // Load lightmap
        var lightPath = System.IO.Path.Combine(worldFolder, "TerrainLight.OZB");
        if (System.IO.File.Exists(lightPath))
            tasks.Add(new OZBReader().Load(lightPath).ContinueWith(t => _lightMap = t.Result.Data));

        await Task.WhenAll(tasks);
        RebuildFinalLightMap();

        // Load terrain textures
        await LoadTerrainTextures(worldFolder, worldIndex);

        GD.Print($"Terrain data loaded for World{worldIndex}");
    }

    private async Task LoadTerrainTextures(string worldFolder, int worldIndex)
    {
        // Load mapped textures (grass, ground, water, rock, etc.)
        foreach (var kvp in _textureMappingFiles)
        {
            var path = System.IO.Path.Combine(worldFolder, kvp.Value);
            if (System.IO.File.Exists(path))
            {
                var tex = await MuTextureHelper.LoadTextureAsync(path);
                if (tex != null)
                    _textures[kvp.Key] = tex;
            }
        }

        // Load extended tiles (ExtTile01..ExtTile16)
        for (int i = 1; i <= 16; i++)
        {
            var path = System.IO.Path.Combine(worldFolder, $"ExtTile{i:00}.ozj");
            if (System.IO.File.Exists(path))
            {
                var tex = await MuTextureHelper.LoadTextureAsync(path);
                if (tex != null)
                    _textures[13 + i] = tex;
            }
        }

        GD.Print($"Loaded {_textures.Count} terrain textures");
    }

    /// <summary>
    /// Get the terrain height at a grid position (0-255).
    /// </summary>
    public float GetHeight(int x, int y)
    {
        if (_heightMap == null) return 0f;
        x = Math.Clamp(x, 0, Size - 1);
        y = Math.Clamp(y, 0, Size - 1);
        int index = y * Size + x;
        return _heightMap[index].R * MuConfig.HeightMultiplier * MuConfig.WorldToGodot;
    }

    /// <summary>
    /// Bilinear height sample in terrain tile coordinates.
    /// </summary>
    public float GetHeightInterpolated(float x, float y)
    {
        if (_heightMap == null)
            return 0f;

        x = Mathf.Clamp(x, 0f, Size - 1.001f);
        y = Mathf.Clamp(y, 0f, Size - 1.001f);

        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        int x1 = Math.Min(x0 + 1, Size - 1);
        int y1 = Math.Min(y0 + 1, Size - 1);

        float tx = x - x0;
        float ty = y - y0;

        float h00 = GetHeight(x0, y0);
        float h10 = GetHeight(x1, y0);
        float h11 = GetHeight(x1, y1);
        float h01 = GetHeight(x0, y1);

        float h0 = Mathf.Lerp(h00, h10, tx);
        float h1 = Mathf.Lerp(h01, h11, tx);
        return Mathf.Lerp(h0, h1, ty);
    }

    /// <summary>
    /// Returns Layer1 texture index for a tile (matches MonoGame TerrainControl.GetHeroTile logic).
    /// </summary>
    public byte GetLayer1TextureIndexAt(int x, int y)
    {
        x = Math.Clamp(x, 0, Size - 1);
        y = Math.Clamp(y, 0, Size - 1);
        int index = y * Size + x;
        return GetMappingValue(_mapping.Layer1, index, 0);
    }

    /// <summary>
    /// Returns terrain flags from ATT for a tile.
    /// </summary>
    public TWFlags GetTerrainFlagsAt(int x, int y)
    {
        if (_attributes == null || _attributes.TerrainWall == null)
            return TWFlags.None;

        x = Math.Clamp(x, 0, Size - 1);
        y = Math.Clamp(y, 0, Size - 1);
        int index = y * Size + x;

        return index >= 0 && index < _attributes.TerrainWall.Length
            ? _attributes.TerrainWall[index]
            : TWFlags.None;
    }

    /// <summary>
    /// Returns terrain base texture index for a tile, following MU layer/alpha logic.
    /// </summary>
    public byte GetBaseTextureIndexAt(int x, int y)
    {
        x = Math.Clamp(x, 0, Size - 1);
        y = Math.Clamp(y, 0, Size - 1);
        int index = y * Size + x;

        byte layer1 = GetMappingValue(_mapping.Layer1, index, 0);
        byte layer2 = GetMappingValue(_mapping.Layer2, index, layer1);
        byte alpha = GetMappingValue(_mapping.Alpha, index, 0);
        return alpha == 255 ? layer2 : layer1;
    }

    /// <summary>
    /// Returns static terrain light color from final lightmap for a tile.
    /// </summary>
    public Color GetTerrainLightColor(int x, int y)
    {
        if (_finalLightMap == null)
            return Colors.White;

        x = Math.Clamp(x, 0, Size - 1);
        y = Math.Clamp(y, 0, Size - 1);
        int index = y * Size + x;

        return index >= 0 && index < _finalLightMap.Length
            ? _finalLightMap[index]
            : Colors.White;
    }

    /// <summary>
    /// Build the terrain as a set of MeshInstance3D nodes grouped by texture, added to the parent node.
    /// Each unique terrain texture becomes a separate surface for efficient rendering.
    /// </summary>
    public void BuildTerrain(Node3D parent)
    {
        if (_heightMap == null)
        {
            GD.PrintErr("Heightmap not loaded, cannot build terrain");
            return;
        }

        _waterMaterials.Clear();

        // Match MonoGame layering:
        // 1) Always draw an opaque/base layer.
        // 2) Draw alpha-blended Layer2 overlay where alpha map is non-zero.
        var opaqueTilesByTexture = new Dictionary<byte, List<(int x, int y)>>();
        var alphaTilesByTexture = new Dictionary<byte, List<(int x, int y)>>();

        for (int y = 0; y < Size - 1; y++)
        {
            for (int x = 0; x < Size - 1; x++)
            {
                int index = y * Size + x;
                int i1 = index;
                int i2 = y * Size + (x + 1);
                int i3 = (y + 1) * Size + (x + 1);
                int i4 = (y + 1) * Size + x;

                byte layer1 = GetMappingValue(_mapping.Layer1, i1, 0);
                byte layer2 = GetMappingValue(_mapping.Layer2, i1, layer1);

                byte a1 = GetMappingValue(_mapping.Alpha, i1, 0);
                byte a2 = GetMappingValue(_mapping.Alpha, i2, 0);
                byte a3 = GetMappingValue(_mapping.Alpha, i3, 0);
                byte a4 = GetMappingValue(_mapping.Alpha, i4, 0);

                bool isOpaque = (a1 & a2 & a3 & a4) == 255;
                bool hasAlpha = (a1 | a2 | a3 | a4) != 0;

                byte baseTexture = isOpaque ? layer2 : layer1;

                if (!opaqueTilesByTexture.TryGetValue(baseTexture, out var opaqueList))
                {
                    opaqueList = new List<(int, int)>();
                    opaqueTilesByTexture[baseTexture] = opaqueList;
                }
                opaqueList.Add((x, y));

                if (!isOpaque && hasAlpha)
                {
                    if (!alphaTilesByTexture.TryGetValue(layer2, out var alphaList))
                    {
                        alphaList = new List<(int, int)>();
                        alphaTilesByTexture[layer2] = alphaList;
                    }

                    alphaList.Add((x, y));
                }
            }
        }

        // Opaque pass first.
        foreach (var (texIndex, tiles) in opaqueTilesByTexture)
        {
            var mesh = BuildChunkMesh(tiles, texIndex, alphaLayer: false);
            if (mesh == null) continue;

            var meshInstance = new MeshInstance3D { Mesh = mesh, Name = $"TerrainOpaque_Tex{texIndex}" };
            meshInstance.MaterialOverride = CreateTerrainMaterial(texIndex, alphaLayer: false);
            parent.AddChild(meshInstance);
        }

        // Alpha pass second.
        foreach (var (texIndex, tiles) in alphaTilesByTexture)
        {
            var mesh = BuildChunkMesh(tiles, texIndex, alphaLayer: true);
            if (mesh == null) continue;

            var meshInstance = new MeshInstance3D { Mesh = mesh, Name = $"TerrainAlpha_Tex{texIndex}" };
            meshInstance.MaterialOverride = CreateTerrainMaterial(texIndex, alphaLayer: true);
            parent.AddChild(meshInstance);
        }

        GD.Print($"Built terrain with {opaqueTilesByTexture.Count} opaque groups and {alphaTilesByTexture.Count} alpha groups");
    }

    private Material CreateTerrainMaterial(byte texIndex, bool alphaLayer)
    {
        if (texIndex == WaterTextureIndex && _textures.TryGetValue(texIndex, out var waterTexture))
        {
            var waterMaterial = new ShaderMaterial
            {
                Shader = alphaLayer ? WaterAlphaShader : WaterOpaqueShader
            };

            waterMaterial.SetShaderParameter("albedo_texture", waterTexture);
            _waterMaterials.Add(waterMaterial);
            SyncWaterMaterialParameters(waterMaterial);
            return waterMaterial;
        }

        var material = new StandardMaterial3D();
        material.VertexColorUseAsAlbedo = true; // Use baked MU lightmap via vertex color.
        material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        material.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps;

        if (_textures.TryGetValue(texIndex, out var texture))
            material.AlbedoTexture = texture;

        if (alphaLayer)
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;

        return material;
    }

    public void Update(double delta)
    {
        _waterTotal += (float)delta * WaterSpeed;
        SyncWaterMaterialParameters();
    }

    private void SyncWaterMaterialParameters()
    {
        for (int i = 0; i < _waterMaterials.Count; i++)
        {
            var mat = _waterMaterials[i];
            if (mat == null)
                continue;

            SyncWaterMaterialParameters(mat);
        }
    }

    private void SyncWaterMaterialParameters(ShaderMaterial material)
    {
        material.SetShaderParameter("water_flow_direction", _waterFlowDirection);
        material.SetShaderParameter("water_total", _waterTotal);
        material.SetShaderParameter("distortion_amplitude", DistortionAmplitude);
        material.SetShaderParameter("distortion_frequency", Math.Max(0.01f, DistortionFrequency));
    }

    private static byte GetMappingValue(byte[]? map, int index, byte fallback)
    {
        if (map == null || index < 0 || index >= map.Length)
            return fallback;

        return map[index];
    }

    private byte GetBlendAlpha(int x, int y)
    {
        var alphaMap = _mapping.Alpha;
        if (alphaMap == null)
            return 0;

        x = Math.Clamp(x, 0, Size - 1);
        y = Math.Clamp(y, 0, Size - 1);
        int index = y * Size + x;
        if (index < 0 || index >= alphaMap.Length)
            return 0;

        return alphaMap[index];
    }

    private static Color WithAlpha(Color color, byte alpha)
    {
        return new Color(color.R, color.G, color.B, alpha / 255f);
    }

    private Vector2 GetTerrainUvStep(byte texIndex)
    {
        if (_textures.TryGetValue(texIndex, out var texture))
        {
            float w = Math.Max(texture.GetWidth(), 1);
            float h = Math.Max(texture.GetHeight(), 1);
            return new Vector2(64f / w, 64f / h);
        }

        return new Vector2(1f, 1f);
    }

    private ArrayMesh? BuildChunkMesh(List<(int x, int y)> tiles, byte texIndex, bool alphaLayer)
    {
        if (tiles.Count == 0)
            return null;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var uvStep = GetTerrainUvStep(texIndex);

        foreach (var (tx, ty) in tiles)
        {
            // 4 corners of the tile
            var v0 = GetTerrainVertex(tx, ty);
            var v1 = GetTerrainVertex(tx + 1, ty);
            var v2 = GetTerrainVertex(tx + 1, ty + 1);
            var v3 = GetTerrainVertex(tx, ty + 1);

            var n0 = GetTerrainNormal(tx, ty);
            var n1 = GetTerrainNormal(tx + 1, ty);
            var n2 = GetTerrainNormal(tx + 1, ty + 1);
            var n3 = GetTerrainNormal(tx, ty + 1);

            var c0 = GetLightColor(tx, ty);
            var c1 = GetLightColor(tx + 1, ty);
            var c2 = GetLightColor(tx + 1, ty + 1);
            var c3 = GetLightColor(tx, ty + 1);

            if (alphaLayer)
            {
                c0 = WithAlpha(c0, GetBlendAlpha(tx, ty));
                c1 = WithAlpha(c1, GetBlendAlpha(tx + 1, ty));
                c2 = WithAlpha(c2, GetBlendAlpha(tx + 1, ty + 1));
                c3 = WithAlpha(c3, GetBlendAlpha(tx, ty + 1));
            }

            // UV coordinates in world space, matching MonoGame terrain mapping.
            float u = tx * uvStep.X;
            float v = ty * uvStep.Y;
            var uv0 = new Vector2(u, v);
            var uv1 = new Vector2(u + uvStep.X, v);
            var uv2 = new Vector2(u + uvStep.X, v + uvStep.Y);
            var uv3 = new Vector2(u, v + uvStep.Y);

            // Triangle 1: v0, v2, v1 (CCW from above)
            st.SetNormal(n0); st.SetColor(c0); st.SetUV(uv0); st.AddVertex(v0);
            st.SetNormal(n2); st.SetColor(c2); st.SetUV(uv2); st.AddVertex(v2);
            st.SetNormal(n1); st.SetColor(c1); st.SetUV(uv1); st.AddVertex(v1);

            // Triangle 2: v0, v3, v2 (CCW from above)
            st.SetNormal(n0); st.SetColor(c0); st.SetUV(uv0); st.AddVertex(v0);
            st.SetNormal(n3); st.SetColor(c3); st.SetUV(uv3); st.AddVertex(v3);
            st.SetNormal(n2); st.SetColor(c2); st.SetUV(uv2); st.AddVertex(v2);
        }

        st.Index();
        return st.Commit();
    }

    /// <summary>
    /// Convert MU grid position to Godot Vector3.
    /// MU: X=right, Y=forward, Z=up -> Godot: X=right, Y=up, Z=-forward
    /// </summary>
    private Vector3 GetTerrainVertex(int x, int y)
    {
        float height = GetHeight(x, y);
        return new Vector3(x, height, -y);
    }

    private Vector3 GetTerrainNormal(int x, int y)
    {
        float hL = GetHeight(Math.Max(0, x - 1), y);
        float hR = GetHeight(Math.Min(Size - 1, x + 1), y);
        float hD = GetHeight(x, Math.Max(0, y - 1));
        float hU = GetHeight(x, Math.Min(Size - 1, y + 1));

        var normal = new Vector3(hL - hR, 2f, hU - hD);
        return normal.Normalized();
    }

    private Color GetLightColor(int x, int y)
    {
        if (_finalLightMap == null)
            return new Color(1, 1, 1, 1);

        x = Math.Clamp(x, 0, Size - 1);
        y = Math.Clamp(y, 0, Size - 1);
        int index = y * Size + x;
        return index >= 0 && index < _finalLightMap.Length
            ? _finalLightMap[index]
            : new Color(1, 1, 1, 1);
    }

    private void RebuildFinalLightMap()
    {
        if (_heightMap == null || _lightMap == null)
        {
            _finalLightMap = null;
            return;
        }

        int total = Size * Size;
        if (_lightMap.Length < total || _heightMap.Length < total)
        {
            _finalLightMap = null;
            return;
        }

        if (_finalLightMap == null || _finalLightMap.Length != total)
            _finalLightMap = new Color[total];

        var normals = BuildTerrainNormals();
        float ambient = Math.Max(0f, AmbientLight) * 255f;
        var lightDir = _lightDirection.LengthSquared() < 0.0001f ? DefaultLightDirection : _lightDirection.Normalized();

        for (int i = 0; i < total; i++)
        {
            float lum = Mathf.Clamp(normals[i].Dot(lightDir) + 0.5f, 0f, 1f);
            var c = _lightMap[i];

            float r = Mathf.Min((c.R * lum) + ambient, 255f) / 255f;
            float g = Mathf.Min((c.G * lum) + ambient, 255f) / 255f;
            float b = Mathf.Min((c.B * lum) + ambient, 255f) / 255f;
            _finalLightMap[i] = new Color(r, g, b, 1f);
        }
    }

    private Vector3[] BuildTerrainNormals()
    {
        var normals = new Vector3[Size * Size];

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int i = y * Size + x;

                var v1 = new Vector3((x + 1) * MuConfig.TerrainScale, y * MuConfig.TerrainScale, GetRawHeight(x + 1, y));
                var v2 = new Vector3((x + 1) * MuConfig.TerrainScale, (y + 1) * MuConfig.TerrainScale, GetRawHeight(x + 1, y + 1));
                var v3 = new Vector3(x * MuConfig.TerrainScale, (y + 1) * MuConfig.TerrainScale, GetRawHeight(x, y + 1));
                var v4 = new Vector3(x * MuConfig.TerrainScale, y * MuConfig.TerrainScale, GetRawHeight(x, y));

                var n1 = FaceNormalize(v1, v2, v3);
                var n2 = FaceNormalize(v3, v4, v1);
                var n = n1 + n2;
                normals[i] = n.LengthSquared() < 0.0001f ? new Vector3(0f, 0f, 1f) : n.Normalized();
            }
        }

        return normals;
    }

    private float GetRawHeight(int x, int y)
    {
        if (_heightMap == null)
            return 0f;

        int i = ((y & SizeMask) * Size) + (x & SizeMask);
        return _heightMap[i].R;
    }

    private static Vector3 FaceNormalize(Vector3 v1, Vector3 v2, Vector3 v3)
    {
        var n = (v2 - v1).Cross(v3 - v1);
        return n.LengthSquared() < 0.000001f ? Vector3.Zero : n.Normalized();
    }

    private static Dictionary<int, string> GetDefaultTextureMappings()
    {
        return new Dictionary<int, string>
        {
            { 0, "TileGrass01.ozj" },
            { 1, "TileGrass02.ozj" },
            { 2, "TileGround01.ozj" },
            { 3, "TileGround02.ozj" },
            { 4, "TileGround03.ozj" },
            { 5, "TileWater01.ozj" },
            { 6, "TileWood01.ozj" },
            { 7, "TileRock01.ozj" },
            { 8, "TileRock02.ozj" },
            { 9, "TileRock03.ozj" },
            { 10, "TileRock04.ozj" },
            { 11, "TileRock05.ozj" },
            { 12, "TileRock06.ozj" },
            { 13, "TileRock07.ozj" },
            { 30, "TileGrass01.ozt" },
            { 31, "TileGrass02.ozt" },
            { 32, "TileGrass03.ozt" },
        };
    }
}
