using Godot;
using MuGodot.Audio;
using MuGodot.Objects.Worlds.Lorencia;
using Client.Data.ATT;
using Client.Data.BMD;
using Client.Data.CAP;

namespace MuGodot;

/// <summary>
/// Main scene controller for the MU Online Godot map viewer.
/// Loads terrain, textures, and objects for a selected world.
/// Spawns a controllable DarkWizard and uses MonoGame-style camera controls.
/// </summary>
[Tool]
public partial class Main : Node3D
{
	private readonly record struct ObjectCullBounds(MeshInstance3D Mesh, Vector3 LocalCenter, float LocalRadius);

	private const float MuToGodotScale = MuConfig.WorldToGodot;
	private const float MonoGameMoveSpeed = 300f * MuToGodotScale;
	private const float MonoGameCameraNear = 10f * MuToGodotScale;
	private const float MonoGameCameraFar = (1800f + 1800f) * MuToGodotScale;
	private const float MonoGameCameraMinDistance = 800f * MuToGodotScale;
	private const float MonoGameCameraMaxDistance = 1800f * MuToGodotScale;
	private const float MonoGameCameraDefaultDistance = 1700f * MuToGodotScale;
	private static readonly float MonoGameCameraDefaultYaw = Mathf.DegToRad(-41.99f);
	private static readonly float MonoGameCameraDefaultPitch = Mathf.DegToRad(135.87f);
	private static readonly float MonoGameCameraMinPitch = Mathf.DegToRad(110f);
	private static readonly float MonoGameCameraMaxPitch = Mathf.DegToRad(160f);
	private const int DarkWizardIdleAction = 1;
	private const int DarkWizardWalkAction = 15;
	private static readonly string[] DarkWizardBodyPartPrefixes = new[]
	{
		"HelmClass",
		"ArmorClass",
		"PantClass",
		"GloveClass",
		"BootClass"
	};

	private static readonly Vector3 MonoGameSunDirectionMu = new Vector3(-1f, 0f, -1f).Normalized();
	private static readonly Vector3 MonoGameTerrainLightDirectionMu = (-MonoGameSunDirectionMu).Normalized();
	private static readonly Color MonoGameSunColor = new Color(1f, 0.95f, 0.85f, 1f);
	private const float MonoGameSunStrength = 0.35f;
	private const float MonoGameTerrainAmbient = 0.25f;

	[Export] public int WorldIndex { get; set; } = 1; // Default: Lorencia
	[Export] public string DataPath { get; set; } = @"C:\Games\MU_Red_1_20_61_Full\Data";
	[ExportGroup("Editor Preview")]
	[Export] public bool BuildInEditor { get; set; } = true;
	[Export] public bool LoadObjectsInEditor { get; set; } = false;
	[Export] public bool AnimateObjectsInEditor { get; set; } = false;
	[Export] public bool ShowGeneratedNodesInSceneTree { get; set; } = false;
	[Export] public bool FullObjectOwnershipPassInEditor { get; set; } = false;
	[Export] public bool ContinuousEditorOwnershipSync { get; set; } = false;
	[Export] public float ContinuousEditorOwnershipSyncInterval { get; set; } = 0.5f;
	[ExportGroup("Editor Performance")]
	[Export] public bool LimitEffectsInEditor { get; set; } = true;
	[Export] public int EditorGrassMaxInstances { get; set; } = 12000;
	[Export] public float EditorGrassRefreshInterval { get; set; } = 0.35f;
	[Export] public float EditorFxUpdateHz { get; set; } = 24f;
	[Export] public int EditorLeafMaxParticles { get; set; } = 64;
	[Export] public float EditorLeafSpawnRateScale { get; set; } = 0.50f;
	[Export] public int EditorBirdMaxCount { get; set; } = 8;
	[Export] public bool EditorDisableBirdSounds { get; set; } = true;
	[Export] public bool EditorAnimateFire { get; set; } = false;
	[Export] public bool EditorFireLightsWhenAnimationOff { get; set; } = false;
	[Export] public float EditorFireAnimationFps { get; set; } = 18f;
	[Export] public float EditorFireActiveDistance { get; set; } = 65f;
	[ExportGroup("Grass")]
	[Export] public bool EnableGrass { get; set; } = true;
	[Export] public int GrassMaxInstances { get; set; } = 22000;
	[Export] public float GrassRefreshInterval { get; set; } = 0.20f;
	[Export] public float GrassWindSpeed { get; set; } = 1.0f;
	[Export] public float GrassWindStrength { get; set; } = 1.0f;
	[Export] public float GrassAlphaCutoff { get; set; } = 0.40f;
	[Export] public float GrassDensityScale { get; set; } = 1.5f;
	[Export] public float GrassRebuildMoveThreshold { get; set; } = 1.5f;
	[Export] public bool GrassProgressiveRebuild { get; set; } = true;
	[Export] public int GrassRebuildTilesPerFrame { get; set; } = 180;
	[ExportGroup("Fog & Culling")]
	[Export] public bool EnableDistanceFogAndObjectCulling { get; set; } = true;
	[Export] public float FogAndCullingDistance { get; set; } = 52f; // Object culling distance.
	[Export] public float FogStartDistance { get; set; } = 44f;       // Fog begins here.
	[Export] public float FogOpaqueBeforeCullMargin { get; set; } = 1.5f; // Fog reaches full strength before culling.
	[Export] public float FogDensityCurve { get; set; } = 3.2f;         // Higher = stronger fog near the end.
	[Export] public float FogMaxDensity { get; set; } = 0.88f;          // Depth fog max opacity.
	[Export] public float ObjectCullingHysteresis { get; set; } = 2f;   // Reduces edge popping while moving.
	[Export] public float ObjectCullingRefreshInterval { get; set; } = 0.10f;
	[Export] public bool UseFrustumObjectCulling { get; set; } = true;
	[Export] public float FrustumCullingMargin { get; set; } = 8f;
	[Export] public int ObjectCullingBatchSize { get; set; } = 512;
	[Export] public int AnimationCullingBatchSize { get; set; } = 128;
	[Export] public Color DistanceFogColor { get; set; } = new Color(0.58f, 0.64f, 0.72f, 1f);
	[ExportGroup("Audio")]
	[Export] public bool EnableWorldAudio { get; set; } = true;
	[Export] public bool PlayAudioInEditor { get; set; } = false;
	[Export] public float MusicVolumeDb { get; set; } = -7.0f;
	[Export] public float AmbientVolumeDb { get; set; } = -12.0f;
	[ExportGroup("Lorencia Birds")]
	[Export] public bool EnableLorenciaBirds { get; set; } = true;
	[Export] public bool ShowBirdsInEditor { get; set; } = false;
	[Export] public int LorenciaBirdMaxCount { get; set; } = 20;
	[Export] public bool LorenciaBirdSounds { get; set; } = true;
	[ExportGroup("Lorencia Leaves")]
	[Export] public bool EnableLorenciaLeaves { get; set; } = true;
	[Export] public bool ShowLeavesInEditor { get; set; } = false;
	[Export] public int LorenciaLeafMaxParticles { get; set; } = 140;
	[Export] public float LorenciaLeafSpawnRate { get; set; } = 25f;
	[Export] public float LorenciaLeafDensityScale { get; set; } = 1f;
	[Export] public string LorenciaLeafTexturePath { get; set; } = "World1/leaf01.OZT";

	[ExportGroup("MonoGame Camera")]
	[Export] public float CameraMouseSensitivity { get; set; } = 0.003f;
	[Export] public float CameraZoomSpeed { get; set; } = 4f;
	[Export] public float CameraMinDistance { get; set; } = MonoGameCameraMinDistance;
	[Export] public float CameraMaxDistance { get; set; } = MonoGameCameraMaxDistance;
	[Export] public float CameraDefaultDistance { get; set; } = MonoGameCameraDefaultDistance;
	[Export] public float CameraDefaultYaw { get; set; } = MonoGameCameraDefaultYaw;
	[Export] public float CameraDefaultPitch { get; set; } = MonoGameCameraDefaultPitch;
	[Export] public float CameraFollowSmoothness { get; set; } = 14f;

	[ExportGroup("DarkWizard")]
	[Export] public string DarkWizardModelPath { get; set; } = "Player/Player.bmd"; // Skeleton/animation source
	[Export] public int DarkWizardClassModelId { get; set; } = 1; // 1 = Dark Wizard
	[Export] public float DarkWizardMoveSpeed { get; set; } = MonoGameMoveSpeed;
	[Export] public float DarkWizardHeightOffset { get; set; } = 0f;
	[Export] public float DarkWizardFacingOffsetDegrees { get; set; } = 0f;
	[Export] public float DarkWizardTurnSmoothness { get; set; } = 16f;
	[Export] public float DarkWizardAnimationSpeed { get; set; } = 6.25f;
	[Export] public int DarkWizardSubFrameSamples { get; set; } = 8;
	[Export] public bool DarkWizardRealtimeInterpolation { get; set; } = true;

	private Camera3D _camera = null!;
	private DirectionalLight3D _sun = null!;
	private Node3D _terrainRoot = null!;
	private Node3D _objectsRoot = null!;
	private Node3D _charactersRoot = null!;

	private float _cameraYaw = MonoGameCameraDefaultYaw;
	private float _cameraPitch = MonoGameCameraDefaultPitch;
	private float _cameraDistance = MonoGameCameraDefaultDistance;
	private float _targetCameraDistance = MonoGameCameraDefaultDistance;
	private bool _cameraRotatePressed;
	private bool _cameraWasRotated;
	private Vector3 _cameraSmoothedTarget;
	private bool _cameraSmoothedTargetInitialized;

	private Node3D? _darkWizardRoot;
	private readonly List<MeshInstance3D> _darkWizardMeshes = new();
	private readonly List<MuAnimatedMeshController> _darkWizardIdleControllers = new();
	private readonly List<MuAnimatedMeshController> _darkWizardWalkControllers = new();
	private bool _darkWizardMoving;
	private Vector3 _darkWizardMoveTarget;
	private readonly Queue<Vector2I> _darkWizardPath = new();
	private float _darkWizardTargetYaw;
	private bool _darkWizardHasTargetYaw;
	private Vector2 _previousMousePosition;
	private Vector3 _cameraFallbackTarget = new Vector3(128f, 0f, -128f);

	private MuTerrainBuilder _terrainBuilder = null!;
	private MuModelBuilder _modelBuilder = null!;
	private MuObjectLoader _objectLoader = null!;
	private MuGrassRenderer? _grassRenderer;
	private LorenciaLeafParticleSystem? _lorenciaLeafSystem;
	private LorenciaBirdSystem? _lorenciaBirdSystem;
	private WorldEnvironment? _worldEnvironment;
	private Godot.Environment? _sceneEnvironment;
	private HashSet<string>? _sceneEnvironmentProperties;
	private readonly List<MeshInstance3D> _distanceCulledObjectInstances = new();
	private readonly List<ObjectCullBounds> _distanceCulledObjectBounds = new();
	private readonly List<MuAnimatedMeshController> _distanceCulledAnimationControllers = new();
	private AudioStreamPlayer? _musicPlayer;
	private AudioStreamPlayer? _ambientPlayer;
	private readonly Dictionary<string, AudioStream?> _audioCache = new(StringComparer.OrdinalIgnoreCase);
	private AudioStream? _lorenciaThemeMusic;
	private AudioStream? _lorenciaPubMusic;
	private AudioStream? _lorenciaAmbientWind;
	private bool _isInLorenciaPubArea;
	private Label? _statusLabel;
	private bool _loading;
	private float _editorOwnerSyncTimer;
	private float _editorFxUpdateAccumulator;
	private float _objectCullingTimer;
	private int _objectCullingCursor;
	private int _animationCullingCursor;
	private bool _distanceCullingResetPending = true;

	public override void _Ready()
	{
		MuConfig.DataPath = DataPath;
		bool isEditor = Engine.IsEditorHint();

		// Setup camera
		_camera = GetNodeOrNull<Camera3D>("Camera3D");
		if (_camera == null)
		{
			_camera = new Camera3D();
			AddChild(_camera);
		}
		_camera.Near = MonoGameCameraNear;
		_camera.Far = MonoGameCameraFar;
		_camera.Fov = 35f;
		ResetCameraToMonoGameDefaults();
		_camera.Position = _cameraFallbackTarget + new Vector3(8f, 11f, 9f);
		_previousMousePosition = GetViewport().GetMousePosition();

		// Setup directional light
		_sun = GetNodeOrNull<DirectionalLight3D>("DirectionalLight3D");
		if (_sun == null)
		{
			_sun = new DirectionalLight3D();
			AddChild(_sun);
		}
		ApplyMonoGameLightingDefaults();
		EnsureWorldEnvironment();
		ConfigureDistanceFog();

		// Setup container nodes
		_terrainRoot = GetNodeOrNull<Node3D>("Terrain") ?? new Node3D { Name = "Terrain" };
		_objectsRoot = GetNodeOrNull<Node3D>("Objects") ?? new Node3D { Name = "Objects" };
		_charactersRoot = GetNodeOrNull<Node3D>("Characters") ?? new Node3D { Name = "Characters" };
		if (_terrainRoot.GetParent() == null) AddChild(_terrainRoot);
		if (_objectsRoot.GetParent() == null) AddChild(_objectsRoot);
		if (_charactersRoot.GetParent() == null) AddChild(_charactersRoot);

		// Setup UI
		if (!isEditor)
			SetupUI();

		// Initialize loaders
		_terrainBuilder = new MuTerrainBuilder();
		_modelBuilder = new MuModelBuilder();
		_objectLoader = new MuObjectLoader(_modelBuilder, _terrainBuilder);
		_grassRenderer = new MuGrassRenderer(_terrainBuilder);
		_lorenciaLeafSystem = new LorenciaLeafParticleSystem(_terrainBuilder);
		_lorenciaBirdSystem = new LorenciaBirdSystem(_modelBuilder, _terrainBuilder);
		SetupAudioPlayers();
		ApplyEditorPerformanceSettings();
		EnsureEditorSceneOwnership();

		// Start loading
		if (isEditor)
		{
			if (BuildInEditor)
				_ = LoadWorldAsync(loadObjects: LoadObjectsInEditor, editorMode: true);
		}
		else
		{
			_ = LoadWorldAsync(loadObjects: true, editorMode: false);
		}
	}

	private void SetupUI()
	{
		var canvas = new CanvasLayer { Name = "UI" };
		AddChild(canvas);

		_statusLabel = new Label();
		_statusLabel.Position = new Vector2(10, 10);
		_statusLabel.AddThemeColorOverride("font_color", new Color(1, 1, 1));
		_statusLabel.AddThemeFontSizeOverride("font_size", 16);
		_statusLabel.Text = "Loading...";
		canvas.AddChild(_statusLabel);

		var helpLabel = new Label();
		helpLabel.Position = new Vector2(10, 680);
		helpLabel.AddThemeColorOverride("font_color", new Color(1, 1, 0.7f));
		helpLabel.AddThemeFontSizeOverride("font_size", 14);
		helpLabel.Text = "LMB: Move DarkWizard | MMB drag: Rotate camera | MMB click: Reset camera | Wheel: Zoom";
		canvas.AddChild(helpLabel);
	}

	private async Task LoadWorldAsync(bool loadObjects, bool editorMode)
	{
		if (_loading)
			return;

		if (string.IsNullOrWhiteSpace(DataPath) || !System.IO.Directory.Exists(DataPath))
		{
			UpdateStatus($"DataPath invalid: {DataPath}");
			return;
		}

		_loading = true;
		UpdateStatus($"Loading World{WorldIndex} terrain...");

		try
		{
			_grassRenderer?.Clear();
			_lorenciaLeafSystem?.Clear();
			_lorenciaBirdSystem?.Clear();
			_distanceCulledObjectInstances.Clear();
			_distanceCulledObjectBounds.Clear();
			_distanceCulledAnimationControllers.Clear();
			_objectCullingTimer = 0f;
			_objectCullingCursor = 0;
			_animationCullingCursor = 0;
			_distanceCullingResetPending = true;
			ClearChildren(_terrainRoot);
			ClearChildren(_objectsRoot);
			ClearChildren(_charactersRoot);
			_darkWizardRoot = null;
			_darkWizardMeshes.Clear();
			_darkWizardIdleControllers.Clear();
			_darkWizardWalkControllers.Clear();
			_darkWizardMoving = false;
			_darkWizardPath.Clear();
			_darkWizardHasTargetYaw = false;
			StopWorldAudio();

			// Load terrain data
			await _terrainBuilder.LoadAsync(WorldIndex);
			ApplyWorldTerrainSettings(WorldIndex);
			_terrainBuilder.BuildTerrain(_terrainRoot);

			if (EnableGrass && _grassRenderer != null)
			{
				int grassMaxInstances = Math.Max(1000, GrassMaxInstances);
				float grassRefreshInterval = Mathf.Clamp(GrassRefreshInterval, 0.05f, 1.0f);
				if (editorMode && LimitEffectsInEditor)
				{
					grassMaxInstances = Math.Min(grassMaxInstances, Math.Max(1000, EditorGrassMaxInstances));
					grassRefreshInterval = Mathf.Max(grassRefreshInterval, Mathf.Clamp(EditorGrassRefreshInterval, 0.10f, 2.0f));
				}

				_grassRenderer.MaxInstances = grassMaxInstances;
				_grassRenderer.RefreshIntervalSeconds = grassRefreshInterval;
				_grassRenderer.WindSpeed = Mathf.Max(0.1f, GrassWindSpeed);
				_grassRenderer.WindStrength = Mathf.Clamp(GrassWindStrength, 0.1f, 3.0f);
				_grassRenderer.AlphaCutoff = Mathf.Clamp(GrassAlphaCutoff, 0.05f, 0.95f);
				_grassRenderer.DensityScale = Mathf.Clamp(GrassDensityScale, 0.1f, 2.0f);
				_grassRenderer.RebuildCameraMoveThreshold = Mathf.Clamp(GrassRebuildMoveThreshold, 0.25f, 12.0f);
				_grassRenderer.ProgressiveRebuild = GrassProgressiveRebuild;
				_grassRenderer.RebuildTilesPerFrame = Math.Clamp(GrassRebuildTilesPerFrame, 16, 4000);
				await _grassRenderer.BuildAsync(WorldIndex, _terrainRoot, GetGrassCameraPosition());
			}
			ExposeGeneratedNodesInEditor(_terrainRoot);

			UpdateStatus(loadObjects ? "Terrain+grass loaded. Loading objects..." : "Terrain+grass loaded.");

			if (loadObjects)
			{
				// Load map objects
				bool enableObjectAnimations = !editorMode || AnimateObjectsInEditor;
				bool assignOwnership = editorMode && ShowGeneratedNodesInSceneTree;
				await _objectLoader.LoadObjectsAsync(
					WorldIndex,
					_objectsRoot,
					enableAnimations: enableObjectAnimations,
					assignEditorOwnership: assignOwnership);
				RebuildObjectDistanceCullingCaches();
				if (FullObjectOwnershipPassInEditor)
					ExposeGeneratedNodesInEditor(_objectsRoot);
			}
			else
			{
				RebuildObjectDistanceCullingCaches();
			}

			await ConfigureWorldAmbientSystemsAsync(WorldIndex);
			if (!editorMode)
				await SpawnDarkWizardAsync();

			ConfigureWorldAudio(WorldIndex);
			EnsureEditorSceneOwnership();

			if (editorMode)
				UpdateStatus($"Editor preview ready: World{WorldIndex}");
			else
				UpdateStatus($"World{WorldIndex} loaded! DarkWizard ready (LMB move, MMB rotate/reset, wheel zoom).");
		}
		catch (System.Exception ex)
		{
			GD.PrintErr($"Error loading world: {ex.Message}\n{ex.StackTrace}");
			UpdateStatus($"Error: {ex.Message}");
		}

		_loading = false;
	}

	private void ApplyWorldTerrainSettings(int worldIndex)
	{
		// Match MonoGame terrain lighting defaults.
		_terrainBuilder.SetStaticLighting(MonoGameTerrainLightDirectionMu, MonoGameTerrainAmbient);

		// Match MonoGame world tuning. World1 (Lorencia) uses animated water.
		_terrainBuilder.WaterFlowDirection = Vector2.Right;
		_terrainBuilder.WaterSpeed = 0f;
		_terrainBuilder.DistortionAmplitude = 0f;
		_terrainBuilder.DistortionFrequency = 0f;
		_terrainBuilder.WaterUvSpeed = 0.015f;
		_terrainBuilder.WaterCrossUvSpeed = 0.008f;
		_terrainBuilder.WaterCrossUvBlend = 0.42f;
		_terrainBuilder.WaterCrestStrength = 0.06f;
		_terrainBuilder.WaterSurfaceLift = 0f;
		_terrainBuilder.WaterEdgeExpand = 0.00f;
		_terrainBuilder.WaterTint = new Vector3(0.84f, 0.97f, 1.08f);
		_terrainBuilder.WaterFresnelColor = new Vector3(0.34f, 0.56f, 0.72f);
		_terrainBuilder.WaterFresnelStrength = 0.26f;
		_terrainBuilder.WaterSpecularColor = new Vector3(1f, 1f, 1f);
		_terrainBuilder.WaterSpecularStrength = 0.16f;
		_terrainBuilder.WaterSpecularPower = 64f;

		if (worldIndex == 1)
		{
			_terrainBuilder.WaterFlowDirection = new Vector2(1f, 0.18f).Normalized();
			_terrainBuilder.WaterSpeed = 0.26f;
			_terrainBuilder.DistortionAmplitude = 0.032f;
			_terrainBuilder.DistortionFrequency = 1.05f;
			_terrainBuilder.WaterUvSpeed = 0.068f;
			_terrainBuilder.WaterCrossUvSpeed = 0.038f;
			_terrainBuilder.WaterCrossUvBlend = 0.50f;
			_terrainBuilder.WaterCrestStrength = 0.045f;
			_terrainBuilder.WaterSurfaceLift = 0.03f;
			_terrainBuilder.WaterEdgeExpand = 0.0f;
			_terrainBuilder.WaterTint = new Vector3(0.23f, 0.34f, 0.48f);
			_terrainBuilder.WaterFresnelColor = new Vector3(0.10f, 0.18f, 0.28f);
			_terrainBuilder.WaterFresnelStrength = 0.07f;
			_terrainBuilder.WaterSpecularColor = new Vector3(0.68f, 0.77f, 0.86f);
			_terrainBuilder.WaterSpecularStrength = 0.03f;
			_terrainBuilder.WaterSpecularPower = 138f;

			// Match Lorencia grass setup from MonoGame.
			_grassRenderer?.ConfigureGrass(brightness: 2.0f, textureIndices: new byte[] { 0 });
		}
		else
		{
			_grassRenderer?.ConfigureGrass(brightness: 2.0f, textureIndices: new byte[] { 0 });
		}
	}

	private void ApplyMonoGameLightingDefaults()
	{
		if (_sun != null)
		{
			_sun.LightColor = MonoGameSunColor;
			// MonoGame SunStrength=0.35 is shader-space, so boost a bit for Godot light-space.
			_sun.LightEnergy = 0.95f;
			_sun.ShadowEnabled = true;

			var sunDirectionGodot = MuDirectionToGodot(MonoGameSunDirectionMu);
			if (sunDirectionGodot.LengthSquared() > 0.0001f)
			{
				var sunPos = _sun.GlobalPosition;
				// Match MonoGame sun direction in Godot space.
				_sun.LookAt(sunPos + sunDirectionGodot.Normalized(), Vector3.Up);
			}
		}
	}

	private static Vector3 MuDirectionToGodot(Vector3 muDirection)
	{
		// MU: X-right, Y-forward, Z-up -> Godot: X-right, Y-up, Z-back.
		return new Vector3(muDirection.X, muDirection.Z, -muDirection.Y);
	}

	private static void ClearChildren(Node node)
	{
		foreach (Node child in node.GetChildren())
			child.QueueFree();
	}

	private void ExposeGeneratedNodesInEditor(Node root)
	{
		if (!Engine.IsEditorHint() || !ShowGeneratedNodesInSceneTree)
			return;

		var editedSceneRoot = GetTree().EditedSceneRoot;
		if (editedSceneRoot == null)
			return;

		SetOwnerRecursive(root, editedSceneRoot);
	}

	private static void SetOwnerRecursive(Node node, Node owner)
	{
		if (node != owner)
			node.Owner = owner;

		foreach (Node child in node.GetChildren())
			SetOwnerRecursive(child, owner);
	}

	private void EnsureEditorSceneOwnership()
	{
		if (!Engine.IsEditorHint() || !ShowGeneratedNodesInSceneTree)
			return;

		var editedSceneRoot = GetTree().EditedSceneRoot;
		if (editedSceneRoot == null)
			return;

		// Keep continuous editor sync lightweight: only patch key roots.
		// Full recursive ownership pass is handled explicitly during world/object build.
		SetOwnerIfNeeded(_camera, editedSceneRoot);
		SetOwnerIfNeeded(_sun, editedSceneRoot);
		SetOwnerIfNeeded(_terrainRoot, editedSceneRoot);
		SetOwnerIfNeeded(_objectsRoot, editedSceneRoot);
		SetOwnerIfNeeded(_charactersRoot, editedSceneRoot);
		SetOwnerIfNeeded(_musicPlayer, editedSceneRoot);
		SetOwnerIfNeeded(_ambientPlayer, editedSceneRoot);
	}

	private static void SetOwnerIfNeeded(Node? node, Node owner)
	{
		if (node == null || !GodotObject.IsInstanceValid(node) || node == owner || node.Owner == owner)
			return;

		node.Owner = owner;
	}

	private void UpdateStatus(string text)
	{
		GD.Print(text);
		if (_statusLabel != null)
			_statusLabel.Text = text;
	}

	public override void _Input(InputEvent @event)
	{
		if (Engine.IsEditorHint())
			return;

		if (@event is InputEventMouseButton mouseButton)
		{
			if (mouseButton.ButtonIndex == MouseButton.Middle)
			{
				if (mouseButton.Pressed)
				{
					_cameraRotatePressed = true;
					_cameraWasRotated = false;
					_previousMousePosition = mouseButton.Position;
				}
				else if (_cameraRotatePressed)
				{
					if (!_cameraWasRotated)
						ResetCameraToMonoGameDefaults();

					_cameraRotatePressed = false;
					_cameraWasRotated = false;
				}
			}

			if (!mouseButton.Pressed)
				return;

			if (mouseButton.ButtonIndex == MouseButton.WheelUp)
			{
				_targetCameraDistance = Mathf.Clamp(
					_targetCameraDistance - (100f * MuToGodotScale),
					Mathf.Min(CameraMinDistance, CameraMaxDistance),
					Mathf.Max(CameraMinDistance, CameraMaxDistance));
			}
			else if (mouseButton.ButtonIndex == MouseButton.WheelDown)
			{
				_targetCameraDistance = Mathf.Clamp(
					_targetCameraDistance + (100f * MuToGodotScale),
					Mathf.Min(CameraMinDistance, CameraMaxDistance),
					Mathf.Max(CameraMinDistance, CameraMaxDistance));
			}
			else if (mouseButton.ButtonIndex == MouseButton.Left)
			{
				TrySetDarkWizardMoveTarget(mouseButton.Position);
			}
		}

		if (@event is InputEventMouseMotion mouseMotion)
		{
			if (_cameraRotatePressed)
			{
				var delta = mouseMotion.Position - _previousMousePosition;
				_previousMousePosition = mouseMotion.Position;

				if (delta.LengthSquared() > 0f)
				{
					_cameraYaw -= delta.X * CameraMouseSensitivity;
					_cameraPitch = Mathf.Clamp(
						_cameraPitch - delta.Y * CameraMouseSensitivity,
						MonoGameCameraMinPitch,
						MonoGameCameraMaxPitch);
					_cameraYaw = WrapAngle(_cameraYaw);
					_cameraWasRotated = true;
				}
			}
			else
			{
				_previousMousePosition = mouseMotion.Position;
			}
		}
	}

	public override void _Process(double delta)
	{
		ApplyEditorPerformanceSettings();

		bool isEditor = Engine.IsEditorHint();
		if (!isEditor)
		{
			UpdateDarkWizardMovement(delta);
			UpdateMonoGameCamera(delta);
		}

		Vector3 cameraPosition = GetGrassCameraPosition();
		UpdateDistanceFogAndObjectCulling(delta, cameraPosition);

		if (_terrainBuilder != null)
			_terrainBuilder.Update(delta);
		_grassRenderer?.Update(delta, cameraPosition);

		if (isEditor)
		{
			LorenciaFireEmitter.SetEditorCameraPosition(cameraPosition);

			if (LimitEffectsInEditor)
			{
				float step = 1f / Mathf.Clamp(EditorFxUpdateHz, 5f, 120f);
				_editorFxUpdateAccumulator += (float)delta;
				if (_editorFxUpdateAccumulator >= step)
				{
					float fxDelta = _editorFxUpdateAccumulator;
					_editorFxUpdateAccumulator = 0f;
					_lorenciaLeafSystem?.Update(fxDelta, cameraPosition, cameraPosition);
					_lorenciaBirdSystem?.Update(fxDelta, cameraPosition);
				}
			}
			else
			{
				_editorFxUpdateAccumulator = 0f;
				_lorenciaLeafSystem?.Update(delta, cameraPosition, cameraPosition);
				_lorenciaBirdSystem?.Update(delta, cameraPosition);
			}

			UpdateWorldAudio();

			_editorOwnerSyncTimer += (float)delta;
			float syncInterval = Mathf.Clamp(ContinuousEditorOwnershipSyncInterval, 0.1f, 5.0f);
			if (ContinuousEditorOwnershipSync && _editorOwnerSyncTimer >= syncInterval)
			{
				_editorOwnerSyncTimer = 0f;
				EnsureEditorSceneOwnership();
			}

			return;
		}

		_editorFxUpdateAccumulator = 0f;
		_lorenciaLeafSystem?.Update(delta, _camera.Position, _camera.Position);
		_lorenciaBirdSystem?.Update(delta, _camera.Position);
		UpdateWorldAudio();
	}

	private async Task SpawnDarkWizardAsync()
	{
		if (_charactersRoot == null || !GodotObject.IsInstanceValid(_charactersRoot))
			return;

		ClearChildren(_charactersRoot);
		_darkWizardRoot = null;
		_darkWizardMeshes.Clear();
		_darkWizardIdleControllers.Clear();
		_darkWizardWalkControllers.Clear();
		_darkWizardMoving = false;
		_darkWizardPath.Clear();
		_darkWizardHasTargetYaw = false;

		var spawn = await ResolveDarkWizardSpawnPositionAsync();
		_cameraFallbackTarget = spawn;

		var root = new Node3D { Name = "DarkWizard" };
		_charactersRoot.AddChild(root);
		_darkWizardRoot = root;

		BMD? skeletonBmd = await _modelBuilder.LoadBmdAsync(DarkWizardModelPath);
		if (skeletonBmd == null)
		{
			GD.PrintErr($"[DarkWizard] BMD not found: {DarkWizardModelPath}. Using fallback mesh.");
			var meshInstance = new MeshInstance3D { Name = "FallbackBody" };
			root.AddChild(meshInstance);
			_darkWizardMeshes.Add(meshInstance);
			ApplyDarkWizardFallbackMesh(meshInstance);
			_darkWizardRoot.Position = spawn;
			_darkWizardMoveTarget = spawn;
			_darkWizardPath.Clear();
			_darkWizardTargetYaw = _darkWizardRoot.Rotation.Y;
			_darkWizardHasTargetYaw = true;
			ResetCameraToMonoGameDefaults();
			UpdateMonoGameCamera(0d);
			UpdateStatus("DarkWizard model missing (using fallback).");
			return;
		}

		int idleAction = ClampActionIndex(skeletonBmd, DarkWizardIdleAction);
		int walkAction = ClampActionIndex(skeletonBmd, DarkWizardWalkAction);
		int classId = Math.Max(1, DarkWizardClassModelId);
		int subFrameSamples = Math.Clamp(DarkWizardSubFrameSamples, 1, 32);
		float animationSyncStartSeconds = Time.GetTicksMsec() * 0.001f;
		bool hasRenderablePart = false;

		for (int i = 0; i < DarkWizardBodyPartPrefixes.Length; i++)
		{
			string prefix = DarkWizardBodyPartPrefixes[i];
			string partModelPath = BuildDarkWizardPartModelPath(prefix, classId);
			BMD? partBmd = await _modelBuilder.LoadBmdAsync(partModelPath, logMissing: false);
			if (partBmd == null)
				continue;

			var materials = await _modelBuilder.LoadModelTexturesAsync(partModelPath);
			var meshInstance = new MeshInstance3D { Name = prefix };
			root.AddChild(meshInstance);
			_darkWizardMeshes.Add(meshInstance);

			var idleController = CreateDarkWizardAnimationController(
				root,
				partBmd,
				materials,
				idleAction,
				$"{prefix}_Idle",
				skeletonBmd,
				subFrameSamples,
				animationSyncStartSeconds);
			var idleMesh = idleController.GetCurrentMesh();
			if (idleMesh == null)
			{
				idleController.QueueFree();
				meshInstance.QueueFree();
				_darkWizardMeshes.Remove(meshInstance);
				continue;
			}

			idleController.RegisterInstance(meshInstance);
			meshInstance.Mesh = idleMesh;
			_darkWizardIdleControllers.Add(idleController);

			if (walkAction != idleAction)
			{
				var walkController = CreateDarkWizardAnimationController(
					root,
					partBmd,
					materials,
					walkAction,
					$"{prefix}_Walk",
					skeletonBmd,
					subFrameSamples,
					animationSyncStartSeconds);
				var walkMesh = walkController.GetCurrentMesh();
				if (walkMesh != null)
				{
					walkController.RegisterInstance(meshInstance);
					_darkWizardWalkControllers.Add(walkController);
				}
				else
				{
					walkController.QueueFree();
				}
			}

			hasRenderablePart = true;
		}

		if (!hasRenderablePart)
		{
			GD.PrintErr($"[DarkWizard] No class body parts found for class {classId}. Using fallback mesh.");
			var meshInstance = new MeshInstance3D { Name = "FallbackBody" };
			root.AddChild(meshInstance);
			_darkWizardMeshes.Add(meshInstance);
			ApplyDarkWizardFallbackMesh(meshInstance);
			_darkWizardRoot.Position = spawn;
			_darkWizardMoveTarget = spawn;
			_darkWizardPath.Clear();
			_darkWizardTargetYaw = _darkWizardRoot.Rotation.Y;
			_darkWizardHasTargetYaw = true;
			ResetCameraToMonoGameDefaults();
			UpdateMonoGameCamera(0d);
			UpdateStatus("DarkWizard class models missing (using fallback).");
			return;
		}

		_darkWizardRoot.Position = spawn;
		_darkWizardMoveTarget = spawn;
		_darkWizardPath.Clear();
		_darkWizardTargetYaw = _darkWizardRoot.Rotation.Y;
		_darkWizardHasTargetYaw = true;
		SetDarkWizardAnimationState(isMoving: false, force: true);
		ResetCameraToMonoGameDefaults();
		UpdateMonoGameCamera(0d);
		GD.Print($"[DarkWizard] Spawned at {spawn}. Parts: {_darkWizardMeshes.Count}, walk controllers: {_darkWizardWalkControllers.Count}");
	}

	private MuAnimatedMeshController CreateDarkWizardAnimationController(
		Node3D parent,
		BMD bmd,
		StandardMaterial3D[] materials,
		int actionIndex,
		string suffix,
		BMD? animationSourceBmd = null,
		int subFrameSamples = 8,
		float? syncStartTimeSeconds = null)
	{
		var controller = new MuAnimatedMeshController
		{
			Name = $"DarkWizard{suffix}"
		};

		parent.AddChild(controller);
		controller.Initialize(
			_modelBuilder,
			bmd,
			materials,
			actionIndex: actionIndex,
			animationSpeed: DarkWizardAnimationSpeed,
			subFrameSamples: subFrameSamples,
			animationSourceBmd: animationSourceBmd,
			syncStartTimeSeconds: syncStartTimeSeconds,
			useRealtimeInterpolation: DarkWizardRealtimeInterpolation);
		controller.SetExternalAnimationEnabled(false);
		return controller;
	}

	private static string BuildDarkWizardPartModelPath(string prefix, int classId)
	{
		string suffix = classId.ToString("D2");
		return $"Player/{prefix}{suffix}.bmd";
	}

	private static int ClampActionIndex(BMD bmd, int requestedAction)
	{
		if (bmd.Actions == null || bmd.Actions.Length == 0)
			return 0;

		return Math.Clamp(requestedAction, 0, bmd.Actions.Length - 1);
	}

	private static void ApplyDarkWizardFallbackMesh(MeshInstance3D meshInstance)
	{
		var capsule = new CapsuleMesh
		{
			Radius = 0.30f,
			Height = 0.95f
		};

		var material = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.20f, 0.23f, 0.42f),
			Metallic = 0.15f,
			Roughness = 0.72f
		};

		meshInstance.Mesh = capsule;
		meshInstance.MaterialOverride = material;
	}

	private async Task<Vector3> ResolveDarkWizardSpawnPositionAsync()
	{
		string capPath = System.IO.Path.Combine(DataPath, $"World{WorldIndex}", "Camera_Angle_Position.bmd");
		if (System.IO.File.Exists(capPath))
		{
			try
			{
				var capData = await new CAPReader().Load(capPath);
				float x = capData.HeroPosition.X * MuToGodotScale;
				float y = capData.HeroPosition.Y * MuToGodotScale;
				float sampleX = Mathf.Clamp(x, 0f, MuConfig.TerrainSize - 1f);
				float sampleY = Mathf.Clamp(y, 0f, MuConfig.TerrainSize - 1f);
				float terrainHeight = _terrainBuilder.GetHeightInterpolated(sampleX, sampleY) + DarkWizardHeightOffset;
				return new Vector3(sampleX, terrainHeight, -sampleY);
			}
			catch (System.Exception ex)
			{
				GD.PrintErr($"[DarkWizard] Failed to read CAP spawn: {ex.Message}");
			}
		}

		float centerX = MuConfig.TerrainSize * 0.5f;
		float centerY = MuConfig.TerrainSize * 0.5f;
		float h = _terrainBuilder.GetHeightInterpolated(centerX, centerY) + DarkWizardHeightOffset;
		return new Vector3(centerX, h, -centerY);
	}

	private void TrySetDarkWizardMoveTarget(Vector2 mousePosition)
	{
		if (_darkWizardRoot == null || !GodotObject.IsInstanceValid(_darkWizardRoot))
			return;

		if (!TryRaycastTerrain(mousePosition, out var hitPos))
			return;

		int tileX = Math.Clamp((int)MathF.Floor(hitPos.X), 0, MuConfig.TerrainSize - 1);
		int tileY = Math.Clamp((int)MathF.Floor(-hitPos.Z), 0, MuConfig.TerrainSize - 1);
		if (!IsTileWalkable(tileX, tileY))
			return;

		int startX = Math.Clamp((int)MathF.Floor(_darkWizardRoot.Position.X), 0, MuConfig.TerrainSize - 1);
		int startY = Math.Clamp((int)MathF.Floor(-_darkWizardRoot.Position.Z), 0, MuConfig.TerrainSize - 1);
		var startTile = new Vector2I(startX, startY);
		var targetTile = new Vector2I(tileX, tileY);

		var path = FindPath(startTile, targetTile);
		if (path.Count == 0)
			return;

		_darkWizardPath.Clear();
		for (int i = 0; i < path.Count; i++)
			_darkWizardPath.Enqueue(path[i]);

		// Skip zero-length/invalid first nodes so movement can't get stuck in walk state.
		if (!AdvanceDarkWizardPath(_darkWizardRoot.Position))
			SetDarkWizardAnimationState(isMoving: false, force: true);
	}

	private bool TryRaycastTerrain(Vector2 mousePosition, out Vector3 hitPosition)
	{
		hitPosition = Vector3.Zero;
		if (_camera == null || !GodotObject.IsInstanceValid(_camera))
			return false;

		Vector3 rayOrigin = _camera.ProjectRayOrigin(mousePosition);
		Vector3 rayDir = _camera.ProjectRayNormal(mousePosition);
		if (rayDir.LengthSquared() < 0.0001f)
			return false;
		rayDir = rayDir.Normalized();

		const float maxDistance = 512f;
		const float coarseStep = 1f;
		const float fineStep = 0.1f;

		float traveled = 0f;
		Vector3 lastPos = rayOrigin;
		if (!TryGetTerrainHeightAtWorld(lastPos.X, lastPos.Z, out float lastTerrainY))
			return false;
		float lastDiff = lastPos.Y - lastTerrainY;

		while (traveled < maxDistance)
		{
			traveled += coarseStep;
			Vector3 pos = rayOrigin + (rayDir * traveled);
			if (!TryGetTerrainHeightAtWorld(pos.X, pos.Z, out float terrainY))
				continue;

			float diff = pos.Y - terrainY;
			if (lastDiff > 0f && diff <= 0f)
			{
				float segmentStart = traveled - coarseStep;
				float refine = segmentStart;
				Vector3 refineLastPos = rayOrigin + (rayDir * segmentStart);
				if (!TryGetTerrainHeightAtWorld(refineLastPos.X, refineLastPos.Z, out float refineLastTerrainY))
					return false;
				float refineLastDiff = refineLastPos.Y - refineLastTerrainY;

				while (refine < traveled)
				{
					refine += fineStep;
					Vector3 refinePos = rayOrigin + (rayDir * refine);
					if (!TryGetTerrainHeightAtWorld(refinePos.X, refinePos.Z, out float refineTerrainY))
						continue;

					float refineDiff = refinePos.Y - refineTerrainY;
					if (refineLastDiff > 0f && refineDiff <= 0f)
					{
						float t = refineLastDiff / (refineLastDiff - refineDiff);
						hitPosition = refineLastPos.Lerp(refinePos, t);
						return true;
					}

					refineLastPos = refinePos;
					refineLastDiff = refineDiff;
				}
			}

			lastPos = pos;
			lastDiff = diff;
		}

		return false;
	}

	private void UpdateDarkWizardMovement(double delta)
	{
		if (_darkWizardRoot == null || !GodotObject.IsInstanceValid(_darkWizardRoot))
			return;

		var current = _darkWizardRoot.Position;
		const float moveEpsilon = 0.01f;
		float stepRemaining = MathF.Max(0.1f, DarkWizardMoveSpeed) * (float)delta;
		Vector2 lastMoveDir = Vector2.Zero;
		bool movedThisFrame = false;
		int guard = 0;

		// Consume full movement step even when crossing multiple path nodes in a single frame.
		while (stepRemaining > 0f && guard++ < 64)
		{
			Vector2 moveDir = new Vector2(
				_darkWizardMoveTarget.X - current.X,
				_darkWizardMoveTarget.Z - current.Z);
			float distance = moveDir.Length();

			if (!float.IsFinite(distance))
			{
				if (!AdvanceDarkWizardPath(current))
					break;
				continue;
			}

			if (distance <= moveEpsilon)
			{
				if (!AdvanceDarkWizardPath(current))
					break;
				continue;
			}

			Vector2 dir = moveDir / distance;
			float travel = MathF.Min(distance, stepRemaining);
			current.X += dir.X * travel;
			current.Z += dir.Y * travel;
			stepRemaining -= travel;
			lastMoveDir = dir;
			movedThisFrame = true;

			// Snap and continue with remaining step to avoid one-frame pauses on tile boundaries.
			if (travel + moveEpsilon >= distance)
			{
				current.X = _darkWizardMoveTarget.X;
				current.Z = _darkWizardMoveTarget.Z;
				if (!AdvanceDarkWizardPath(current))
					_darkWizardMoveTarget = current;
			}
			else
			{
				break;
			}
		}

		if (movedThisFrame && lastMoveDir.LengthSquared() > 0f)
			SetDarkWizardFacingTarget(lastMoveDir);

		if (TryGetTerrainHeightAtWorld(current.X, current.Z, out float height))
			current.Y = height + DarkWizardHeightOffset;

		_darkWizardRoot.Position = current;
		ApplyDarkWizardFacingInterpolation(delta);

		Vector2 remaining = new Vector2(
			_darkWizardMoveTarget.X - current.X,
			_darkWizardMoveTarget.Z - current.Z);
		float remainingDistance = remaining.Length();
		bool isMoving = float.IsFinite(remainingDistance) &&
			(remainingDistance > moveEpsilon || _darkWizardPath.Count > 0);
		SetDarkWizardAnimationState(isMoving);
	}

	private void SetMoveTargetFromTile(Vector2I tile)
	{
		int clampedTileX = Math.Clamp(tile.X, 0, MuConfig.TerrainSize - 1);
		int clampedTileY = Math.Clamp(tile.Y, 0, MuConfig.TerrainSize - 1);
		float targetX = Mathf.Clamp(clampedTileX + 0.5f, 0f, MuConfig.TerrainSize - 1.001f);
		float targetY = Mathf.Clamp(clampedTileY + 0.5f, 0f, MuConfig.TerrainSize - 1.001f);

		float terrainHeightBase;
		if (!TryGetTerrainHeightAtWorld(targetX, -targetY, out terrainHeightBase))
			terrainHeightBase = _darkWizardRoot != null && GodotObject.IsInstanceValid(_darkWizardRoot)
				? _darkWizardRoot.Position.Y - DarkWizardHeightOffset
				: 0f;

		float worldY = terrainHeightBase + DarkWizardHeightOffset;
		_darkWizardMoveTarget = new Vector3(targetX, worldY, -targetY);
	}

	private bool AdvanceDarkWizardPath(Vector3 currentPosition)
	{
		const float minDistanceSq = 0.0001f; // 0.01^2
		while (_darkWizardPath.Count > 0)
		{
			var nextTile = _darkWizardPath.Dequeue();
			SetMoveTargetFromTile(nextTile);
			if (!IsFiniteVector3(_darkWizardMoveTarget))
				continue;

			var delta = new Vector2(
				_darkWizardMoveTarget.X - currentPosition.X,
				_darkWizardMoveTarget.Z - currentPosition.Z);
			if (delta.LengthSquared() > minDistanceSq)
				return true;
		}

		_darkWizardMoveTarget = currentPosition;
		return false;
	}

	private void SetDarkWizardFacingTarget(Vector2 moveDir)
	{
		if (moveDir.LengthSquared() <= 0.000001f)
			return;

		_darkWizardTargetYaw = MathF.Atan2(moveDir.X, moveDir.Y) + Mathf.DegToRad(DarkWizardFacingOffsetDegrees);
		_darkWizardHasTargetYaw = true;
	}

	private void ApplyDarkWizardFacingInterpolation(double delta)
	{
		if (_darkWizardRoot == null || !GodotObject.IsInstanceValid(_darkWizardRoot) || !_darkWizardHasTargetYaw)
			return;

		float currentYaw = _darkWizardRoot.Rotation.Y;
		float targetYaw = _darkWizardTargetYaw;
		float smoothness = MathF.Max(0f, DarkWizardTurnSmoothness);
		float nextYaw;

		if (smoothness <= 0.001f)
		{
			nextYaw = targetYaw;
		}
		else
		{
			float t = 1f - MathF.Exp(-smoothness * (float)delta);
			nextYaw = Mathf.LerpAngle(currentYaw, targetYaw, Mathf.Clamp(t, 0f, 1f));
		}

		var rotation = _darkWizardRoot.Rotation;
		rotation.Y = WrapAngle(nextYaw);
		_darkWizardRoot.Rotation = rotation;
	}

	private static bool IsFiniteVector3(Vector3 v)
	{
		return float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
	}

	private List<Vector2I> FindPath(Vector2I start, Vector2I target)
	{
		if (start == target)
			return new List<Vector2I> { target };

		var frontier = new PriorityQueue<Vector2I, float>();
		var cameFrom = new Dictionary<Vector2I, Vector2I>();
		var gScore = new Dictionary<Vector2I, float> { [start] = 0f };
		frontier.Enqueue(start, 0f);

		Vector2I[] neighbors = new Vector2I[]
		{
			new Vector2I(1, 0),
			new Vector2I(-1, 0),
			new Vector2I(0, 1),
			new Vector2I(0, -1),
			new Vector2I(1, 1),
			new Vector2I(1, -1),
			new Vector2I(-1, 1),
			new Vector2I(-1, -1)
		};

		const int maxVisitedNodes = 4000;
		int visited = 0;

		while (frontier.Count > 0 && visited < maxVisitedNodes)
		{
			visited++;
			Vector2I current = frontier.Dequeue();
			if (current == target)
				break;

			float currentG = gScore[current];
			for (int i = 0; i < neighbors.Length; i++)
			{
				Vector2I n = current + neighbors[i];
				if (!IsTileInBounds(n) || !IsTileWalkable(n.X, n.Y))
					continue;

				bool diagonal = neighbors[i].X != 0 && neighbors[i].Y != 0;
				if (diagonal)
				{
					// Avoid cutting through blocked corners.
					var sideA = new Vector2I(current.X + neighbors[i].X, current.Y);
					var sideB = new Vector2I(current.X, current.Y + neighbors[i].Y);
					if (!IsTileInBounds(sideA) || !IsTileInBounds(sideB))
						continue;
					if (!IsTileWalkable(sideA.X, sideA.Y) || !IsTileWalkable(sideB.X, sideB.Y))
						continue;
				}

				float moveCost = diagonal ? 1.4142135f : 1f;
				float tentativeG = currentG + moveCost;
				if (gScore.TryGetValue(n, out float knownG) && tentativeG >= knownG)
					continue;

				gScore[n] = tentativeG;
				cameFrom[n] = current;
				float f = tentativeG + HeuristicCost(n, target);
				frontier.Enqueue(n, f);
			}
		}

		if (!cameFrom.ContainsKey(target))
			return new List<Vector2I>();

		var path = new List<Vector2I>();
		Vector2I node = target;
		path.Add(node);
		while (node != start && cameFrom.TryGetValue(node, out var parent))
		{
			node = parent;
			path.Add(node);
		}
		path.Reverse();

		// Skip start tile to avoid zero-length first move.
		if (path.Count > 0 && path[0] == start)
			path.RemoveAt(0);

		return path;
	}

	private static float HeuristicCost(Vector2I from, Vector2I to)
	{
		float dx = MathF.Abs(from.X - to.X);
		float dy = MathF.Abs(from.Y - to.Y);
		return MathF.Max(dx, dy);
	}

	private static bool IsTileInBounds(Vector2I tile)
	{
		return tile.X >= 0 &&
			   tile.Y >= 0 &&
			   tile.X < MuConfig.TerrainSize &&
			   tile.Y < MuConfig.TerrainSize;
	}

	private void SetDarkWizardAnimationState(bool isMoving, bool force = false)
	{
		if (!force && _darkWizardMoving == isMoving)
			return;

		_darkWizardMoving = isMoving;
		bool hasWalkControllers = _darkWizardWalkControllers.Count > 0;

		for (int i = 0; i < _darkWizardIdleControllers.Count; i++)
		{
			var controller = _darkWizardIdleControllers[i];
			if (controller == null || !GodotObject.IsInstanceValid(controller))
				continue;

			controller.SetExternalAnimationEnabled(!isMoving || !hasWalkControllers);
		}

		for (int i = 0; i < _darkWizardWalkControllers.Count; i++)
		{
			var controller = _darkWizardWalkControllers[i];
			if (controller == null || !GodotObject.IsInstanceValid(controller))
				continue;

			controller.SetExternalAnimationEnabled(isMoving);
		}
	}

	private bool IsTileWalkable(int x, int y)
	{
		var flags = _terrainBuilder.GetTerrainFlagsAt(x, y);
		return (flags & TWFlags.NoMove) == 0;
	}

	private bool TryGetTerrainHeightAtWorld(float worldX, float worldZ, out float terrainY)
	{
		float tileX = worldX;
		float tileY = -worldZ;
		if (tileX < 0f || tileY < 0f || tileX >= MuConfig.TerrainSize || tileY >= MuConfig.TerrainSize)
		{
			terrainY = 0f;
			return false;
		}

		terrainY = _terrainBuilder.GetHeightInterpolated(tileX, tileY);
		return true;
	}

	private void UpdateMonoGameCamera(double delta)
	{
		if (_camera == null || !GodotObject.IsInstanceValid(_camera))
			return;

		_targetCameraDistance = Mathf.Clamp(
			_targetCameraDistance,
			Mathf.Min(CameraMinDistance, CameraMaxDistance),
			Mathf.Max(CameraMinDistance, CameraMaxDistance));
		_cameraDistance = Mathf.Lerp(
			_cameraDistance,
			_targetCameraDistance,
			Mathf.Clamp(CameraZoomSpeed, 0.01f, 30f) * (float)delta);
		_cameraDistance = Mathf.Clamp(
			_cameraDistance,
			Mathf.Min(CameraMinDistance, CameraMaxDistance),
			Mathf.Max(CameraMinDistance, CameraMaxDistance));

		Vector3 targetRaw = GetCameraTargetPosition();
		if (!_cameraSmoothedTargetInitialized)
		{
			_cameraSmoothedTarget = targetRaw;
			_cameraSmoothedTargetInitialized = true;
		}

		float followSmoothness = MathF.Max(0f, CameraFollowSmoothness);
		if (followSmoothness <= 0.001f)
		{
			_cameraSmoothedTarget = targetRaw;
		}
		else
		{
			float t = 1f - MathF.Exp(-followSmoothness * (float)delta);
			_cameraSmoothedTarget = _cameraSmoothedTarget.Lerp(targetRaw, Mathf.Clamp(t, 0f, 1f));
		}

		Vector3 target = _cameraSmoothedTarget;
		Vector3 offset = ComputeMonoGameCameraOffset(_cameraDistance, _cameraYaw, _cameraPitch);
		_camera.GlobalPosition = target + offset;
		_camera.LookAt(target, Vector3.Up);
	}

	private Vector3 GetCameraTargetPosition()
	{
		if (_darkWizardRoot != null && GodotObject.IsInstanceValid(_darkWizardRoot))
			return _darkWizardRoot.GlobalPosition;

		return _cameraFallbackTarget;
	}

	private static Vector3 ComputeMonoGameCameraOffset(float distance, float yaw, float pitch)
	{
		float x = distance * MathF.Cos(pitch) * MathF.Sin(yaw);
		float y = distance * MathF.Cos(pitch) * MathF.Cos(yaw);
		float z = distance * MathF.Sin(pitch);
		return new Vector3(x, z, -y);
	}

	private void ResetCameraToMonoGameDefaults()
	{
		_cameraYaw = CameraDefaultYaw;
		_cameraPitch = Mathf.Clamp(CameraDefaultPitch, MonoGameCameraMinPitch, MonoGameCameraMaxPitch);
		_cameraDistance = CameraDefaultDistance;
		_targetCameraDistance = CameraDefaultDistance;
		_cameraSmoothedTargetInitialized = false;
	}

	private static float WrapAngle(float angle)
	{
		return Mathf.Wrap(angle, -Mathf.Pi, Mathf.Pi);
	}

	private Vector3 GetGrassCameraPosition()
	{
		if (Engine.IsEditorHint())
		{
			var editorCamera = GetViewport()?.GetCamera3D();
			if (editorCamera != null && GodotObject.IsInstanceValid(editorCamera))
				return editorCamera.GlobalPosition;
		}

		if (_camera != null && GodotObject.IsInstanceValid(_camera))
			return _camera.GlobalPosition;

		return Vector3.Zero;
	}

	private void EnsureWorldEnvironment()
	{
		if (_worldEnvironment != null && GodotObject.IsInstanceValid(_worldEnvironment) &&
			_sceneEnvironment != null && _sceneEnvironmentProperties != null)
		{
			return;
		}

		_worldEnvironment = GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
		if (_worldEnvironment == null)
		{
			_worldEnvironment = new WorldEnvironment { Name = "WorldEnvironment" };
			AddChild(_worldEnvironment);
		}

		_sceneEnvironment = _worldEnvironment.Environment;
		if (_sceneEnvironment == null)
		{
			_sceneEnvironment = new Godot.Environment();
			_worldEnvironment.Environment = _sceneEnvironment;
		}

		_sceneEnvironmentProperties = BuildPropertyNameSet(_sceneEnvironment);
	}

	private void ConfigureDistanceFog()
	{
		EnsureWorldEnvironment();
		if (_sceneEnvironment == null)
			return;

		bool enabled = EnableDistanceFogAndObjectCulling;
		_sceneEnvironment.FogEnabled = enabled;
		_sceneEnvironment.VolumetricFogEnabled = false;
		if (!enabled)
			return;

		float cullDistance = MathF.Max(10f, FogAndCullingDistance);
		float fogStart = Mathf.Clamp(FogStartDistance, 1f, cullDistance - 1f);
		float fogEnd = MathF.Max(
			fogStart + 1f,
			cullDistance - MathF.Max(0.5f, FogOpaqueBeforeCullMargin));

		_sceneEnvironment.FogMode = Godot.Environment.FogModeEnum.Depth;
		_sceneEnvironment.FogLightColor = DistanceFogColor;
		_sceneEnvironment.FogLightEnergy = 1.10f;
		_sceneEnvironment.FogSkyAffect = 1f;
		_sceneEnvironment.FogAerialPerspective = 0f;
		_sceneEnvironment.FogSunScatter = 0f;
		_sceneEnvironment.FogDensity = Mathf.Clamp(FogMaxDensity, 0f, 1f);
		_sceneEnvironment.FogHeightDensity = 0f;
		_sceneEnvironment.FogDepthBegin = fogStart;
		_sceneEnvironment.FogDepthEnd = fogEnd;
		_sceneEnvironment.FogDepthCurve = Mathf.Clamp(FogDensityCurve, 1f, 8f);
	}

	private void UpdateDistanceFogAndObjectCulling(double delta, Vector3 cameraPosition)
	{
		ConfigureDistanceFog();

		if (!EnableDistanceFogAndObjectCulling)
		{
			if (_distanceCullingResetPending)
				ResetObjectDistanceCulling();
			return;
		}

		_distanceCullingResetPending = true;
		_objectCullingTimer += (float)delta;
		float refresh = Mathf.Clamp(ObjectCullingRefreshInterval, 0.05f, 1.0f);
		if (_objectCullingTimer < refresh)
			return;

		_objectCullingTimer = 0f;
		float maxDistance = MathF.Max(10f, FogAndCullingDistance);
		float maxDistanceSq = maxDistance * maxDistance;
		float hysteresis = MathF.Max(0f, ObjectCullingHysteresis);
		bool useFrustum = UseFrustumObjectCulling && _camera != null && GodotObject.IsInstanceValid(_camera);
		float frustumShowMargin = MathF.Max(0f, FrustumCullingMargin);
		float frustumHideMargin = frustumShowMargin + hysteresis;
		Godot.Collections.Array<Plane>? frustumPlanes = useFrustum ? _camera!.GetFrustum() : null;
		float[]? frustumPlaneSigns = null;
		if (useFrustum && frustumPlanes != null && frustumPlanes.Count > 0)
		{
			Vector3 insidePoint = GetFrustumInsideSamplePoint(_camera!);
			frustumPlaneSigns = BuildFrustumPlaneSigns(frustumPlanes, insidePoint);
		}

		if (useFrustum && frustumPlanes != null && frustumPlanes.Count > 0 && frustumPlaneSigns != null)
		{
			ProcessFrustumObjectCullingBatch(
				frustumPlanes,
				frustumPlaneSigns,
				frustumShowMargin,
				frustumHideMargin);
		}
		else
		{
			float showDistance = MathF.Max(1f, maxDistance - hysteresis);
			float showDistanceSq = showDistance * showDistance;
			ProcessDistanceObjectCullingBatch(cameraPosition, maxDistanceSq, showDistanceSq);
		}

		ProcessAnimationCullingBatch(cameraPosition, maxDistanceSq, useFrustum);
	}

	private void ProcessFrustumObjectCullingBatch(
		Godot.Collections.Array<Plane> frustumPlanes,
		float[] frustumPlaneSigns,
		float showMargin,
		float hideMargin)
	{
		int total = _distanceCulledObjectBounds.Count;
		if (total <= 0)
			return;

		int batch = ResolveCullingBatchSize(total, ObjectCullingBatchSize);
		if (_objectCullingCursor >= total)
			_objectCullingCursor = 0;

		for (int n = 0; n < batch; n++)
		{
			int idx = (_objectCullingCursor + n) % total;
			var bounds = _distanceCulledObjectBounds[idx];
			var mesh = bounds.Mesh;
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			var transform = mesh.GlobalTransform;
			Vector3 worldCenter = transform * bounds.LocalCenter;
			float worldScale = GetTransformMaxScale(transform);
			float margin = mesh.Visible ? hideMargin : showMargin;
			float worldRadius = bounds.LocalRadius * worldScale + margin;
			bool visible = IsSphereInFrustum(frustumPlanes, frustumPlaneSigns, worldCenter, worldRadius);
			if (mesh.Visible != visible)
				mesh.Visible = visible;
		}

		_objectCullingCursor = (_objectCullingCursor + batch) % total;
	}

	private void ProcessDistanceObjectCullingBatch(Vector3 cameraPosition, float maxDistanceSq, float showDistanceSq)
	{
		int total = _distanceCulledObjectInstances.Count;
		if (total <= 0)
			return;

		int batch = ResolveCullingBatchSize(total, ObjectCullingBatchSize);
		if (_objectCullingCursor >= total)
			_objectCullingCursor = 0;

		for (int n = 0; n < batch; n++)
		{
			int idx = (_objectCullingCursor + n) % total;
			var mesh = _distanceCulledObjectInstances[idx];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			float distSq = mesh.GlobalPosition.DistanceSquaredTo(cameraPosition);
			bool visible = mesh.Visible
				? distSq <= maxDistanceSq
				: distSq <= showDistanceSq;
			if (mesh.Visible != visible)
				mesh.Visible = visible;
		}

		_objectCullingCursor = (_objectCullingCursor + batch) % total;
	}

	private void ProcessAnimationCullingBatch(Vector3 cameraPosition, float maxDistanceSq, bool useFrustum)
	{
		int total = _distanceCulledAnimationControllers.Count;
		if (total <= 0)
			return;

		int batch = ResolveCullingBatchSize(total, AnimationCullingBatchSize);
		if (_animationCullingCursor >= total)
			_animationCullingCursor = 0;

		for (int n = 0; n < batch; n++)
		{
			int idx = (_animationCullingCursor + n) % total;
			var controller = _distanceCulledAnimationControllers[idx];
			if (controller == null || !GodotObject.IsInstanceValid(controller))
				continue;

			bool animate = useFrustum
				? controller.HasAnyVisibleTarget()
				: controller.HasAnyVisibleTargetWithinDistance(cameraPosition, maxDistanceSq);
			controller.SetExternalAnimationEnabled(animate);
		}

		_animationCullingCursor = (_animationCullingCursor + batch) % total;
	}

	private static int ResolveCullingBatchSize(int totalCount, int requestedBatchSize)
	{
		if (totalCount <= 0)
			return 0;

		return Math.Clamp(requestedBatchSize, 1, totalCount);
	}

	private void RebuildObjectDistanceCullingCaches()
	{
		_distanceCulledObjectInstances.Clear();
		_distanceCulledObjectBounds.Clear();
		_distanceCulledAnimationControllers.Clear();
		_objectCullingCursor = 0;
		_animationCullingCursor = 0;

		if (_objectsRoot == null || !GodotObject.IsInstanceValid(_objectsRoot))
			return;

		foreach (Node child in _objectsRoot.GetChildren())
		{
			if (child is MeshInstance3D mesh &&
				mesh.Name.ToString().StartsWith("Obj_", StringComparison.Ordinal))
			{
				_distanceCulledObjectInstances.Add(mesh);
				_distanceCulledObjectBounds.Add(BuildObjectCullBounds(mesh));
				continue;
			}

			if (child is MuAnimatedMeshController controller)
				_distanceCulledAnimationControllers.Add(controller);
		}
	}

	private static ObjectCullBounds BuildObjectCullBounds(MeshInstance3D mesh)
	{
		if (mesh.Mesh == null)
			return new ObjectCullBounds(mesh, Vector3.Zero, 0.75f);

		var aabb = mesh.Mesh.GetAabb();
		Vector3 localCenter = aabb.Position + (aabb.Size * 0.5f);
		float localRadius = MathF.Max(0.5f, aabb.Size.Length() * 0.5f);
		return new ObjectCullBounds(mesh, localCenter, localRadius);
	}

	private static float GetTransformMaxScale(Transform3D transform)
	{
		float sx = transform.Basis.X.Length();
		float sy = transform.Basis.Y.Length();
		float sz = transform.Basis.Z.Length();
		return MathF.Max(0.0001f, MathF.Max(sx, MathF.Max(sy, sz)));
	}

	private static Vector3 GetFrustumInsideSamplePoint(Camera3D camera)
	{
		Vector3 forward = -camera.GlobalTransform.Basis.Z;
		if (forward.LengthSquared() <= 0.000001f)
			forward = Vector3.Forward;

		forward = forward.Normalized();
		float nearOffset = MathF.Max(0.05f, camera.Near) + 0.5f;
		return camera.GlobalPosition + (forward * nearOffset);
	}

	private static float[] BuildFrustumPlaneSigns(Godot.Collections.Array<Plane> frustumPlanes, Vector3 insidePoint)
	{
		int count = frustumPlanes.Count;
		var signs = new float[count];
		for (int i = 0; i < count; i++)
		{
			float d = frustumPlanes[i].DistanceTo(insidePoint);
			signs[i] = d >= 0f ? 1f : -1f;
		}

		return signs;
	}

	private static bool IsSphereInFrustum(
		Godot.Collections.Array<Plane> frustumPlanes,
		float[] planeSigns,
		Vector3 center,
		float radius)
	{
		for (int i = 0; i < frustumPlanes.Count; i++)
		{
			float signedDistance = frustumPlanes[i].DistanceTo(center) * planeSigns[i];
			if (signedDistance < -radius)
				return false;
		}

		return true;
	}

	private void ResetObjectDistanceCulling()
	{
		for (int i = 0; i < _distanceCulledObjectInstances.Count; i++)
		{
			var mesh = _distanceCulledObjectInstances[i];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			if (!mesh.Visible)
				mesh.Visible = true;
		}

		for (int i = 0; i < _distanceCulledAnimationControllers.Count; i++)
		{
			var controller = _distanceCulledAnimationControllers[i];
			if (controller == null || !GodotObject.IsInstanceValid(controller))
				continue;

			controller.SetExternalAnimationEnabled(true);
		}

		_objectCullingCursor = 0;
		_animationCullingCursor = 0;
		_distanceCullingResetPending = false;
	}

	private void SetEnvironmentPropertyIfExists(string propertyName, Variant value)
	{
		if (_sceneEnvironment == null || _sceneEnvironmentProperties == null)
			return;

		if (!_sceneEnvironmentProperties.Contains(propertyName))
			return;

		_sceneEnvironment.Set(propertyName, value);
	}

	private static HashSet<string> BuildPropertyNameSet(GodotObject obj)
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		var properties = obj.GetPropertyList();
		for (int i = 0; i < properties.Count; i++)
		{
			var property = properties[i];
			if (!property.ContainsKey("name"))
				continue;

			string name = property["name"].ToString();
			if (!string.IsNullOrWhiteSpace(name))
				names.Add(name);
		}

		return names;
	}

	public override void _ExitTree()
	{
		_lorenciaLeafSystem?.Clear();
		_lorenciaBirdSystem?.Clear();
		LorenciaFireEmitter.ResetSharedAssetsForReload();
		if (_charactersRoot != null && GodotObject.IsInstanceValid(_charactersRoot))
			ClearChildren(_charactersRoot);
		_darkWizardRoot = null;
		_darkWizardMeshes.Clear();
		_darkWizardIdleControllers.Clear();
		_darkWizardWalkControllers.Clear();
		_darkWizardPath.Clear();
		_darkWizardHasTargetYaw = false;
		StopWorldAudio();
		base._ExitTree();
	}

	private async Task ConfigureWorldAmbientSystemsAsync(int worldIndex)
	{
		if (_lorenciaLeafSystem == null || _lorenciaBirdSystem == null)
			return;

		_lorenciaLeafSystem.Clear();
		_lorenciaBirdSystem.Clear();

		bool isEditor = Engine.IsEditorHint();
		if (worldIndex != 1)
			return;

		bool leavesAllowed = !isEditor || ShowLeavesInEditor;
		if (EnableLorenciaLeaves && leavesAllowed)
		{
			int leafMaxParticles = Math.Clamp(LorenciaLeafMaxParticles, 1, 2000);
			float leafSpawnRate = MathF.Max(0f, LorenciaLeafSpawnRate);
			if (isEditor && LimitEffectsInEditor)
			{
				leafMaxParticles = Math.Min(leafMaxParticles, Math.Clamp(EditorLeafMaxParticles, 1, 2000));
				leafSpawnRate *= Mathf.Clamp(EditorLeafSpawnRateScale, 0.05f, 1f);
			}

			_lorenciaLeafSystem.MaxParticles = leafMaxParticles;
			_lorenciaLeafSystem.SpawnRate = leafSpawnRate;
			_lorenciaLeafSystem.DensityScale = Mathf.Clamp(LorenciaLeafDensityScale, 0.1f, 3f);
			_lorenciaLeafSystem.TexturePath = string.IsNullOrWhiteSpace(LorenciaLeafTexturePath)
				? "World1/leaf01.OZT"
				: LorenciaLeafTexturePath.Trim();
			await _lorenciaLeafSystem.InitializeAsync(_objectsRoot);
		}

		bool birdsAllowed = !isEditor || ShowBirdsInEditor;
		if (EnableLorenciaBirds && birdsAllowed)
		{
			int birdMaxCount = Math.Clamp(LorenciaBirdMaxCount, 0, 100);
			bool birdSounds = LorenciaBirdSounds;
			if (isEditor && LimitEffectsInEditor)
			{
				birdMaxCount = Math.Min(birdMaxCount, Math.Clamp(EditorBirdMaxCount, 0, 100));
				if (EditorDisableBirdSounds)
					birdSounds = false;
			}

			_lorenciaBirdSystem.MaxBirds = birdMaxCount;
			_lorenciaBirdSystem.EnableSounds = birdSounds;
			await _lorenciaBirdSystem.InitializeAsync(_objectsRoot);
		}

		if (FullObjectOwnershipPassInEditor)
			ExposeGeneratedNodesInEditor(_objectsRoot);
	}

	private void SetupAudioPlayers()
	{
		_musicPlayer = GetNodeOrNull<AudioStreamPlayer>("MusicPlayer");
		if (_musicPlayer == null)
		{
			_musicPlayer = new AudioStreamPlayer { Name = "MusicPlayer", Autoplay = false };
			AddChild(_musicPlayer);
		}

		_ambientPlayer = GetNodeOrNull<AudioStreamPlayer>("AmbientPlayer");
		if (_ambientPlayer == null)
		{
			_ambientPlayer = new AudioStreamPlayer { Name = "AmbientPlayer", Autoplay = false };
			AddChild(_ambientPlayer);
		}
	}

	private void ConfigureWorldAudio(int worldIndex)
	{
		_isInLorenciaPubArea = false;

		if (!EnableWorldAudio || !IsAudioAllowedForCurrentMode())
			return;

		if (worldIndex != 1)
			return;

		_lorenciaThemeMusic ??= LoadAudioAsset("Music/MuTheme.mp3");
		_lorenciaPubMusic ??= LoadAudioAsset("Music/Pub.mp3");
		_lorenciaAmbientWind ??= LoadAudioAsset("Sound/aWind.wav");

		PlayMusic(_lorenciaThemeMusic);
		PlayAmbient(_lorenciaAmbientWind);
	}

	private void UpdateWorldAudio()
	{
		if (!EnableWorldAudio || !IsAudioAllowedForCurrentMode())
			return;

		if (WorldIndex == 1)
			UpdateLorenciaPubMusicState();

		EnsurePlayerKeepsPlaying(_musicPlayer);
		EnsurePlayerKeepsPlaying(_ambientPlayer);
	}

	private void UpdateLorenciaPubMusicState()
	{
		if (_musicPlayer == null || _terrainBuilder == null || _camera == null)
			return;

		int tileX = Math.Clamp((int)MathF.Floor(_camera.Position.X), 0, MuConfig.TerrainSize - 1);
		int tileY = Math.Clamp((int)MathF.Floor(-_camera.Position.Z), 0, MuConfig.TerrainSize - 1);
		bool isInPubArea = _terrainBuilder.GetLayer1TextureIndexAt(tileX, tileY) == 4;

		if (isInPubArea == _isInLorenciaPubArea)
			return;

		_isInLorenciaPubArea = isInPubArea;
		PlayMusic(_isInLorenciaPubArea ? _lorenciaPubMusic : _lorenciaThemeMusic);
	}

	private AudioStream? LoadAudioAsset(string relativePath)
	{
		var resolvedPath = ResolveDataAssetPath(relativePath);
		if (resolvedPath == null)
		{
			GD.PrintErr($"[Audio] Asset not found: {relativePath}");
			return null;
		}

		if (_audioCache.TryGetValue(resolvedPath, out var cached))
			return cached;

		var stream = MuAudioLoader.LoadFromFile(resolvedPath);
		_audioCache[resolvedPath] = stream;
		return stream;
	}

	private string? ResolveDataAssetPath(string relativePath)
	{
		if (string.IsNullOrWhiteSpace(relativePath))
			return null;

		var normalized = relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);
		var candidate = System.IO.Path.Combine(DataPath, normalized);
		if (System.IO.File.Exists(candidate))
			return candidate;

		// Some client layouts store Music/Sound one level above Data/.
		var dataParent = System.IO.Directory.GetParent(DataPath);
		if (dataParent != null)
		{
			var fallback = System.IO.Path.Combine(dataParent.FullName, normalized);
			if (System.IO.File.Exists(fallback))
				return fallback;
		}

		return null;
	}

	private void PlayMusic(AudioStream? stream)
	{
		if (_musicPlayer == null || stream == null)
			return;

		_musicPlayer.VolumeDb = MusicVolumeDb;

		if (_musicPlayer.Stream == stream && _musicPlayer.Playing)
			return;

		_musicPlayer.Stop();
		_musicPlayer.Stream = stream;
		_musicPlayer.Play();
	}

	private void PlayAmbient(AudioStream? stream)
	{
		if (_ambientPlayer == null || stream == null)
			return;

		_ambientPlayer.VolumeDb = AmbientVolumeDb;

		if (_ambientPlayer.Stream == stream && _ambientPlayer.Playing)
			return;

		_ambientPlayer.Stop();
		_ambientPlayer.Stream = stream;
		_ambientPlayer.Play();
	}

	private static void EnsurePlayerKeepsPlaying(AudioStreamPlayer? player)
	{
		if (player == null || player.Stream == null || player.Playing)
			return;

		player.Play();
	}

	private void StopWorldAudio()
	{
		_musicPlayer?.Stop();
		_ambientPlayer?.Stop();
		_isInLorenciaPubArea = false;
	}

	private bool IsAudioAllowedForCurrentMode()
	{
		return !Engine.IsEditorHint() || PlayAudioInEditor;
	}

	private void ApplyEditorPerformanceSettings()
	{
		if (!Engine.IsEditorHint())
		{
			LorenciaFireEmitter.EditorAnimationEnabled = true;
			LorenciaFireEmitter.EditorKeepLightsWhenAnimationDisabled = true;
			LorenciaFireEmitter.EditorAnimationFps = 60f;
			LorenciaFireEmitter.EditorDistanceCulling = false;
			return;
		}

		bool limited = LimitEffectsInEditor;
		LorenciaFireEmitter.EditorAnimationEnabled = EditorAnimateFire;
		LorenciaFireEmitter.EditorKeepLightsWhenAnimationDisabled = EditorFireLightsWhenAnimationOff;
		LorenciaFireEmitter.EditorAnimationFps = limited
			? Mathf.Clamp(EditorFireAnimationFps, 5f, 60f)
			: 60f;
		LorenciaFireEmitter.EditorDistanceCulling = limited;
		LorenciaFireEmitter.EditorMaxActiveDistance = Mathf.Clamp(EditorFireActiveDistance, 10f, 250f);
	}
}
