using Godot;
using MuGodot.Audio;
using MuGodot.Objects.Worlds.Lorencia;

namespace MuGodot;

/// <summary>
/// Main scene controller for the MU Online Godot map viewer.
/// Loads terrain, textures, and objects for a selected world.
/// Provides a free-fly camera for exploring the scene.
/// </summary>
[Tool]
public partial class Main : Node3D
{
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
	[ExportGroup("Fog & Culling")]
	[Export] public bool EnableDistanceFogAndObjectCulling { get; set; } = true;
	[Export] public float FogAndCullingDistance { get; set; } = 52f; // Object culling distance.
	[Export] public float FogStartDistance { get; set; } = 44f;       // Fog begins here.
	[Export] public float FogOpaqueBeforeCullMargin { get; set; } = 1.5f; // Fog reaches full strength before culling.
	[Export] public float FogDensityCurve { get; set; } = 3.2f;         // Higher = stronger fog near the end.
	[Export] public float FogMaxDensity { get; set; } = 0.88f;          // Depth fog max opacity.
	[Export] public float ObjectCullingHysteresis { get; set; } = 2f;   // Reduces edge popping while moving.
	[Export] public float ObjectCullingRefreshInterval { get; set; } = 0.10f;
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

	// Camera
	[Export] public float CameraMoveSpeed { get; set; } = 50f;
	[Export] public float CameraFastMultiplier { get; set; } = 3f;
	[Export] public float CameraMouseSensitivity { get; set; } = 0.003f;

	private Camera3D _camera = null!;
	private DirectionalLight3D _sun = null!;
	private Node3D _terrainRoot = null!;
	private Node3D _objectsRoot = null!;

	private float _cameraYaw;
	private float _cameraPitch = -0.5f;
	private bool _mouseCapture;

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
		_camera.Far = 1000f;
		_camera.Fov = 60f;

		// Position camera above terrain center looking down
		_camera.Position = new Vector3(128, 30, -128);

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
		if (_terrainRoot.GetParent() == null) AddChild(_terrainRoot);
		if (_objectsRoot.GetParent() == null) AddChild(_objectsRoot);

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
		helpLabel.Text = "WASD: Move | Shift: Fast | RMB: Look | Scroll: Speed | Esc: Release mouse";
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
			_distanceCulledAnimationControllers.Clear();
			_objectCullingTimer = 0f;
			_distanceCullingResetPending = true;
			ClearChildren(_terrainRoot);
			ClearChildren(_objectsRoot);
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

			ConfigureWorldAudio(WorldIndex);
			EnsureEditorSceneOwnership();

			if (editorMode)
				UpdateStatus($"Editor preview ready: World{WorldIndex}");
			else
				UpdateStatus($"World{WorldIndex} loaded! RMB to look around, WASD to move.");
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
			if (mouseButton.ButtonIndex == MouseButton.Right)
			{
				_mouseCapture = mouseButton.Pressed;
				Input.MouseMode = _mouseCapture ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
			}

			// Scroll to adjust speed
			if (mouseButton.ButtonIndex == MouseButton.WheelUp)
				CameraMoveSpeed = Mathf.Min(CameraMoveSpeed * 1.2f, 500f);
			else if (mouseButton.ButtonIndex == MouseButton.WheelDown)
				CameraMoveSpeed = Mathf.Max(CameraMoveSpeed / 1.2f, 5f);
		}

		if (@event is InputEventMouseMotion mouseMotion && _mouseCapture)
		{
			_cameraYaw -= mouseMotion.Relative.X * CameraMouseSensitivity;
			_cameraPitch -= mouseMotion.Relative.Y * CameraMouseSensitivity;
			_cameraPitch = Mathf.Clamp(_cameraPitch, Mathf.DegToRad(-89f), Mathf.DegToRad(89f));
		}

		if (@event is InputEventKey keyEvent && keyEvent.Keycode == Key.Escape)
		{
			_mouseCapture = false;
			Input.MouseMode = Input.MouseModeEnum.Visible;
		}
	}

	public override void _Process(double delta)
	{
		ApplyEditorPerformanceSettings();

		bool isEditor = Engine.IsEditorHint();
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

		// Update camera rotation
		_camera.Rotation = new Vector3(_cameraPitch, _cameraYaw, 0);

		// Camera movement
		float speed = CameraMoveSpeed * (float)delta;
		if (Input.IsKeyPressed(Key.Shift))
			speed *= CameraFastMultiplier;

		var forward = -_camera.GlobalTransform.Basis.Z;
		var right = _camera.GlobalTransform.Basis.X;
		var up = Vector3.Up;

		var velocity = Vector3.Zero;

		if (Input.IsKeyPressed(Key.W)) velocity += forward;
		if (Input.IsKeyPressed(Key.S)) velocity -= forward;
		if (Input.IsKeyPressed(Key.D)) velocity += right;
		if (Input.IsKeyPressed(Key.A)) velocity -= right;
		if (Input.IsKeyPressed(Key.E) || Input.IsKeyPressed(Key.Space)) velocity += up;
		if (Input.IsKeyPressed(Key.Q) || Input.IsKeyPressed(Key.Ctrl)) velocity -= up;

		if (velocity.LengthSquared() > 0)
			_camera.Position += velocity.Normalized() * speed;
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
		float showDistance = MathF.Max(1f, maxDistance - hysteresis);
		float showDistanceSq = showDistance * showDistance;

		for (int i = 0; i < _distanceCulledObjectInstances.Count; i++)
		{
			var mesh = _distanceCulledObjectInstances[i];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			float distSq = mesh.GlobalPosition.DistanceSquaredTo(cameraPosition);
			bool visible = mesh.Visible
				? distSq <= maxDistanceSq
				: distSq <= showDistanceSq;
			if (mesh.Visible != visible)
				mesh.Visible = visible;
		}

		for (int i = 0; i < _distanceCulledAnimationControllers.Count; i++)
		{
			var controller = _distanceCulledAnimationControllers[i];
			if (controller == null || !GodotObject.IsInstanceValid(controller))
				continue;

			bool animate = controller.HasAnyVisibleTargetWithinDistance(cameraPosition, maxDistanceSq);
			controller.SetExternalAnimationEnabled(animate);
		}
	}

	private void RebuildObjectDistanceCullingCaches()
	{
		_distanceCulledObjectInstances.Clear();
		_distanceCulledAnimationControllers.Clear();

		if (_objectsRoot == null || !GodotObject.IsInstanceValid(_objectsRoot))
			return;

		foreach (Node child in _objectsRoot.GetChildren())
		{
			if (child is MeshInstance3D mesh &&
				mesh.Name.ToString().StartsWith("Obj_", StringComparison.Ordinal))
			{
				_distanceCulledObjectInstances.Add(mesh);
				continue;
			}

			if (child is MuAnimatedMeshController controller)
				_distanceCulledAnimationControllers.Add(controller);
		}
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
