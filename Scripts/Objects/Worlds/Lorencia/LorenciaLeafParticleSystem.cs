using Godot;

namespace MuGodot.Objects.Worlds.Lorencia;

/// <summary>
/// Ambient flying leaf particles for Lorencia.
/// Mirrors MonoGame LorenciaLeafAmbientEffect behavior with a fixed-size particle pool.
/// </summary>
public sealed class LorenciaLeafParticleSystem
{
    private static readonly Shader LeafShader = new()
    {
        Code = @"
shader_type spatial;
render_mode cull_disabled, blend_mix, depth_prepass_alpha, unshaded;

uniform sampler2D leaf_texture : source_color, filter_linear_mipmap, repeat_disable;
uniform float alpha_cutoff = 0.16;

void fragment()
{
    vec4 tex = texture(leaf_texture, UV);
    if (tex.a < alpha_cutoff)
        discard;

    ALBEDO = tex.rgb * COLOR.rgb;
    ALPHA = tex.a * COLOR.a;
}
"
    };

    private const float MU_TO_GODOT = MuConfig.WorldToGodot;

    private static float ToGodot(float mu) => mu * MU_TO_GODOT;

    // Runtime defaults from Client.Main/appsettings.json (LorenciaLeaf section),
    // mapped to Godot coordinates (MU +Y => Godot -Z).
    private static readonly Vector2 WindDirectionDefault = new Vector2(14f, -8f).Normalized();

    private static readonly float SpawnOffsetX = ToGodot(1500f);
    private static readonly float SpawnOffsetBack = ToGodot(1200f);
    private static readonly float SpawnOffsetForward = ToGodot(2000f);
    private static readonly float SpawnHeightMin = ToGodot(50f);
    private static readonly float SpawnHeightMax = ToGodot(320f);
    private static readonly float UpwindSpawnDistance = ToGodot(1100f);

    // appsettings has Min=450 / Max=250; MonoGame normalizes that to 250..450.
    private static readonly float MinHorizontalSpeed = ToGodot(250f);
    private static readonly float MaxHorizontalSpeed = ToGodot(450f);
    private static readonly float VerticalSpeedRange = ToGodot(100f);
    private static readonly float DriftStrength = ToGodot(3.5f);
    private static readonly float MaxDistance = ToGodot(3000f);
    private static readonly float BaseScale = ToGodot(7f);
    private static readonly float ScaleVariance = ToGodot(3f);

    private const float FadeInDuration = 0.8f;
    private const float FadeOutDuration = 2.0f;
    private const float MinLifetime = 10f;
    private const float MaxLifetime = 20f;
    private const float TiltStrength = 0.45f;
    private const float SwayStrength = 18f * MU_TO_GODOT;
    private const float WindVariance = 0.35f;
    private const float WindAlignment = 0.55f;
    private const float InitialFillRatio = 0.7f;

    private readonly MuTerrainBuilder _terrainBuilder;
    private readonly Random _random = new();

    private Node3D? _root;
    private MultiMeshInstance3D? _instanceNode;
    private MultiMesh? _multiMesh;

    private LeafParticle[] _particles = Array.Empty<LeafParticle>();
    private int _activeCount;
    private float _time;
    private float _spawnAccumulator;
    private bool _needsInitialFill;

    private float _maxDistanceSq;

    public int MaxParticles { get; set; } = 140;
    public float SpawnRate { get; set; } = 25f;
    public float DensityScale { get; set; } = 1f;
    public float WindSpeedMultiplier { get; set; } = 1.1f;
    public float AlphaCutoff { get; set; } = 0.16f;
    public string TexturePath { get; set; } = "World1/leaf01.OZT";

    public LorenciaLeafParticleSystem(MuTerrainBuilder terrainBuilder)
    {
        _terrainBuilder = terrainBuilder;
        _maxDistanceSq = MaxDistance * MaxDistance;
    }

    public async Task<bool> InitializeAsync(Node3D parent)
    {
        Clear();

        string? texturePath = ResolveDataAssetPath(TexturePath);
        if (texturePath == null)
        {
            GD.PrintErr($"[Leaves] Texture not found: {TexturePath}");
            return false;
        }

        var texture = await MuTextureHelper.LoadTextureAsync(texturePath, generateMipmaps: true);
        if (texture == null)
        {
            GD.PrintErr($"[Leaves] Failed to load texture: {texturePath}");
            return false;
        }

        _root = new Node3D { Name = "LorenciaLeaves" };
        parent.AddChild(_root);
        if (Engine.IsEditorHint() && parent.Owner != null)
            _root.Owner = parent.Owner;

        var mesh = CreateQuadMesh();
        var material = new ShaderMaterial { Shader = LeafShader };
        material.SetShaderParameter("leaf_texture", texture);
        material.SetShaderParameter("alpha_cutoff", Mathf.Clamp(AlphaCutoff, 0.02f, 0.95f));
        mesh.SurfaceSetMaterial(0, material);

        int maxCount = Math.Clamp(MaxParticles, 1, 2000);

        _multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = maxCount,
            VisibleInstanceCount = 0,
            Mesh = mesh
        };

        _instanceNode = new MultiMeshInstance3D
        {
            Name = "LeafInstances",
            Multimesh = _multiMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };

        _root.AddChild(_instanceNode);
        if (Engine.IsEditorHint() && _root.Owner != null)
            _instanceNode.Owner = _root.Owner;

        _particles = new LeafParticle[maxCount];
        _activeCount = 0;
        _time = 0f;
        _spawnAccumulator = 0f;
        _needsInitialFill = true;

        _maxDistanceSq = MaxDistance * MaxDistance;

        GD.Print($"[Leaves] Initialized Lorencia leaves system ({maxCount} max particles)");
        return true;
    }

    public void Update(double delta, Vector3 heroPosition, Vector3 cameraPosition)
    {
        if (_multiMesh == null || _particles.Length == 0)
            return;

        float dt = (float)delta;
        if (dt <= 0f)
            return;

        _time += dt;

        if (_needsInitialFill)
            PrefillParticles(heroPosition);

        for (int i = _activeCount - 1; i >= 0; i--)
        {
            var particle = _particles[i];
            particle.Age += dt;

            if (particle.Age >= particle.Lifetime)
            {
                RemoveParticleAt(i);
                continue;
            }

            // Random drift.
            particle.Velocity += new Vector3(
                RandomRange(-1f, 1f) * (DriftStrength * 0.35f),
                RandomRange(-0.5f, 0.5f) * (DriftStrength * 0.6f),
                RandomRange(-1f, 1f) * (DriftStrength * 0.35f)) * dt;

            Vector2 currentHorizontal = new Vector2(particle.Velocity.X, particle.Velocity.Z);
            Vector2 preferredDirection = NormalizeSafe(particle.PreferredDirection, WindDirectionDefault);
            Vector2 targetHorizontal = preferredDirection * particle.BaseSpeed;
            float alignFactor = Mathf.Clamp(WindAlignment * dt, 0f, 1f);
            currentHorizontal = currentHorizontal.Lerp(targetHorizontal, alignFactor);

            float currentSpeed = currentHorizontal.Length();
            float maxAllowed = MaxHorizontalSpeed * MathF.Max(0.05f, WindSpeedMultiplier);
            if (currentSpeed > maxAllowed && currentSpeed > 0.0001f)
                currentHorizontal *= maxAllowed / currentSpeed;

            particle.Velocity.X = currentHorizontal.X;
            particle.Velocity.Z = currentHorizontal.Y;

            if (WindVariance > 0f)
            {
                Vector2 noise = new Vector2(RandomRange(-1f, 1f), RandomRange(-1f, 1f)) * (WindVariance * 0.05f);
                preferredDirection = NormalizeSafe(preferredDirection + noise, preferredDirection);
            }

            float dirAlign = Mathf.Clamp(WindAlignment * dt * 0.4f, 0f, 1f);
            particle.PreferredDirection = NormalizeSafe(preferredDirection.Lerp(WindDirectionDefault, dirAlign), WindDirectionDefault);

            particle.Velocity.Y = Mathf.Clamp(particle.Velocity.Y, -VerticalSpeedRange, VerticalSpeedRange);
            particle.Position += particle.Velocity * dt;

            if (SwayStrength > 0f)
            {
                float sway = MathF.Sin((_time * particle.SwaySpeed) + particle.SwayPhase);
                float lift = MathF.Cos((_time * particle.SwaySpeed * 0.7f) + particle.SwayPhase * 0.35f);

                particle.Position += new Vector3(
                    sway * (SwayStrength * dt * 0.5f),
                    lift * (SwayStrength * dt * 0.15f),
                    sway * (SwayStrength * dt * 0.2f));
            }

            particle.RollAngle += particle.RollSpeed * dt;
            if (particle.RollAngle > Mathf.Tau)
                particle.RollAngle -= Mathf.Tau;
            else if (particle.RollAngle < 0f)
                particle.RollAngle += Mathf.Tau;

            float dx = particle.Position.X - heroPosition.X;
            float dz = particle.Position.Z - heroPosition.Z;
            if ((dx * dx) + (dz * dz) > _maxDistanceSq)
            {
                RemoveParticleAt(i);
                continue;
            }

            _particles[i] = particle;
        }

        float effectiveSpawnRate = MathF.Max(0f, SpawnRate) * MathF.Max(0.05f, DensityScale);
        if (effectiveSpawnRate > 0f)
        {
            _spawnAccumulator += effectiveSpawnRate * dt;
            int spawnCount = Math.Min((int)_spawnAccumulator, _particles.Length - _activeCount);

            if (spawnCount > 0)
            {
                _spawnAccumulator -= spawnCount;
                for (int i = 0; i < spawnCount; i++)
                    SpawnParticle(heroPosition);
            }
        }

        RebuildInstances(cameraPosition);
    }

    public void Clear()
    {
        if (_root != null && GodotObject.IsInstanceValid(_root))
            _root.QueueFree();

        _root = null;
        _instanceNode = null;
        _multiMesh = null;
        _particles = Array.Empty<LeafParticle>();
        _activeCount = 0;
        _spawnAccumulator = 0f;
        _time = 0f;
        _needsInitialFill = true;
    }

    private void SpawnParticle(Vector3 heroPosition)
    {
        if (_activeCount >= _particles.Length)
            return;

        Vector2 hero2D = new Vector2(heroPosition.X, heroPosition.Z);
        Vector2 spawn2D;

        bool spawnUpwind = UpwindSpawnDistance > 0f && _random.NextDouble() < 0.75;
        if (spawnUpwind)
        {
            Vector2 baseWind = NormalizeSafe(WindDirectionDefault, Vector2.Right);
            float distance = UpwindSpawnDistance + RandomRange(0f, SpawnOffsetForward * 0.5f);
            Vector2 perpendicular = new Vector2(-baseWind.Y, baseWind.X);

            spawn2D = hero2D - (baseWind * distance);
            spawn2D += perpendicular * RandomRange(-SpawnOffsetX, SpawnOffsetX);
            spawn2D += baseWind * RandomRange(-SpawnOffsetBack, SpawnOffsetForward) * 0.25f;
        }
        else
        {
            spawn2D = hero2D + new Vector2(
                RandomRange(-SpawnOffsetX, SpawnOffsetX),
                RandomRange(-SpawnOffsetBack, SpawnOffsetForward));
        }

        float terrainHeight = _terrainBuilder.GetHeightInterpolated(spawn2D.X, -spawn2D.Y);
        if (!float.IsFinite(terrainHeight))
            terrainHeight = heroPosition.Y;

        float spawnY = terrainHeight + RandomRange(SpawnHeightMin, SpawnHeightMax);
        var position = new Vector3(spawn2D.X, spawnY, spawn2D.Y);

        float speed = RandomRange(MinHorizontalSpeed, MaxHorizontalSpeed) * MathF.Max(0.05f, WindSpeedMultiplier);

        Vector2 direction = NormalizeSafe(WindDirectionDefault, Vector2.Right);
        if (WindVariance > 0f)
        {
            Vector2 jitter = new Vector2(RandomRange(-1f, 1f), RandomRange(-1f, 1f)) * WindVariance;
            direction = NormalizeSafe(direction + jitter, direction);

            Vector2 tangent = new Vector2(-direction.Y, direction.X);
            float tangentScale = WindVariance * 0.05f;
            direction = NormalizeSafe(direction + tangent * RandomRange(-tangentScale, tangentScale), direction);
        }

        var velocity = new Vector3(
            direction.X * speed,
            RandomRange(-VerticalSpeedRange, VerticalSpeedRange) * speed,
            direction.Y * speed);

        float scale = MathF.Max(ToGodot(4f), BaseScale + RandomRange(-ScaleVariance, ScaleVariance));

        _particles[_activeCount++] = new LeafParticle
        {
            Position = position,
            Velocity = velocity,
            Age = 0f,
            Lifetime = RandomRange(MinLifetime, MaxLifetime),
            Scale = scale,
            RollAngle = RandomRange(0f, Mathf.Tau),
            RollSpeed = RandomRange(-0.9f, 0.9f),
            TiltPhase = RandomRange(0f, Mathf.Tau),
            TiltSpeed = RandomRange(0.35f, 0.9f),
            SwayPhase = RandomRange(0f, Mathf.Tau),
            SwaySpeed = RandomRange(0.4f, 1.0f),
            FadeIn = FadeInDuration,
            FadeOut = FadeOutDuration,
            BaseAlpha = Mathf.Clamp(0.65f + RandomRange(-0.15f, 0.15f), 0.4f, 0.9f),
            BaseSpeed = speed,
            PreferredDirection = direction,
        };
    }

    private void RebuildInstances(Vector3 cameraPosition)
    {
        if (_multiMesh == null)
            return;

        int renderCount = 0;

        float swayStrength = TiltStrength * 0.6f;
        for (int i = 0; i < _activeCount && renderCount < _particles.Length; i++)
        {
            var particle = _particles[i];
            float alpha = ComputeAlpha(particle);
            if (alpha <= 0f)
                continue;

            Vector3 forward = cameraPosition - particle.Position;
            if (forward.LengthSquared() < 0.0001f)
                forward = new Vector3(0f, 0f, -1f);
            forward = forward.Normalized();

            Vector3 right = Vector3.Up.Cross(forward);
            if (right.LengthSquared() < 0.0001f)
                right = Vector3.Right.Cross(forward);
            right = right.Normalized();

            Vector3 up = forward.Cross(right).Normalized();

            right = right.Rotated(forward, particle.RollAngle);
            up = up.Rotated(forward, particle.RollAngle);

            if (TiltStrength > 0f)
            {
                float tilt = TiltStrength * MathF.Sin((_time * particle.TiltSpeed) + particle.TiltPhase);
                up = up.Rotated(right, tilt);
            }

            if (swayStrength > 0f)
            {
                float sway = swayStrength * MathF.Sin((_time * particle.TiltSpeed * 0.75f) + particle.TiltPhase * 1.3f);
                right = right.Rotated(up, sway);
            }

            forward = right.Cross(up).Normalized();
            right = up.Cross(forward).Normalized();

            float width = particle.Scale;
            float height = particle.Scale * 1.2f;

            var basis = new Basis(right * width, up * height, forward);
            var transform = new Transform3D(basis, particle.Position);

            _multiMesh.SetInstanceTransform(renderCount, transform);
            _multiMesh.SetInstanceColor(renderCount, new Color(1f, 1f, 1f, alpha));
            renderCount++;
        }

        _multiMesh.VisibleInstanceCount = renderCount;
    }

    private float ComputeAlpha(in LeafParticle particle)
    {
        float alpha = particle.BaseAlpha;

        if (particle.FadeIn > 0f && particle.Age < particle.FadeIn)
            alpha *= Mathf.Clamp(particle.Age / particle.FadeIn, 0f, 1f);

        float remaining = particle.Lifetime - particle.Age;
        if (particle.FadeOut > 0f && remaining < particle.FadeOut)
            alpha *= Mathf.Clamp(remaining / particle.FadeOut, 0f, 1f);

        return Mathf.Clamp(alpha, 0f, 1f);
    }

    private void RemoveParticleAt(int index)
    {
        int last = _activeCount - 1;
        if (index < 0 || index > last)
            return;

        if (index < last)
            _particles[index] = _particles[last];

        _activeCount--;
    }

    private void PrefillParticles(Vector3 heroPosition)
    {
        int desired = Math.Min(_particles.Length, Mathf.CeilToInt(_particles.Length * Mathf.Clamp(InitialFillRatio * DensityScale, 0f, 1f)));
        for (int i = _activeCount; i < desired; i++)
            SpawnParticle(heroPosition);

        _needsInitialFill = false;
    }

    private static Vector2 NormalizeSafe(Vector2 value, Vector2 fallback)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            return fallback;

        float lenSq = value.LengthSquared();
        if (lenSq < 0.0001f)
            return fallback;

        return value / MathF.Sqrt(lenSq);
    }

    private float RandomRange(float min, float max)
    {
        if (max <= min)
            return min;

        return (float)(_random.NextDouble() * (max - min) + min);
    }

    private static string? ResolveDataAssetPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var normalized = relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);
        string[] candidates = BuildTextureCandidates(normalized);

        for (int i = 0; i < candidates.Length; i++)
        {
            var candidate = System.IO.Path.Combine(MuConfig.DataPath, candidates[i]);
            if (System.IO.File.Exists(candidate))
                return candidate;
        }

        var dataParent = System.IO.Directory.GetParent(MuConfig.DataPath);
        if (dataParent == null)
            return null;

        for (int i = 0; i < candidates.Length; i++)
        {
            var candidate = System.IO.Path.Combine(dataParent.FullName, candidates[i]);
            if (System.IO.File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string[] BuildTextureCandidates(string normalizedPath)
    {
        var candidates = new List<string>(8) { normalizedPath };

        var dir = System.IO.Path.GetDirectoryName(normalizedPath) ?? string.Empty;
        var baseName = System.IO.Path.GetFileNameWithoutExtension(normalizedPath);
        if (string.IsNullOrWhiteSpace(baseName))
            return candidates.ToArray();

        string[] exts = [".OZT", ".ozt", ".OZJ", ".ozj", ".TGA", ".tga"];
        for (int i = 0; i < exts.Length; i++)
        {
            var withExt = System.IO.Path.Combine(dir, baseName + exts[i]);
            if (!candidates.Contains(withExt, StringComparer.OrdinalIgnoreCase))
                candidates.Add(withExt);
        }

        return candidates.ToArray();
    }

    private static ArrayMesh CreateQuadMesh()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        st.SetUV(new Vector2(0f, 1f)); st.AddVertex(new Vector3(-0.5f, -0.5f, 0f));
        st.SetUV(new Vector2(1f, 1f)); st.AddVertex(new Vector3(0.5f, -0.5f, 0f));
        st.SetUV(new Vector2(1f, 0f)); st.AddVertex(new Vector3(0.5f, 0.5f, 0f));

        st.SetUV(new Vector2(0f, 1f)); st.AddVertex(new Vector3(-0.5f, -0.5f, 0f));
        st.SetUV(new Vector2(1f, 0f)); st.AddVertex(new Vector3(0.5f, 0.5f, 0f));
        st.SetUV(new Vector2(0f, 0f)); st.AddVertex(new Vector3(-0.5f, 0.5f, 0f));

        st.Index();
        return st.Commit() ?? new ArrayMesh();
    }

    private struct LeafParticle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float Lifetime;
        public float Scale;
        public float RollAngle;
        public float RollSpeed;
        public float TiltPhase;
        public float TiltSpeed;
        public float SwayPhase;
        public float SwaySpeed;
        public float FadeIn;
        public float FadeOut;
        public float BaseAlpha;
        public float BaseSpeed;
        public Vector2 PreferredDirection;
    }
}
