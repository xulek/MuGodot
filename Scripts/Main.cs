using Godot;
using MuGodot.Audio;
using MuGodot.Objects.Worlds.Lorencia;

namespace MuGodot;

/// <summary>
/// Main scene controller for the MU Online Godot map viewer.
/// Loads terrain, textures, and objects for a selected world.
/// Spawns a controllable DarkWizard and uses MonoGame-style camera controls.
/// </summary>
[Tool]
public partial class Main : Node3D
{
	private const float MuToGodotScale = MuConfig.WorldToGodot;
	private const int UiNoneId = 2_000_000_001;
	private const int UiArmorClassDefaultId = 2_000_000_002;

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

	private DirectionalLight3D _sun = null!;
	private Node3D _terrainRoot = null!;
	private Node3D _objectsRoot = null!;
	private Node3D _charactersRoot = null!;

	private MuTerrainBuilder _terrainBuilder = null!;
	private MuModelBuilder _modelBuilder = null!;
	private MuObjectLoader _objectLoader = null!;
	private MuGrassRenderer? _grassRenderer;
	private LorenciaAmbientManager? _ambientManager;
	private LorenciaHouseOcclusionSystem? _houseOcclusionSystem;
	private LorenciaInteractionSystem _lorenciaInteractionSystem = new();
	private CameraController _cameraController = null!;
	private ObjectCullingSystem _cullingSystem = null!;
	private WorldAudioManager? _audioManager;
	private DarkWizardController _darkWizardController = null!;
	private CanvasLayer? _uiLayer;
	private Label? _statusLabel;
	private OptionButton? _armorOption;
	private OptionButton? _leftWeaponOption;
	private OptionButton? _rightWeaponOption;
	private OptionButton? _wingOption;
	private SpinBox? _itemLevelSpin;
	private CheckBox? _excellentCheck;
	private CheckBox? _ancientCheck;
	private bool _suppressEquipmentUiEvents;
	private bool _loading;
	private float _editorOwnerSyncTimer;
	private float _editorFxUpdateAccumulator;

	public override void _Ready()
	{
		MuConfig.DataPath = DataPath;
		bool isEditor = Engine.IsEditorHint();

		// Setup camera controller
		_cameraController = GetNodeOrNull<CameraController>("CameraController")
			?? new CameraController { Name = "CameraController" };
		if (_cameraController.GetParent() == null) AddChild(_cameraController);
		_cameraController.Initialize(GetCameraTargetPosition);
		DeactivateLegacySceneCameraIfNeeded();

		// Setup directional light
		_sun = GetNodeOrNull<DirectionalLight3D>("DirectionalLight3D");
		if (_sun == null)
		{
			_sun = new DirectionalLight3D();
			AddChild(_sun);
		}
		ApplyMonoGameLightingDefaults();

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
		_ambientManager = GetNodeOrNull<LorenciaAmbientManager>("LorenciaAmbientManager")
			?? new LorenciaAmbientManager { Name = "LorenciaAmbientManager" };
		if (_ambientManager.GetParent() == null) AddChild(_ambientManager);
		_ambientManager.Initialize(_modelBuilder, _terrainBuilder);
		_houseOcclusionSystem = GetNodeOrNull<LorenciaHouseOcclusionSystem>("LorenciaHouseOcclusionSystem")
			?? new LorenciaHouseOcclusionSystem { Name = "LorenciaHouseOcclusionSystem" };
		if (_houseOcclusionSystem.GetParent() == null) AddChild(_houseOcclusionSystem);
		_houseOcclusionSystem.Initialize(GetCameraTargetPosition);

		_audioManager = GetNodeOrNull<WorldAudioManager>("WorldAudioManager")
			?? new WorldAudioManager { Name = "WorldAudioManager" };
		if (_audioManager.GetParent() == null) AddChild(_audioManager);
		_audioManager.Initialize(DataPath, _terrainBuilder, GetCameraTargetPosition);

		_cullingSystem = GetNodeOrNull<ObjectCullingSystem>("ObjectCullingSystem")
			?? new ObjectCullingSystem { Name = "ObjectCullingSystem" };
		if (_cullingSystem.GetParent() == null) AddChild(_cullingSystem);
		_cullingSystem.Initialize(_cameraController.Camera);

		_darkWizardController = GetNodeOrNull<DarkWizardController>("DarkWizardController")
			?? new DarkWizardController { Name = "DarkWizardController" };
		if (_darkWizardController.GetParent() == null) AddChild(_darkWizardController);
		_darkWizardController.Initialize(_modelBuilder, _terrainBuilder, _cameraController.Camera, DataPath, _cameraController.ResetAndUpdate);
		_ = _lorenciaInteractionSystem.InitializeAsync(_cameraController.Camera, _darkWizardController);

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
		_uiLayer = new CanvasLayer { Name = "UI" };
		AddChild(_uiLayer);

		_statusLabel = new Label();
		_statusLabel.Position = new Vector2(10, 10);
		_statusLabel.AddThemeColorOverride("font_color", new Color(1, 1, 1));
		_statusLabel.AddThemeFontSizeOverride("font_size", 16);
		_statusLabel.Text = "Loading...";
		_uiLayer.AddChild(_statusLabel);

		var helpLabel = new Label();
		helpLabel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
		helpLabel.OffsetLeft = 10;
		helpLabel.OffsetTop = -28;
		helpLabel.AddThemeColorOverride("font_color", new Color(1, 1, 0.7f));
		helpLabel.AddThemeFontSizeOverride("font_size", 14);
		helpLabel.Text = "LMB: Move | MMB drag: Rotate camera | MMB click: Reset | Wheel: Zoom";
		_uiLayer.AddChild(helpLabel);

		var panel = new PanelContainer { Name = "EquipmentPanel" };
		panel.SetAnchorsPreset(Control.LayoutPreset.TopWide);
		panel.OffsetLeft = 10;
		panel.OffsetTop = 38;
		panel.OffsetRight = -10;
		panel.OffsetBottom = 170;
		_uiLayer.AddChild(panel);

		var scroll = new ScrollContainer
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill
		};
		panel.AddChild(scroll);

		var flow = new HFlowContainer
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};
		flow.AddThemeConstantOverride("h_separation", 10);
		scroll.AddChild(flow);

		AddEquipmentField(flow, "Armor", out _armorOption);
		AddEquipmentField(flow, "Left Hand", out _leftWeaponOption);
		AddEquipmentField(flow, "Right Hand", out _rightWeaponOption);
		AddEquipmentField(flow, "Wing", out _wingOption);
		AddItemLevelField(flow);
		AddItemFlagField(flow, "Excellent", out _excellentCheck);
		AddItemFlagField(flow, "Ancient", out _ancientCheck);

		_armorOption?.Connect("item_selected", Callable.From<long>(OnEquipmentSelectionChanged));
		_leftWeaponOption?.Connect("item_selected", Callable.From<long>(OnEquipmentSelectionChanged));
		_rightWeaponOption?.Connect("item_selected", Callable.From<long>(OnEquipmentSelectionChanged));
		_wingOption?.Connect("item_selected", Callable.From<long>(OnEquipmentSelectionChanged));
		_itemLevelSpin?.Connect("value_changed", Callable.From<double>(OnEquipmentLevelChanged));
		_excellentCheck?.Connect("toggled", Callable.From<bool>(OnEquipmentFlagChanged));
		_ancientCheck?.Connect("toggled", Callable.From<bool>(OnEquipmentFlagChanged));
	}

	private static void AddEquipmentField(Godot.Container parent, string label, out OptionButton option)
	{
		var box = new VBoxContainer
		{
			CustomMinimumSize = new Vector2(150, 0)
		};
		parent.AddChild(box);
		box.AddChild(new Label { Text = label });
		option = new OptionButton();
		box.AddChild(option);
	}

	private void AddItemLevelField(Godot.Container parent)
	{
		var box = new VBoxContainer
		{
			CustomMinimumSize = new Vector2(120, 0)
		};
		parent.AddChild(box);
		box.AddChild(new Label { Text = "Item Level" });
		_itemLevelSpin = new SpinBox
		{
			MinValue = 0,
			MaxValue = 15,
			Step = 1,
			Rounded = true,
			Value = 13
		};
		box.AddChild(_itemLevelSpin);
	}

	private static void AddItemFlagField(Godot.Container parent, string label, out CheckBox checkBox)
	{
		var box = new VBoxContainer
		{
			CustomMinimumSize = new Vector2(110, 0)
		};
		parent.AddChild(box);
		box.AddChild(new Label { Text = label });
		checkBox = new CheckBox
		{
			Text = "On"
		};
		box.AddChild(checkBox);
	}

	private async Task PopulateEquipmentUiAsync()
	{
		if (_armorOption == null ||
			_leftWeaponOption == null ||
			_rightWeaponOption == null ||
			_wingOption == null ||
			_itemLevelSpin == null ||
			_excellentCheck == null ||
			_ancientCheck == null)
			return;

		var uiData = await _darkWizardController.GetEquipmentUiDataAsync();
		_suppressEquipmentUiEvents = true;
		try
		{
			FillArmorOptions(_armorOption, uiData.Armors, uiData.Current.ArmorSetId);
			FillWeaponOptions(_leftWeaponOption, uiData.Weapons, uiData.Current.LeftHandGroup, uiData.Current.LeftHandId);
			FillWeaponOptions(_rightWeaponOption, uiData.Weapons, uiData.Current.RightHandGroup, uiData.Current.RightHandId);
			FillWingOptions(_wingOption, uiData.Wings, uiData.Current.WingId);
			_itemLevelSpin.Value = uiData.Current.ItemLevel;
			_excellentCheck.ButtonPressed = uiData.Current.IsExcellent;
			_ancientCheck.ButtonPressed = uiData.Current.IsAncient;
		}
		finally
		{
			_suppressEquipmentUiEvents = false;
		}
	}

	private static void FillArmorOptions(OptionButton option, IReadOnlyList<MuItemCatalog.ItemDef> armors, int selectedId)
	{
		option.Clear();
		option.AddItem("Class default", UiArmorClassDefaultId);
		for (int i = 0; i < armors.Count; i++)
		{
			var armor = armors[i];
			option.AddItem($"{armor.Name} ({armor.Id})", armor.Id);
		}

		int target = selectedId < 0 ? UiArmorClassDefaultId : selectedId;
		SelectOptionById(option, target);
	}

	private static void FillWeaponOptions(OptionButton option, IReadOnlyList<MuItemCatalog.ItemDef> weapons, int selectedGroup, int selectedId)
	{
		option.Clear();
		option.AddItem("None", UiNoneId);
		for (int i = 0; i < weapons.Count; i++)
		{
			var weapon = weapons[i];
			int key = EncodeItemKey(weapon.Group, weapon.Id);
			option.AddItem($"[{weapon.Group}] {weapon.Name} ({weapon.Id})", key);
		}

		int selectedKey = selectedGroup < 0 || selectedId < 0 ? UiNoneId : EncodeItemKey((byte)selectedGroup, (short)selectedId);
		SelectOptionById(option, selectedKey);
	}

	private static void FillWingOptions(OptionButton option, IReadOnlyList<MuItemCatalog.ItemDef> wings, int selectedId)
	{
		option.Clear();
		option.AddItem("None", UiNoneId);
		for (int i = 0; i < wings.Count; i++)
		{
			var wing = wings[i];
			option.AddItem($"{wing.Name} ({wing.Id})", wing.Id);
		}

		int target = selectedId < 0 ? UiNoneId : selectedId;
		SelectOptionById(option, target);
	}

	private static void SelectOptionById(OptionButton option, int targetId)
	{
		for (int i = 0; i < option.ItemCount; i++)
		{
			if (option.GetItemId(i) != targetId)
				continue;
			option.Selected = i;
			return;
		}

		option.Selected = 0;
	}

	private static int EncodeItemKey(byte group, short id)
	{
		return (group << 16) | (ushort)id;
	}

	private static (int Group, int Id) DecodeItemKey(int key)
	{
		if (key == UiNoneId || key < 0)
			return (-1, -1);
		int group = (key >> 16) & 0xFF;
		int id = key & 0xFFFF;
		return (group, id);
	}

	private void OnEquipmentSelectionChanged(long _selectedIndex)
	{
		if (_suppressEquipmentUiEvents)
			return;
		_ = ApplyEquipmentFromUiAsync();
	}

	private void OnEquipmentLevelChanged(double _value)
	{
		if (_suppressEquipmentUiEvents)
			return;
		_ = ApplyEquipmentFromUiAsync();
	}

	private void OnEquipmentFlagChanged(bool _value)
	{
		if (_suppressEquipmentUiEvents)
			return;
		_ = ApplyEquipmentFromUiAsync();
	}

	private async Task ApplyEquipmentFromUiAsync()
	{
		if (_armorOption == null ||
			_leftWeaponOption == null ||
			_rightWeaponOption == null ||
			_wingOption == null ||
			_itemLevelSpin == null ||
			_excellentCheck == null ||
			_ancientCheck == null)
			return;
		if (_loading)
			return;

		int armorId = _armorOption.GetItemId(_armorOption.Selected);
		int leftKey = _leftWeaponOption.GetItemId(_leftWeaponOption.Selected);
		int rightKey = _rightWeaponOption.GetItemId(_rightWeaponOption.Selected);
		int wingId = _wingOption.GetItemId(_wingOption.Selected);
		if (armorId == UiArmorClassDefaultId)
			armorId = -1;
		if (wingId == UiNoneId)
			wingId = -1;
		var left = DecodeItemKey(leftKey);
		var right = DecodeItemKey(rightKey);

		var preset = new DarkWizardController.EquipmentPreset(
			armorId,
			(int)_itemLevelSpin.Value,
			_excellentCheck.ButtonPressed,
			_ancientCheck.ButtonPressed,
			left.Group,
			left.Id,
			right.Group,
			right.Id,
			wingId);
		await _darkWizardController.ApplyEquipmentPresetAsync(preset);
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
			_ambientManager?.Clear();
			ClearChildren(_terrainRoot);
			ClearChildren(_objectsRoot);
			ClearChildren(_charactersRoot);
			_darkWizardController.Reset();
			_audioManager?.Stop();

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
				await _grassRenderer.BuildAsync(WorldIndex, _terrainRoot, _cameraController.GetEditorAwarePosition());
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
				_cullingSystem.RebuildCaches(_objectsRoot);
				_houseOcclusionSystem?.RebuildCaches(_objectsRoot, WorldIndex);
				_lorenciaInteractionSystem.RebuildCaches(_objectsRoot, WorldIndex);
				if (FullObjectOwnershipPassInEditor)
					ExposeGeneratedNodesInEditor(_objectsRoot);
			}
			else
			{
				_cullingSystem.RebuildCaches(_objectsRoot);
				_houseOcclusionSystem?.RebuildCaches(_objectsRoot, WorldIndex);
				_lorenciaInteractionSystem.RebuildCaches(_objectsRoot, WorldIndex);
			}

			if (_ambientManager != null)
				await _ambientManager.ConfigureForWorldAsync(WorldIndex, _objectsRoot, LimitEffectsInEditor);
			if (FullObjectOwnershipPassInEditor)
				ExposeGeneratedNodesInEditor(_objectsRoot);
			if (!editorMode)
			{
				await _darkWizardController.SpawnAsync(_charactersRoot, WorldIndex, UpdateStatus);
				await PopulateEquipmentUiAsync();
			}

			_audioManager?.ConfigureForWorld(WorldIndex);
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
		SetOwnerIfNeeded(_cameraController, editedSceneRoot);
		SetOwnerIfNeeded(_cullingSystem, editedSceneRoot);
		SetOwnerIfNeeded(_darkWizardController, editedSceneRoot);
		SetOwnerIfNeeded(_sun, editedSceneRoot);
		SetOwnerIfNeeded(_terrainRoot, editedSceneRoot);
		SetOwnerIfNeeded(_objectsRoot, editedSceneRoot);
		SetOwnerIfNeeded(_charactersRoot, editedSceneRoot);
		SetOwnerIfNeeded(_audioManager, editedSceneRoot);
		SetOwnerIfNeeded(_ambientManager, editedSceneRoot);
		SetOwnerIfNeeded(_houseOcclusionSystem, editedSceneRoot);
	}

	private static void SetOwnerIfNeeded(Node? node, Node owner)
	{
		if (node == null || !GodotObject.IsInstanceValid(node) || node == owner || node.Owner == owner)
			return;

		node.Owner = owner;
	}

	private void DeactivateLegacySceneCameraIfNeeded()
	{
		// Main.tscn still contains the pre-refactor root Camera3D.
		// Keep it as a node for compatibility, but force gameplay camera ownership to CameraController.
		var legacyCamera = GetNodeOrNull<Camera3D>("Camera3D");
		if (legacyCamera != null &&
			GodotObject.IsInstanceValid(legacyCamera) &&
			legacyCamera != _cameraController.Camera)
		{
			legacyCamera.Current = false;
		}
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

		if (@event is InputEventMouseButton mouseButton && mouseButton.Pressed &&
			mouseButton.ButtonIndex == MouseButton.Left)
		{
			// Prevent terrain click-through when interacting with equipment UI.
			var hoveredControl = GetViewport().GuiGetHoveredControl();
			if (hoveredControl != null && GodotObject.IsInstanceValid(hoveredControl))
				return;

			if (_lorenciaInteractionSystem.TryHandleLeftClick(mouseButton.Position))
				return;
			_darkWizardController.HandleInput(mouseButton.Position);
		}
	}

	public override void _Process(double delta)
	{
		ApplyEditorPerformanceSettings();

		bool isEditor = Engine.IsEditorHint();
		if (!isEditor)
		{
			_darkWizardController.Update(delta);
			_cameraController.Update(delta);
			_lorenciaInteractionSystem.Update(
				delta,
				GetViewport().GetMousePosition(),
				Input.IsMouseButtonPressed(MouseButton.Left));
		}

		_houseOcclusionSystem?.UpdateOcclusion(delta, WorldIndex, enabledInCurrentMode: !isEditor);

		Vector3 cameraPosition = _cameraController.GetEditorAwarePosition();
		_cullingSystem.Update(delta, cameraPosition);

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
					_ambientManager?.Update(fxDelta, cameraPosition);
				}
			}
			else
			{
				_editorFxUpdateAccumulator = 0f;
				_ambientManager?.Update(delta, cameraPosition);
			}

			_audioManager?.Update(WorldIndex);

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
		_ambientManager?.Update(delta, cameraPosition);
		_audioManager?.Update(WorldIndex);
	}

	private Vector3 GetCameraTargetPosition()
	{
		if (_darkWizardController != null && GodotObject.IsInstanceValid(_darkWizardController))
			return _darkWizardController.GetPosition();
		return new Vector3(128f, 0f, -128f);
	}

	public override void _ExitTree()
	{
		_ambientManager?.Clear();
		_houseOcclusionSystem?.ResetToOpaque();
		LorenciaFireEmitter.ResetSharedAssetsForReload();
		if (_charactersRoot != null && GodotObject.IsInstanceValid(_charactersRoot))
			ClearChildren(_charactersRoot);
		_darkWizardController?.Reset();
		_audioManager?.Stop();
		base._ExitTree();
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
