using Godot;

namespace MuGodot.Objects.Worlds.Lorencia;

/// <summary>
/// Lightweight point light emitter for Lorencia objects that need illumination
/// without fire billboard sprites: Candle (150), StreetLight (90), DungeonGate (55).
///
/// Mirrors the DynamicLight usage in:
///   - CandleObject.cs    (Color 1,0.8,0.5 | Radius 250 MU | flameHeight 30 MU)
///   - StreetLightObject.cs (Color 1,0.9,0.7 | Radius 300 MU | height 200 MU)
///   - DungeonGateObject.cs (Color 1,0.7,0.4 | Radius 400 MU | two pillars)
/// </summary>
[Tool]
public sealed partial class LorenciaPointLightEmitter : Node3D
{
    private OmniLight3D? _light;
    private float _time;
    private float _timeOffset;
    private float _lightBaseEnergy;
    private bool _initialized;

    [Export]
    public short SourceType { get; set; }

    [Export]
    public int VariantIndex { get; set; }

    private readonly struct LightProfile
    {
        public readonly Color Color;
        public readonly float Range;
        public readonly float Energy;
        public readonly Vector3 LocalOffset;
        public readonly float FlickerAmplitude; // 0 = no flicker

        public LightProfile(Color color, float range, float energy, Vector3 localOffset, float flickerAmplitude = 0f)
        {
            Color = color;
            Range = range;
            Energy = energy;
            LocalOffset = localOffset;
            FlickerAmplitude = flickerAmplitude;
        }
    }

    /// <summary>
    /// Attach a LorenciaPointLightEmitter to a MeshInstance3D, mirroring the
    /// LorenciaFireEmitter.AttachTo pattern.
    /// </summary>
    public static void AttachTo(MeshInstance3D instance, short sourceType, int variantIndex = 0, string? nodeName = null)
    {
        if (instance == null || !GodotObject.IsInstanceValid(instance))
            return;

        string name = nodeName
            ?? (variantIndex == 0 ? "LorenciaPointLight" : $"LorenciaPointLight_{variantIndex}");

        if (instance.GetNodeOrNull<LorenciaPointLightEmitter>(name) != null)
            return;

        var emitter = new LorenciaPointLightEmitter
        {
            Name = name,
            SourceType = sourceType,
            VariantIndex = variantIndex
        };

        instance.AddChild(emitter);
        if (instance.Owner != null)
            emitter.Owner = instance.Owner;
    }

    public override void _Ready()
    {
        Initialize();
    }

    public override void _Process(double delta)
    {
        if (!_initialized || _light == null)
            return;

        var profile = GetProfile(SourceType, VariantIndex);
        if (profile.FlickerAmplitude <= 0f)
            return;

        _time += (float)delta;
        float t = _time + _timeOffset;

        // Same flicker formula as CandleObject.CalculateBaseLuminosity / DungeonGateObject
        float lum = 0.9f
            + MathF.Sin(t * 1.8f) * profile.FlickerAmplitude
            + MathF.Sin(t * 3.7f) * (profile.FlickerAmplitude * 0.53f);

        _light.LightEnergy = _lightBaseEnergy * MathF.Max(0.6f, lum);
    }

    private void Initialize()
    {
        if (_initialized)
            return;

        var profile = GetProfile(SourceType, VariantIndex);

        // Desynchronise flicker between instances using a deterministic hash of the instance ID.
        unchecked
        {
            uint h = (uint)GetInstanceId().GetHashCode();
            h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15;
            _timeOffset = (h & 0x00FFFFFFu) / 16777215f * 200f;
        }

        _light = new OmniLight3D
        {
            Name = "PointLight",
            LightColor = profile.Color,
            OmniRange = profile.Range,
            LightEnergy = profile.Energy,
            ShadowEnabled = false,
            Position = profile.LocalOffset
        };
        _lightBaseEnergy = profile.Energy;

        AddChild(_light);
        if (Engine.IsEditorHint() && Owner != null)
            _light.Owner = Owner;

        _initialized = true;
    }

    private static LightProfile GetProfile(short type, int variantIndex)
    {
        return type switch
        {
            // Candle01 — warm yellow, small radius, flickering flame.
            // MU reference: Color(1, 0.8, 0.5), Radius=250, flameHeight=30 MU.
            150 => new LightProfile(
                color: new Color(1f, 0.8f, 0.5f),
                range: 2.5f,                                   // 250 MU / 100
                energy: 1.2f,
                localOffset: new Vector3(0f, 0.3f, 0f),        // 30 MU / 100 (Z-up → Godot Y)
                flickerAmplitude: 0.15f),

            // StreetLight01 — warm white, wider reach, nearly static lamp.
            // MU reference: Color(1, 0.9, 0.7), Radius=300, height=200 MU.
            90 => new LightProfile(
                color: new Color(1f, 0.9f, 0.7f),
                range: 3.0f,                                   // 300 MU / 100
                energy: 1.5f,
                localOffset: new Vector3(0f, 2.0f, 0f),        // 200 MU / 100
                flickerAmplitude: 0f),

            // DungeonGate torches — warm fire, two pillars, flickering.
            // MU reference: Color(1, 0.7, 0.4), Radius=400.
            // Pillar offsets are approximated from BoneTransform[4]/[1] in the reference client.
            // Exact positions may need tuning once BMD bone data is inspected.
            55 => variantIndex == 0
                ? new LightProfile(
                    color: new Color(1f, 0.7f, 0.4f),
                    range: 4.0f,                               // 400 MU / 100
                    energy: 1.8f,
                    localOffset: new Vector3(0.65f, 0.8f, 0f), // right pillar (approx.)
                    flickerAmplitude: 0.15f)
                : new LightProfile(
                    color: new Color(1f, 0.7f, 0.4f),
                    range: 4.0f,
                    energy: 1.8f,
                    localOffset: new Vector3(-0.65f, 0.8f, 0f), // left pillar (approx.)
                    flickerAmplitude: 0.15f),

            _ => new LightProfile(
                color: new Color(1f, 0.8f, 0.5f),
                range: 2.5f,
                energy: 1.0f,
                localOffset: Vector3.Zero)
        };
    }
}
