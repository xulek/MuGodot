using Client.Data.ATT;
using Godot;
using MuGodot.Audio;

namespace MuGodot.Objects.Worlds.Lorencia;

/// <summary>
/// Lorencia bird flock system inspired by Client.Main BirdManager/BirdObject.
/// Uses one shared animated mesh for all birds for good runtime performance.
/// </summary>
public sealed class LorenciaBirdSystem
{
    private const int TerrainSize = MuConfig.TerrainSize;

    private const int DefaultMaxBirds = 20;
    private const int MaxSpawnPerTick = 3;

    private const float NeighborRadiusMu = 200f;
    private const float SeparationDistanceMu = 180f;
    private const float CohesionStrength = 0.08f;
    private const float AlignmentStrength = 0.15f;
    private const float SeparationStrength = 3.0f;

    private const float SpawnDistanceMu = 2500f;
    private const float DespawnDistanceMu = 2000f;
    private const float SpawnCooldownSeconds = 0.6f;

    private const float MinFlightHeightMu = 350f;
    private const float MaxFlightHeightMu = 450f;
    private const float GroundOffsetMu = 65f;

    private const float LandCheckDistanceMu = 200f;
    private const float LandCheckMaxDistanceMu = 400f;

    private const float SteeringSpeed = 2.5f;
    private const float RotationSpeed = 4.0f;
    private const float VerticalLerpSpeed = 0.3f;

    private const float MinSpeedMultiplier = 1.0f;
    private const float MaxSpeedMultiplier = 1.3f;
    private const float DirectionChangeInterval = 1.0f;

    private const float AnimationSpeed = 12.0f;

    private readonly MuModelBuilder _modelBuilder;
    private readonly MuTerrainBuilder _terrainBuilder;
    private readonly Random _random = new();
    private readonly List<BirdAgent> _birds = new(DefaultMaxBirds);

    private Node3D? _root;
    private MuAnimatedMeshController? _animationController;
    private Mesh? _sharedMesh;
    private StandardMaterial3D[]? _materials;

    private AudioStreamPlayer3D? _chirpPlayer;
    private AudioStream? _chirp1;
    private AudioStream? _chirp2;

    private float _spawnCooldown;
    private float _chirpCooldown;
    private int _nextBirdId;

    public int MaxBirds { get; set; } = DefaultMaxBirds;
    public bool EnableSounds { get; set; } = true;

    private static float ToGodot(float muUnits) => muUnits * MuConfig.WorldToGodot;

    private static readonly float NeighborRadius = ToGodot(NeighborRadiusMu);
    private static readonly float SeparationDistance = ToGodot(SeparationDistanceMu);
    private static readonly float SpawnDistance = ToGodot(SpawnDistanceMu);
    private static readonly float DespawnDistance = ToGodot(DespawnDistanceMu);
    private static readonly float MinFlightHeight = ToGodot(MinFlightHeightMu);
    private static readonly float MaxFlightHeight = ToGodot(MaxFlightHeightMu);
    private static readonly float GroundOffset = ToGodot(GroundOffsetMu);
    private static readonly float LandCheckDistance = ToGodot(LandCheckDistanceMu);
    private static readonly float LandCheckMaxDistance = ToGodot(LandCheckMaxDistanceMu);

    public LorenciaBirdSystem(MuModelBuilder modelBuilder, MuTerrainBuilder terrainBuilder)
    {
        _modelBuilder = modelBuilder;
        _terrainBuilder = terrainBuilder;
    }

    public async Task<bool> InitializeAsync(Node3D parent)
    {
        Clear();

        _root = new Node3D { Name = "LorenciaBirds" };
        parent.AddChild(_root);
        if (Engine.IsEditorHint() && parent.Owner != null)
            _root.Owner = parent.Owner;

        var bmd = await _modelBuilder.LoadBmdAsync("Object1/Bird01.bmd");
        if (bmd == null)
        {
            GD.PrintErr("[Birds] Could not load Object1/Bird01.bmd");
            Clear();
            return false;
        }

        _materials = await _modelBuilder.LoadModelTexturesAsync("Object1/Bird01.bmd");
        SetupBirdMaterials(_materials);

        _animationController = new MuAnimatedMeshController { Name = "BirdAnimation" };
        _root.AddChild(_animationController);
        if (Engine.IsEditorHint() && _root.Owner != null)
            _animationController.Owner = _root.Owner;
        _animationController.Initialize(
            _modelBuilder,
            bmd,
            _materials,
            actionIndex: 0,
            animationSpeed: AnimationSpeed);

        _sharedMesh = _animationController.GetCurrentMesh();
        if (_sharedMesh == null)
        {
            GD.PrintErr("[Birds] Bird animation mesh is null");
            Clear();
            return false;
        }

        EnsureBirdSoundPlayer();

        _spawnCooldown = 0f;
        _chirpCooldown = 0f;
        _nextBirdId = 0;

        GD.Print("[Birds] Lorencia bird system initialized");
        return true;
    }

    public void Update(double delta, Vector3 heroPosition)
    {
        if (_root == null || _sharedMesh == null)
            return;

        float dt = (float)delta;
        _spawnCooldown -= dt;
        _chirpCooldown = Mathf.Max(0f, _chirpCooldown - dt);

        if (_birds.Count < MaxBirds && _spawnCooldown <= 0f)
        {
            SpawnBirds(heroPosition);
            _spawnCooldown = SpawnCooldownSeconds;
        }

        for (int i = _birds.Count - 1; i >= 0; i--)
        {
            var bird = _birds[i];

            if (bird.Hidden)
            {
                RemoveBirdAt(i);
                continue;
            }

            float distanceFromHero = HorizontalDistance(bird.Position, heroPosition);
            if (distanceFromHero > DespawnDistance)
            {
                RemoveBirdAt(i);
                continue;
            }

            UpdateBirdBehavior(bird, dt, heroPosition);

            if (bird.State == BirdAIState.Flying)
                ApplyBoidBehavior(bird, i, dt);

            CheckBoundaries(bird);
            ApplyMovement(bird, dt);
            ApplyVisualTransform(bird, dt);
        }
    }

    public void Clear()
    {
        for (int i = 0; i < _birds.Count; i++)
            _birds[i].Instance?.QueueFree();

        _birds.Clear();

        if (_root != null && GodotObject.IsInstanceValid(_root))
            _root.QueueFree();

        _root = null;
        _animationController = null;
        _sharedMesh = null;
        _materials = null;
        _chirpPlayer = null;
        _spawnCooldown = 0f;
        _chirpCooldown = 0f;
    }

    private void SetupBirdMaterials(StandardMaterial3D[] materials)
    {
        for (int i = 0; i < materials.Length; i++)
        {
            var mat = materials[i];
            if (mat == null)
                continue;

            mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            mat.AlphaScissorThreshold = 0.12f;
            mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            mat.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
            mat.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
        }
    }

    private void EnsureBirdSoundPlayer()
    {
        if (_root == null)
            return;

        _chirpPlayer = new AudioStreamPlayer3D
        {
            Name = "BirdChirpPlayer",
            MaxDistance = ToGodot(1500f),
            UnitSize = 1f,
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            VolumeDb = -8f
        };

        _root.AddChild(_chirpPlayer);
        if (Engine.IsEditorHint() && _root.Owner != null)
            _chirpPlayer.Owner = _root.Owner;

        _chirp1 = LoadAudioAsset("Sound/aBird1.wav");
        _chirp2 = LoadAudioAsset("Sound/aBird2.wav");
    }

    private void SpawnBirds(Vector3 heroPosition)
    {
        if (_root == null || _sharedMesh == null || _animationController == null)
            return;

        int spawned = 0;
        int maxAttempts = MaxSpawnPerTick * 5;

        for (int attempt = 0; attempt < maxAttempts && spawned < MaxSpawnPerTick; attempt++)
        {
            if (_birds.Count >= MaxBirds)
                break;

            float angle = (float)(_random.NextDouble() * Mathf.Tau);
            float distance = (float)(_random.NextDouble() * SpawnDistance * 0.5f) + (SpawnDistance * 0.5f);

            var spawn = new Vector3(
                heroPosition.X + MathF.Cos(angle) * distance,
                0f,
                heroPosition.Z + MathF.Sin(angle) * distance);

            int tileX = (int)MathF.Floor(spawn.X);
            int tileY = (int)MathF.Floor(-spawn.Z);

            if (tileX < 0 || tileX >= TerrainSize || tileY < 0 || tileY >= TerrainSize)
                continue;

            var flag = _terrainBuilder.GetTerrainFlagsAt(tileX, tileY);
            if (flag.HasFlag(TWFlags.NoMove) || flag.HasFlag(TWFlags.Height) || flag.HasFlag(TWFlags.SafeZone))
                continue;

            float terrainHeight = _terrainBuilder.GetHeightInterpolated(spawn.X, -spawn.Z);
            float spawnHeight = terrainHeight + ToGodot(200f + (float)_random.NextDouble() * 400f);
            spawn.Y = spawnHeight;

            float heading = (float)(_random.NextDouble() * Mathf.Tau);
            var direction = new Vector3(MathF.Cos(heading), 0f, MathF.Sin(heading)).Normalized();
            float baseSpeed = ToGodot(600f + (float)_random.NextDouble() * 50f);
            float scale = 0.75f + (float)_random.NextDouble() * 0.25f;

            var instance = new MeshInstance3D
            {
                Name = $"Bird_{_nextBirdId++}",
                Mesh = _sharedMesh,
                Position = spawn,
                Scale = new Vector3(scale, scale, scale)
            };

            if (_materials != null)
            {
                int surfaces = _sharedMesh.GetSurfaceCount();
                for (int i = 0; i < _materials.Length && i < surfaces; i++)
                    instance.SetSurfaceOverrideMaterial(i, _materials[i]);
            }

            _root.AddChild(instance);
            if (Engine.IsEditorHint() && _root.Owner != null)
                instance.Owner = _root.Owner;
            _animationController.RegisterInstance(instance);

            var bird = new BirdAgent
            {
                Instance = instance,
                Position = spawn,
                Scale = scale,
                DesiredDirection = direction,
                CurrentVelocity = direction * baseSpeed,
                BaseSpeed = baseSpeed,
                CurrentSpeed = baseSpeed,
                TargetSpeed = baseSpeed,
                TargetHeight = MinFlightHeight + (float)_random.NextDouble() * (MaxFlightHeight - MinFlightHeight),
                State = BirdAIState.Flying,
                StateTimer = 0f,
                DirectionChangeTimer = 0f,
                SubType = 0,
                Yaw = HeadingToYaw(direction),
                Hidden = false
            };

            _birds.Add(bird);
            spawned++;
        }
    }

    private void UpdateBirdBehavior(BirdAgent bird, float dt, Vector3 heroPosition)
    {
        bird.StateTimer += dt;
        bird.DirectionChangeTimer += dt;

        float distanceFromHero = HorizontalDistance(bird.Position, heroPosition);

        switch (bird.State)
        {
            case BirdAIState.Flying:
                UpdateFlyingState(bird, dt, distanceFromHero);
                break;

            case BirdAIState.Descending:
                bird.CurrentSpeed = bird.BaseSpeed;
                break;

            case BirdAIState.OnGround:
                bird.CurrentSpeed = 0f;
                if (distanceFromHero < ToGodot(150f) || _random.NextDouble() < dt * 0.1f)
                {
                    bird.State = BirdAIState.Ascending;
                    bird.StateTimer = 0f;
                    bird.CurrentSpeed = bird.BaseSpeed * 1.1f;
                    TryPlayBirdSound(bird.Position);
                }
                break;

            case BirdAIState.Ascending:
                bird.CurrentSpeed -= ToGodot(0.005f * 60f) * dt;
                if (_random.NextDouble() < dt * 10f)
                    bird.TargetHeight += ToGodot((float)_random.NextDouble() * 16f - 8f);

                if (bird.CurrentSpeed <= bird.BaseSpeed)
                {
                    bird.CurrentSpeed = bird.BaseSpeed;
                    bird.State = BirdAIState.Flying;
                    bird.StateTimer = 0f;
                }
                break;
        }
    }

    private void UpdateFlyingState(BirdAgent bird, float dt, float distanceFromHero)
    {
        if (_random.NextDouble() < dt * 0.05f)
            TryPlayBirdSound(bird.Position);

        if (_random.NextDouble() < dt * 0.5f)
        {
            float speedMultiplier = MinSpeedMultiplier + (float)_random.NextDouble() * (MaxSpeedMultiplier - MinSpeedMultiplier);
            bird.TargetSpeed = bird.BaseSpeed * speedMultiplier;
        }

        bird.CurrentSpeed = Mathf.Lerp(bird.CurrentSpeed, bird.TargetSpeed, Mathf.Clamp(dt * 0.8f, 0f, 1f));

        if (bird.DirectionChangeTimer >= DirectionChangeInterval)
        {
            bird.DirectionChangeTimer = 0f;

            float randomAngle = (float)(_random.NextDouble() * Mathf.Tau);
            var newDirection = new Vector3(MathF.Cos(randomAngle), 0f, MathF.Sin(randomAngle));
            bird.DesiredDirection = bird.DesiredDirection.Lerp(newDirection, 0.5f).Normalized();
            bird.TargetHeight = MinFlightHeight + (float)_random.NextDouble() * (MaxFlightHeight - MinFlightHeight);
        }

        if (_random.NextDouble() < 0.4f)
        {
            float nudgeAngle = (float)(_random.NextDouble() * Mathf.Tau);
            var nudge = new Vector3(MathF.Cos(nudgeAngle), 0f, MathF.Sin(nudgeAngle));
            bird.DesiredDirection = bird.DesiredDirection.Lerp(nudge, 0.2f).Normalized();
        }

        if (_random.NextDouble() < dt * 8f)
        {
            float erraticAngle = (float)(_random.NextDouble() * Mathf.Tau);
            var erratic = new Vector3(MathF.Cos(erraticAngle), 0f, MathF.Sin(erraticAngle));
            bird.DesiredDirection = bird.DesiredDirection.Lerp(erratic, 0.12f).Normalized();
        }

        if (bird.StateTimer > 5f &&
            distanceFromHero >= LandCheckDistance &&
            distanceFromHero <= LandCheckMaxDistance &&
            _random.NextDouble() < dt * 0.3f)
        {
            bird.State = BirdAIState.Descending;
            bird.StateTimer = 0f;
        }
    }

    private void ApplyBoidBehavior(BirdAgent bird, int birdIndex, float dt)
    {
        var separation = Vector3.Zero;
        var alignment = Vector3.Zero;
        var cohesion = Vector3.Zero;
        int neighbors = 0;

        for (int i = 0; i < _birds.Count; i++)
        {
            if (i == birdIndex)
                continue;

            var other = _birds[i];
            if (other.State != BirdAIState.Flying || other.Hidden)
                continue;

            float distance = bird.Position.DistanceTo(other.Position);
            if (distance < NeighborRadius && distance > 0.0001f)
            {
                neighbors++;

                if (distance < SeparationDistance)
                {
                    var away = (bird.Position - other.Position).Normalized();
                    separation += away / distance;
                }

                alignment += other.DesiredDirection;
                cohesion += other.Position;
            }
        }

        if (neighbors == 0)
            return;

        alignment /= neighbors;
        cohesion = ((cohesion / neighbors) - bird.Position).Normalized();

        var influence =
            (separation * SeparationStrength) +
            (alignment * AlignmentStrength) +
            (cohesion * CohesionStrength);

        bird.DesiredDirection = (bird.DesiredDirection + influence * dt).Normalized();
    }

    private void CheckBoundaries(BirdAgent bird)
    {
        int tileX = (int)MathF.Floor(bird.Position.X);
        int tileY = (int)MathF.Floor(-bird.Position.Z);

        if (tileX < 0 || tileX >= TerrainSize || tileY < 0 || tileY >= TerrainSize)
        {
            bird.Hidden = true;
            return;
        }

        var flag = _terrainBuilder.GetTerrainFlagsAt(tileX, tileY);
        bool hitObstacle = flag.HasFlag(TWFlags.NoMove) || flag.HasFlag(TWFlags.SafeZone);

        if (!hitObstacle)
        {
            if (bird.SubType > 0)
                bird.SubType--;
            return;
        }

        bird.Yaw = WrapAngle(bird.Yaw + Mathf.Pi);
        bird.DesiredDirection = YawToDirection(bird.Yaw);
        bird.SubType++;

        if (bird.SubType >= 3)
            bird.Hidden = true;
    }

    private void ApplyMovement(BirdAgent bird, float dt)
    {
        float terrainHeight = _terrainBuilder.GetHeightInterpolated(bird.Position.X, -bird.Position.Z);

        var targetVelocity = bird.DesiredDirection * bird.CurrentSpeed;
        bird.CurrentVelocity = bird.CurrentVelocity.Lerp(targetVelocity, Mathf.Clamp(dt * SteeringSpeed, 0f, 1f));

        bird.Position += bird.CurrentVelocity * dt;

        switch (bird.State)
        {
            case BirdAIState.Flying:
                if (_random.NextDouble() < dt * 5f)
                    bird.TargetHeight += ToGodot((float)_random.NextDouble() * 16f - 8f);

                bird.TargetHeight = Mathf.Clamp(bird.TargetHeight, MinFlightHeight, MaxFlightHeight);
                float desiredY = terrainHeight + bird.TargetHeight;
                bird.Position = new Vector3(
                    bird.Position.X,
                    Mathf.Lerp(bird.Position.Y, desiredY, Mathf.Clamp(dt * VerticalLerpSpeed, 0f, 1f)),
                    bird.Position.Z);
                break;

            case BirdAIState.Descending:
                float descendY = bird.Position.Y - ToGodot(5f * 60f) * dt;
                float groundY = terrainHeight + GroundOffset;

                if (descendY <= groundY)
                {
                    bird.Position = new Vector3(bird.Position.X, groundY, bird.Position.Z);
                    bird.State = BirdAIState.OnGround;
                    bird.StateTimer = 0f;
                    TryPlayBirdSound(bird.Position);
                }
                else
                {
                    bird.Position = new Vector3(bird.Position.X, descendY, bird.Position.Z);
                }
                break;

            case BirdAIState.OnGround:
                bird.Position = new Vector3(bird.Position.X, terrainHeight + GroundOffset, bird.Position.Z);
                break;

            case BirdAIState.Ascending:
                bird.Position += new Vector3(0f, ToGodot(5f * 60f) * dt, 0f);
                break;
        }
    }

    private void ApplyVisualTransform(BirdAgent bird, float dt)
    {
        if (bird.Hidden || bird.Instance == null || !GodotObject.IsInstanceValid(bird.Instance))
            return;

        if (bird.State != BirdAIState.OnGround && bird.CurrentVelocity.LengthSquared() > 0.001f)
        {
            float targetYaw = HeadingToYaw(bird.CurrentVelocity.Normalized());
            float diff = WrapAngle(targetYaw - bird.Yaw);
            float maxStep = RotationSpeed * dt;
            bird.Yaw += Mathf.Clamp(diff, -maxStep, maxStep);
            bird.Yaw = WrapAngle(bird.Yaw);
        }

        bird.Instance.Position = bird.Position;
        bird.Instance.Rotation = new Vector3(0f, bird.Yaw + Mathf.Pi * 0.5f, 0f);
        bird.Instance.Scale = new Vector3(bird.Scale, bird.Scale, bird.Scale);
    }

    private void RemoveBirdAt(int index)
    {
        var bird = _birds[index];
        if (bird.Instance != null && GodotObject.IsInstanceValid(bird.Instance))
            bird.Instance.QueueFree();

        _birds.RemoveAt(index);
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float HeadingToYaw(Vector3 direction)
    {
        return MathF.Atan2(direction.X, -direction.Z);
    }

    private static Vector3 YawToDirection(float yaw)
    {
        return new Vector3(MathF.Sin(yaw), 0f, -MathF.Cos(yaw));
    }

    private static float WrapAngle(float angle)
    {
        float wrapped = Mathf.PosMod(angle + Mathf.Pi, Mathf.Tau) - Mathf.Pi;
        return wrapped;
    }

    private void TryPlayBirdSound(Vector3 position)
    {
        if (!EnableSounds || _chirpPlayer == null || _chirpCooldown > 0f)
            return;

        AudioStream? stream = null;
        if (_chirp1 != null && _chirp2 != null)
            stream = _random.Next(0, 2) == 0 ? _chirp1 : _chirp2;
        else
            stream = _chirp1 ?? _chirp2;

        if (stream == null)
            return;

        _chirpPlayer.Stream = stream;
        _chirpPlayer.GlobalPosition = position;
        _chirpPlayer.Play();
        _chirpCooldown = 0.25f;
    }

    private static AudioStream? LoadAudioAsset(string relativePath)
    {
        string normalized = relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);

        string inData = System.IO.Path.Combine(MuConfig.DataPath, normalized);
        if (System.IO.File.Exists(inData))
            return MuAudioLoader.LoadFromFile(inData);

        var parent = System.IO.Directory.GetParent(MuConfig.DataPath);
        if (parent == null)
            return null;

        string fallback = System.IO.Path.Combine(parent.FullName, normalized);
        return System.IO.File.Exists(fallback)
            ? MuAudioLoader.LoadFromFile(fallback)
            : null;
    }

    private enum BirdAIState
    {
        Flying,
        Descending,
        OnGround,
        Ascending
    }

    private sealed class BirdAgent
    {
        public MeshInstance3D? Instance;
        public Vector3 Position;
        public Vector3 DesiredDirection;
        public Vector3 CurrentVelocity;
        public float BaseSpeed;
        public float CurrentSpeed;
        public float TargetSpeed;
        public float TargetHeight;
        public float StateTimer;
        public float DirectionChangeTimer;
        public float Yaw;
        public float Scale;
        public int SubType;
        public bool Hidden;
        public BirdAIState State;
    }
}
