using Godot;
using Client.Data.ATT;
using Client.Data.BMD;
using Client.Data.CAP;
using MuGodot.Audio;
using System.Threading;

namespace MuGodot;

[Tool]
public partial class DarkWizardController : Node3D
{
	private enum SpecialPoseMode
	{
		None,
		Sit,
		Rest
	}

	private const float MonoGameMoveSpeed = 300f * MuConfig.WorldToGodot;
	private const int IdleAction = 1;
	private const int WalkAction = 15;
	private const int LeftHandBoneIndex = 33;
	private const int RightHandBoneIndex = 42;
	private const int WingBoneIndex = 47;
	private const string ItemMaterialShaderPath = "res://Shaders/ItemMaterial.gdshader";
	private static readonly byte[] ArmorGroupsByPart = { 7, 8, 9, 10, 11 };
	private static readonly string[] BodyPartPrefixes =
	{
		"HelmClass",
		"ArmorClass",
		"PantClass",
		"GloveClass",
		"BootClass"
	};

	[ExportGroup("DarkWizard")]
	[Export] public string DarkWizardModelPath { get; set; } = "Player/Player.bmd";
	[Export] public int DarkWizardClassModelId { get; set; } = 1;
	[Export] public float DarkWizardMoveSpeed { get; set; } = MonoGameMoveSpeed;
	[Export] public float DarkWizardHeightOffset { get; set; } = 0f;
	[Export] public float DarkWizardFacingOffsetDegrees { get; set; } = 0f;
	[Export] public float DarkWizardTurnSmoothness { get; set; } = 16f;
	[Export] public float DarkWizardAnimationSpeed { get; set; } = 6.25f;
	[Export] public int DarkWizardSubFrameSamples { get; set; } = 8;
	[Export] public bool DarkWizardRealtimeInterpolation { get; set; } = true;
	[Export] public int DarkWizardSitActionIndex { get; set; } = 233;
	[Export] public int DarkWizardRestActionIndex { get; set; } = 239;
	[ExportGroup("DarkWizard Equipment")]
	[Export] public int EquippedArmorSetId { get; set; } = -1;
	[Export] public int EquippedItemLevel { get; set; } = 13;
	[Export] public bool EquippedIsExcellent { get; set; } = false;
	[Export] public bool EquippedIsAncient { get; set; } = false;
	[Export] public int EquippedLeftHandGroup { get; set; } = -1;
	[Export] public int EquippedLeftHandId { get; set; } = -1;
	[Export] public int EquippedRightHandGroup { get; set; } = -1;
	[Export] public int EquippedRightHandId { get; set; } = -1;
	[Export] public int EquippedWingId { get; set; } = -1;
	[Export(PropertyHint.Range, "0,1.5,0.01")] public float ItemGlowIntensityScale { get; set; } = 0.8f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float ItemEffectMix { get; set; } = 0.55f;
	[ExportGroup("DarkWizard Equipment Bones")]
	[Export] public int LeftHandBoneLink { get; set; } = LeftHandBoneIndex;
	[Export] public int RightHandBoneLink { get; set; } = RightHandBoneIndex;
	[Export] public int WingBoneLink { get; set; } = WingBoneIndex;
	[ExportGroup("DarkWizard Equipment Offsets")]
	[Export] public Vector3 LeftHandItemOffset { get; set; } = Vector3.Zero;
	[Export] public Vector3 LeftHandItemRotationDegrees { get; set; } = Vector3.Zero;
	[Export] public Vector3 RightHandItemOffset { get; set; } = Vector3.Zero;
	[Export] public Vector3 RightHandItemRotationDegrees { get; set; } = Vector3.Zero;
	[Export] public Vector3 WingItemOffset { get; set; } = Vector3.Zero;
	[Export] public Vector3 WingItemRotationDegrees { get; set; } = Vector3.Zero;
	[ExportGroup("DarkWizard Audio")]
	[Export] public string SitSoundPath { get; set; } = "Sound/pDropItem.wav";
	[Export] public string RestSoundPath { get; set; } = "Sound/pDropItem.wav";
	[Export] public float FootstepVolumeDb { get; set; } = -4f;
	[Export] public float PoseVolumeDb { get; set; } = -2f;

	private MuModelBuilder _modelBuilder = null!;
	private MuTerrainBuilder _terrainBuilder = null!;
	private Camera3D? _camera;
	private string _dataPath = "";
	private Action? _onCameraReset;
	private int _worldIndex;
	private MuItemCatalog _itemCatalog = new();
	private BMD? _skeletonBmd;
	private readonly SemaphoreSlim _equipmentUpdateLock = new(1, 1);
	private static Shader? s_itemMaterialShader;

	private sealed class EquipmentAttachmentRuntime
	{
		public int BoneIndex;
		public Node3D? Pivot;
		public MuAnimatedMeshController? AnimationController;
		public MeshInstance3D? MeshInstance;
	}

	private EquipmentAttachmentRuntime _leftAttachment = new() { BoneIndex = LeftHandBoneIndex };
	private EquipmentAttachmentRuntime _rightAttachment = new() { BoneIndex = RightHandBoneIndex };
	private EquipmentAttachmentRuntime _wingAttachment = new() { BoneIndex = WingBoneIndex };

	private Node3D? _root;
	private Node3D? _moveTargetMarker;
	private MeshInstance3D? _moveTargetMesh;
	private MuAnimatedMeshController? _moveTargetAnimController;
	private StandardMaterial3D[] _moveTargetMaterials = Array.Empty<StandardMaterial3D>();
	private Color[] _moveTargetBaseAlbedo = Array.Empty<Color>();
	private float _moveTargetVisibleMs;
	private const float MoveTargetDurationMs = 1500f;
	private readonly List<MeshInstance3D> _meshes = new();
	private readonly List<MuAnimatedMeshController> _idleControllers = new();
	private readonly List<MuAnimatedMeshController> _walkControllers = new();
	private readonly List<MuAnimatedMeshController> _sitControllers = new();
	private readonly List<MuAnimatedMeshController> _restControllers = new();
	private bool _moving;
	private SpecialPoseMode _specialPose = SpecialPoseMode.None;
	private Vector3 _moveTarget;
	private readonly Queue<Vector2I> _path = new();
	private float _targetYaw;
	private bool _hasTargetYaw;
	private Vector3 _fallbackPosition = new Vector3(128f, 0f, -128f);
	private AudioStreamPlayer3D? _footstepPlayer;
	private AudioStreamPlayer3D? _posePlayer;
	private AudioStream? _walkGrassSound;
	private AudioStream? _walkSnowSound;
	private AudioStream? _walkSoilSound;
	private AudioStream? _swimSound;
	private AudioStream? _sitSound;
	private AudioStream? _restSound;
	private float _footstepTimer;

	public event Action<Vector2I, IReadOnlyList<Vector2I>>? MovePathPlanned;

	public void Initialize(MuModelBuilder modelBuilder, MuTerrainBuilder terrainBuilder, Camera3D? camera, string dataPath, Action onCameraReset)
	{
		_modelBuilder = modelBuilder;
		_terrainBuilder = terrainBuilder;
		_camera = camera;
		_dataPath = dataPath;
		_onCameraReset = onCameraReset;
		SyncAttachmentBoneLinks();
	}

	public sealed record EquipmentPreset(
		int ArmorSetId,
		int ItemLevel,
		bool IsExcellent,
		bool IsAncient,
		int LeftHandGroup,
		int LeftHandId,
		int RightHandGroup,
		int RightHandId,
		int WingId);

	public sealed record EquipmentUiData(
		IReadOnlyList<MuItemCatalog.ItemDef> Armors,
		IReadOnlyList<MuItemCatalog.ItemDef> Weapons,
		IReadOnlyList<MuItemCatalog.ItemDef> Wings,
		EquipmentPreset Current);

	public async Task<EquipmentUiData> GetEquipmentUiDataAsync()
	{
		await EnsureItemCatalogAsync();
		return new EquipmentUiData(
			_itemCatalog.Armors,
			_itemCatalog.Weapons,
			_itemCatalog.Wings,
			new EquipmentPreset(
				EquippedArmorSetId,
				EquippedItemLevel,
				EquippedIsExcellent,
				EquippedIsAncient,
				EquippedLeftHandGroup,
				EquippedLeftHandId,
				EquippedRightHandGroup,
				EquippedRightHandId,
				EquippedWingId));
	}

	public async Task ApplyEquipmentPresetAsync(EquipmentPreset preset)
	{
		await _equipmentUpdateLock.WaitAsync();
		try
		{
			SyncAttachmentBoneLinks();
			EquippedArmorSetId = preset.ArmorSetId;
			EquippedItemLevel = Math.Clamp(preset.ItemLevel, 0, 15);
			EquippedIsExcellent = preset.IsExcellent;
			EquippedIsAncient = preset.IsAncient;
			EquippedLeftHandGroup = preset.LeftHandGroup;
			EquippedLeftHandId = preset.LeftHandId;
			EquippedRightHandGroup = preset.RightHandGroup;
			EquippedRightHandId = preset.RightHandId;
			EquippedWingId = preset.WingId;

			await EnsureItemCatalogAsync();
			if (_root != null && GodotObject.IsInstanceValid(_root) && _skeletonBmd != null)
			{
				await RebuildBodyAndEquipmentAsync(_root, _skeletonBmd);
				SetAnimationState(_moving, force: true);
				UpdateEquipmentAttachmentTransforms();
			}
		}
		finally
		{
			_equipmentUpdateLock.Release();
		}
	}

	public Vector3 GetPosition()
	{
		if (_root != null && GodotObject.IsInstanceValid(_root))
			return _root.GlobalPosition;
		return _fallbackPosition;
	}

	public void TeleportToTile(byte tileX, byte tileY, byte rotation = 0)
	{
		float x = Mathf.Clamp(tileX + 0.5f, 0f, MuConfig.TerrainSize - 1.001f);
		float y = Mathf.Clamp(tileY + 0.5f, 0f, MuConfig.TerrainSize - 1.001f);
		float height = _terrainBuilder.GetHeightInterpolated(x, y) + DarkWizardHeightOffset;
		var target = new Vector3(x, height, -y);

		_fallbackPosition = target;
		if (_root == null || !GodotObject.IsInstanceValid(_root))
		{
			return;
		}

		_root.Position = target;
		_moveTarget = target;
		_path.Clear();
		_moving = false;
		_specialPose = SpecialPoseMode.None;
		SetAnimationState(isMoving: false, force: true);

		float yaw = Mathf.DegToRad((rotation & 0x0F) * (360f / 16f)) + Mathf.DegToRad(DarkWizardFacingOffsetDegrees);
		_targetYaw = yaw;
		_hasTargetYaw = true;

		var rotationVector = _root.Rotation;
		rotationVector.Y = yaw;
		_root.Rotation = rotationVector;
		UpdateEquipmentAttachmentTransforms();
	}

	public void HandleInput(Vector2 mousePosition)
	{
		TrySetMoveTarget(mousePosition);
	}

	public bool TryMoveToTile(Vector2I tile, bool showMarker = true)
	{
		return TrySetMoveTargetTile(tile.X, tile.Y, showMarker);
	}

	public bool IsMoving()
	{
		if (_root == null || !GodotObject.IsInstanceValid(_root))
			return false;

		Vector2 remaining = new Vector2(_moveTarget.X - _root.Position.X, _moveTarget.Z - _root.Position.Z);
		return _path.Count > 0 || remaining.LengthSquared() > 0.0001f;
	}

	public Vector2I GetCurrentTile()
	{
		if (_root == null || !GodotObject.IsInstanceValid(_root))
			return new Vector2I(0, 0);

		return new Vector2I(
			Math.Clamp((int)MathF.Floor(_root.Position.X), 0, MuConfig.TerrainSize - 1),
			Math.Clamp((int)MathF.Floor(-_root.Position.Z), 0, MuConfig.TerrainSize - 1));
	}

	public bool IsNearTile(Vector2I tile, float maxDistance = 0.15f)
	{
		if (_root == null || !GodotObject.IsInstanceValid(_root))
			return false;

		float targetX = Mathf.Clamp(tile.X + 0.5f, 0f, MuConfig.TerrainSize - 1.001f);
		float targetY = Mathf.Clamp(tile.Y + 0.5f, 0f, MuConfig.TerrainSize - 1.001f);
		Vector2 delta = new Vector2(_root.Position.X - targetX, _root.Position.Z - (-targetY));
		return delta.Length() <= MathF.Max(0.01f, maxDistance);
	}

	public void EnterSitPose(float facingYaw)
	{
		EnterSpecialPose(SpecialPoseMode.Sit, facingYaw);
	}

	public void EnterRestPose(float facingYaw)
	{
		EnterSpecialPose(SpecialPoseMode.Rest, facingYaw);
	}

	public void ClearSpecialPose()
	{
		if (_specialPose == SpecialPoseMode.None)
			return;

		_specialPose = SpecialPoseMode.None;
		SetAnimationState(_moving, force: true);
	}

	public void Update(double delta)
	{
		UpdateMovement(delta);
	}

	public void Reset()
	{
		SyncAttachmentBoneLinks();
		_root = null;
		_moveTargetMarker = null;
		_moveTargetMesh = null;
		_moveTargetAnimController = null;
		_moveTargetMaterials = Array.Empty<StandardMaterial3D>();
		_moveTargetBaseAlbedo = Array.Empty<Color>();
		_moveTargetVisibleMs = 0f;
		_meshes.Clear();
		_idleControllers.Clear();
		_walkControllers.Clear();
		_sitControllers.Clear();
		_restControllers.Clear();
		_moving = false;
		_specialPose = SpecialPoseMode.None;
		_path.Clear();
		_hasTargetYaw = false;
		_footstepPlayer = null;
		_posePlayer = null;
		_footstepTimer = 0f;
		_skeletonBmd = null;
		_leftAttachment = new EquipmentAttachmentRuntime { BoneIndex = LeftHandBoneLink };
		_rightAttachment = new EquipmentAttachmentRuntime { BoneIndex = RightHandBoneLink };
		_wingAttachment = new EquipmentAttachmentRuntime { BoneIndex = WingBoneLink };
	}

	public async Task SpawnAsync(Node3D charactersRoot, int worldIndex, Action<string>? onStatus = null)
	{
		SyncAttachmentBoneLinks();
		if (charactersRoot == null || !GodotObject.IsInstanceValid(charactersRoot))
			return;

		ClearChildren(charactersRoot);
		Reset();

		var spawn = await ResolveSpawnPositionAsync(worldIndex);
		_worldIndex = worldIndex;
		_fallbackPosition = spawn;

		var root = new Node3D { Name = "DarkWizard" };
		charactersRoot.AddChild(root);
		_root = root;
		EnsureAudioPlayers();

		await EnsureItemCatalogAsync();
		BMD? skeletonBmd = await _modelBuilder.LoadBmdAsync(DarkWizardModelPath);
		_skeletonBmd = skeletonBmd;
		if (_skeletonBmd == null)
		{
			GD.PrintErr($"[DarkWizard] BMD not found: {DarkWizardModelPath}. Using fallback mesh.");
			var meshInstance = new MeshInstance3D { Name = "FallbackBody" };
			root.AddChild(meshInstance);
			_meshes.Add(meshInstance);
			ApplyFallbackMesh(meshInstance);
			_root.Position = spawn;
			_moveTarget = spawn;
			_path.Clear();
			_targetYaw = _root.Rotation.Y;
			_hasTargetYaw = true;
			await EnsureMoveTargetMarkerAsync();
			_onCameraReset?.Invoke();
			onStatus?.Invoke("DarkWizard model missing (using fallback).");
			return;
		}

		bool hasRenderablePart = await RebuildBodyAndEquipmentAsync(root, _skeletonBmd);
		if (!hasRenderablePart)
		{
			GD.PrintErr($"[DarkWizard] No class body parts found for class {Math.Max(1, DarkWizardClassModelId)}. Using fallback mesh.");
			var meshInstance = new MeshInstance3D { Name = "FallbackBody" };
			root.AddChild(meshInstance);
			_meshes.Add(meshInstance);
			ApplyFallbackMesh(meshInstance);
			_root.Position = spawn;
			_moveTarget = spawn;
			_path.Clear();
			_targetYaw = _root.Rotation.Y;
			_hasTargetYaw = true;
			await EnsureMoveTargetMarkerAsync();
			_onCameraReset?.Invoke();
			onStatus?.Invoke("DarkWizard class models missing (using fallback).");
			return;
		}

		_root.Position = spawn;
		_moveTarget = spawn;
		_path.Clear();
		_targetYaw = _root.Rotation.Y;
		_hasTargetYaw = true;
		SetAnimationState(isMoving: false, force: true);
		await EnsureMoveTargetMarkerAsync();
		_onCameraReset?.Invoke();
		GD.Print($"[DarkWizard] Spawned at {spawn}. Parts: {_meshes.Count}, walk controllers: {_walkControllers.Count}");
	}

	private async Task EnsureItemCatalogAsync()
	{
		if (_itemCatalog.Weapons.Count > 0 || _itemCatalog.Armors.Count > 0 || _itemCatalog.Wings.Count > 0)
			return;
		if (string.IsNullOrWhiteSpace(_dataPath) || !System.IO.Directory.Exists(_dataPath))
			return;

		_itemCatalog = await MuItemCatalog.LoadAsync(_dataPath);
	}

	private bool ShouldUseItemMaterialShader()
	{
		return EquippedItemLevel >= 7 || EquippedIsExcellent || EquippedIsAncient;
	}

	private int BuildItemOptions()
	{
		int options = EquippedItemLevel & 0x0F;
		if (EquippedIsExcellent)
			options |= 0x10;
		return options;
	}

	private Material[] BuildEquipmentMaterials(StandardMaterial3D[] sourceMaterials, bool applyItemVisuals)
	{
		if (!applyItemVisuals || !ShouldUseItemMaterialShader())
			return sourceMaterials;

		var shader = s_itemMaterialShader;
		if (shader == null || !GodotObject.IsInstanceValid(shader))
		{
			shader = ResourceLoader.Load<Shader>(ItemMaterialShaderPath);
			s_itemMaterialShader = shader;
		}

		if (shader == null || !GodotObject.IsInstanceValid(shader))
			return sourceMaterials;

		var result = new Material[sourceMaterials.Length];
		int itemOptions = BuildItemOptions();
		for (int i = 0; i < sourceMaterials.Length; i++)
		{
			var src = sourceMaterials[i];
			if (src == null || src.AlbedoTexture == null)
			{
				result[i] = src;
				continue;
			}

			var shaderMat = new ShaderMaterial { Shader = shader };
			shaderMat.SetShaderParameter("diffuse_texture", src.AlbedoTexture);
			shaderMat.SetShaderParameter("item_options", itemOptions);
			shaderMat.SetShaderParameter("is_ancient", EquippedIsAncient);
			shaderMat.SetShaderParameter("is_excellent", EquippedIsExcellent);
			shaderMat.SetShaderParameter("glow_color", new Vector3(0.6f, 0.5f, 0.0f));
			shaderMat.SetShaderParameter("light_direction", new Vector3(0.707f, -0.707f, 0f));
			shaderMat.SetShaderParameter("glow_intensity_scale", ItemGlowIntensityScale);
			shaderMat.SetShaderParameter("effect_mix", ItemEffectMix);
			result[i] = shaderMat;
		}

		return result;
	}

	private void SyncAttachmentBoneLinks()
	{
		LeftHandBoneLink = Math.Max(0, LeftHandBoneLink);
		RightHandBoneLink = Math.Max(0, RightHandBoneLink);
		WingBoneLink = Math.Max(0, WingBoneLink);

		_leftAttachment.BoneIndex = LeftHandBoneLink;
		_rightAttachment.BoneIndex = RightHandBoneLink;
		_wingAttachment.BoneIndex = WingBoneLink;
	}

	private void ClearBodyControllersAndMeshes()
	{
		for (int i = 0; i < _idleControllers.Count; i++)
		{
			var c = _idleControllers[i];
			if (c != null && GodotObject.IsInstanceValid(c))
			{
				if (c.GetParent() != null)
					c.GetParent().RemoveChild(c);
				c.QueueFree();
			}
		}
		for (int i = 0; i < _walkControllers.Count; i++)
		{
			var c = _walkControllers[i];
			if (c != null && GodotObject.IsInstanceValid(c))
			{
				if (c.GetParent() != null)
					c.GetParent().RemoveChild(c);
				c.QueueFree();
			}
		}
		for (int i = 0; i < _sitControllers.Count; i++)
		{
			var c = _sitControllers[i];
			if (c != null && GodotObject.IsInstanceValid(c))
			{
				if (c.GetParent() != null)
					c.GetParent().RemoveChild(c);
				c.QueueFree();
			}
		}
		for (int i = 0; i < _restControllers.Count; i++)
		{
			var c = _restControllers[i];
			if (c != null && GodotObject.IsInstanceValid(c))
			{
				if (c.GetParent() != null)
					c.GetParent().RemoveChild(c);
				c.QueueFree();
			}
		}
		for (int i = 0; i < _meshes.Count; i++)
		{
			var mesh = _meshes[i];
			if (mesh != null && GodotObject.IsInstanceValid(mesh))
			{
				if (mesh.GetParent() != null)
					mesh.GetParent().RemoveChild(mesh);
				mesh.QueueFree();
			}
		}

		_idleControllers.Clear();
		_walkControllers.Clear();
		_sitControllers.Clear();
		_restControllers.Clear();
		_meshes.Clear();
	}

	private async Task<bool> RebuildBodyAndEquipmentAsync(Node3D root, BMD skeletonBmd)
	{
		ClearBodyControllersAndMeshes();
		int idleAction = ClampActionIndex(skeletonBmd, IdleAction);
		int walkAction = ClampActionIndex(skeletonBmd, WalkAction);
		int classId = Math.Max(1, DarkWizardClassModelId);
		int subFrameSamples = Math.Clamp(DarkWizardSubFrameSamples, 1, 32);
		float animationSyncStartSeconds = Time.GetTicksMsec() * 0.001f;
		bool hasRenderablePart = false;

		for (int i = 0; i < BodyPartPrefixes.Length; i++)
		{
			string prefix = BodyPartPrefixes[i];
			var bodyPart = await ResolveBodyPartModelPathAsync(prefix, i, classId);
			if (string.IsNullOrWhiteSpace(bodyPart.Path))
				continue;

			BMD? partBmd = await _modelBuilder.LoadBmdAsync(bodyPart.Path, logMissing: false);
			if (partBmd == null)
				continue;

			var materials = await _modelBuilder.LoadModelTexturesAsync(bodyPart.Path);
			var renderMaterials = BuildEquipmentMaterials(materials, bodyPart.UsesItemVisual);
			var meshInstance = new MeshInstance3D { Name = prefix };
			root.AddChild(meshInstance);
			_meshes.Add(meshInstance);

			var idleController = CreateAnimationController(
				root, partBmd, renderMaterials, idleAction,
				$"{prefix}_Idle", skeletonBmd, subFrameSamples, animationSyncStartSeconds);
			var idleMesh = idleController.GetCurrentMesh();
			if (idleMesh == null)
			{
				idleController.QueueFree();
				meshInstance.QueueFree();
				_meshes.Remove(meshInstance);
				continue;
			}

			idleController.RegisterInstance(meshInstance);
			meshInstance.Mesh = idleMesh;
			_idleControllers.Add(idleController);

			if (walkAction != idleAction)
			{
				var walkController = CreateAnimationController(
					root, partBmd, renderMaterials, walkAction,
					$"{prefix}_Walk", skeletonBmd, subFrameSamples, animationSyncStartSeconds);
				var walkMesh = walkController.GetCurrentMesh();
				if (walkMesh != null)
				{
					walkController.RegisterInstance(meshInstance);
					_walkControllers.Add(walkController);
				}
				else
				{
					walkController.QueueFree();
				}
			}

			TryCreateOptionalPoseController(
				root, partBmd, renderMaterials, skeletonBmd, subFrameSamples, animationSyncStartSeconds, prefix,
				DarkWizardSitActionIndex, idleAction, walkAction, "_Sit", _sitControllers, meshInstance);
			TryCreateOptionalPoseController(
				root, partBmd, renderMaterials, skeletonBmd, subFrameSamples, animationSyncStartSeconds, prefix,
				DarkWizardRestActionIndex, idleAction, walkAction, "_Rest", _restControllers, meshInstance);

			hasRenderablePart = true;
		}

		await RebuildEquipmentAttachmentsAsync(root);
		UpdateEquipmentAttachmentTransforms();
		return hasRenderablePart;
	}

	private async Task<(string? Path, bool UsesItemVisual)> ResolveBodyPartModelPathAsync(string prefix, int partIndex, int classId)
	{
		// Reset to the class default first, just like the reference flow before reapplying item visuals.
		string classPath = BuildPartModelPath(prefix, classId);
		string resolvedClassPath = await ResolveExistingModelPathAsync(classPath) ?? classPath;
		if (EquippedArmorSetId < 0 || partIndex < 0 || partIndex >= ArmorGroupsByPart.Length)
			return (resolvedClassPath, false);

		byte group = ArmorGroupsByPart[partIndex];
		short id = (short)EquippedArmorSetId;
		var itemDef = _itemCatalog.Get(group, id);
		if (itemDef == null || string.IsNullOrWhiteSpace(itemDef.TexturePath))
			return (resolvedClassPath, false);

		string playerPath = itemDef.TexturePath.Replace("Item/", "Player/", StringComparison.OrdinalIgnoreCase);
		string? resolvedItemPath = await ResolveExistingModelPathAsync(playerPath)
			?? await ResolveExistingModelPathAsync(itemDef.TexturePath);
		if (!string.IsNullOrWhiteSpace(resolvedItemPath))
			return (resolvedItemPath, true);

		return (resolvedClassPath, false);
	}

	private async Task<string?> ResolveExistingModelPathAsync(string relativePath)
	{
		if (string.IsNullOrWhiteSpace(relativePath))
			return null;
		var bmd = await _modelBuilder.LoadBmdAsync(relativePath, logMissing: false);
		return bmd != null ? relativePath : null;
	}

	private async Task<string?> ResolveAttachmentModelPathAsync(string texturePath, int group, int id)
	{
		if (string.IsNullOrWhiteSpace(texturePath))
			return null;

		string normalized = texturePath.Replace("\\", "/");
		string? resolved = await ResolveExistingModelPathAsync(normalized);
		if (!string.IsNullOrWhiteSpace(resolved))
			return resolved;

		// Wing models in some clients are placed under Item/Wing/<file>, while actual data may store them in Item/<file>.
		if (normalized.Contains("Item/Wing/", StringComparison.OrdinalIgnoreCase))
		{
			string flatWingPath = $"Item/{System.IO.Path.GetFileName(normalized)}";
			resolved = await ResolveExistingModelPathAsync(flatWingPath);
			if (!string.IsNullOrWhiteSpace(resolved))
				return resolved;
		}

		// Generic fallback: try flat Item/<file>.
		string fileName = System.IO.Path.GetFileName(normalized);
		if (!string.IsNullOrWhiteSpace(fileName))
		{
			string flatItemPath = $"Item/{fileName}";
			resolved = await ResolveExistingModelPathAsync(flatItemPath);
			if (!string.IsNullOrWhiteSpace(resolved))
				return resolved;
		}

		GD.PrintErr($"[DarkWizard] Attachment model not found for group={group}, id={id}, path={texturePath}");
		return null;
	}

	private async Task RebuildEquipmentAttachmentsAsync(Node3D root)
	{
		await RebuildSingleAttachmentAsync(root, _leftAttachment, "LeftWeapon", EquippedLeftHandGroup, EquippedLeftHandId);
		await RebuildSingleAttachmentAsync(root, _rightAttachment, "RightWeapon", EquippedRightHandGroup, EquippedRightHandId);
		await RebuildSingleAttachmentAsync(root, _wingAttachment, "Wing", 12, EquippedWingId);
	}

	private async Task RebuildSingleAttachmentAsync(Node3D root, EquipmentAttachmentRuntime runtime, string nodeName, int group, int id)
	{
		if (runtime.Pivot != null && GodotObject.IsInstanceValid(runtime.Pivot))
		{
			if (runtime.Pivot.GetParent() != null)
				runtime.Pivot.GetParent().RemoveChild(runtime.Pivot);
			runtime.Pivot.QueueFree();
			runtime.Pivot = null;
			runtime.AnimationController = null;
			runtime.MeshInstance = null;
		}

		if (group < 0 || id < 0)
			return;

		var itemDef = _itemCatalog.Get((byte)group, (short)id);
		if (itemDef == null || string.IsNullOrWhiteSpace(itemDef.TexturePath))
			return;

		string? modelPath = await ResolveAttachmentModelPathAsync(itemDef.TexturePath, group, id);
		if (string.IsNullOrWhiteSpace(modelPath))
			return;

		BMD? itemBmd = await _modelBuilder.LoadBmdAsync(modelPath, logMissing: false);
		if (itemBmd == null)
			return;

		var materials = await _modelBuilder.LoadModelTexturesAsync(modelPath);
		var renderMaterials = BuildEquipmentMaterials(materials, applyItemVisuals: true);
		var pivot = new Node3D { Name = nodeName };
		var itemMesh = new MeshInstance3D { Name = $"{nodeName}Mesh" };
		pivot.AddChild(itemMesh);
		root.AddChild(pivot);
		runtime.Pivot = pivot;
		runtime.MeshInstance = itemMesh;
		ApplyAttachmentLocalOffset(runtime);

		// Wings should keep their own animation (as in reference WingObject), while still
		// being attached to the player's back bone.
		bool isWing = group == 12;
		if (isWing && _modelBuilder.HasAnimatedAction(itemBmd, 0))
		{
			var animController = new MuAnimatedMeshController { Name = $"{nodeName}Anim" };
			pivot.AddChild(animController);
			animController.Initialize(
				_modelBuilder,
				itemBmd,
				renderMaterials,
				actionIndex: 0,
				animationSpeed: 4f,
				subFrameSamples: 8,
				useRealtimeInterpolation: DarkWizardRealtimeInterpolation);
			animController.RegisterInstance(itemMesh);
			animController.SetExternalAnimationEnabled(true);
			runtime.AnimationController = animController;
			return;
		}

		ArrayMesh? mesh = _modelBuilder.BuildMesh(itemBmd, actionIndex: 0, framePos: 0f);
		if (mesh == null)
			return;
		for (int s = 0; s < mesh.GetSurfaceCount() && s < renderMaterials.Length; s++)
		{
			if (renderMaterials[s] != null)
				mesh.SurfaceSetMaterial(s, renderMaterials[s]);
		}
		itemMesh.Mesh = mesh;
	}

	private MuAnimatedMeshController CreateAnimationController(
		Node3D parent,
		BMD bmd,
		Material[] materials,
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

	private void TryCreateOptionalPoseController(
		Node3D root,
		BMD partBmd,
		Material[] materials,
		BMD skeletonBmd,
		int subFrameSamples,
		float animationSyncStartSeconds,
		string prefix,
		int requestedActionIndex,
		int idleAction,
		int walkAction,
		string suffix,
		List<MuAnimatedMeshController> targetList,
		MeshInstance3D meshInstance)
	{
		if (requestedActionIndex < 0 || skeletonBmd.Actions == null || skeletonBmd.Actions.Length == 0)
			return;

		int maxAction = skeletonBmd.Actions.Length - 1;
		if (requestedActionIndex > maxAction)
			return;

		int actionIndex = ClampActionIndex(skeletonBmd, requestedActionIndex);
		if (actionIndex == idleAction || actionIndex == walkAction)
			return;

		var controller = CreateAnimationController(
			root, partBmd, materials, actionIndex,
			$"{prefix}{suffix}", skeletonBmd, subFrameSamples, animationSyncStartSeconds);
		var mesh = controller.GetCurrentMesh();
		if (mesh == null)
		{
			controller.QueueFree();
			return;
		}

		controller.RegisterInstance(meshInstance);
		targetList.Add(controller);
	}

	private static string BuildPartModelPath(string prefix, int classId)
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

	private static void ApplyFallbackMesh(MeshInstance3D meshInstance)
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

	private async Task<Vector3> ResolveSpawnPositionAsync(int worldIndex)
	{
		string capPath = System.IO.Path.Combine(_dataPath, $"World{worldIndex}", "Camera_Angle_Position.bmd");
		if (System.IO.File.Exists(capPath))
		{
			try
			{
				var capData = await new CAPReader().Load(capPath);
				float x = capData.HeroPosition.X * MuConfig.WorldToGodot;
				float y = capData.HeroPosition.Y * MuConfig.WorldToGodot;
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

	private void TrySetMoveTarget(Vector2 mousePosition)
	{
		if (_root == null || !GodotObject.IsInstanceValid(_root))
			return;

		if (!TryRaycastTerrain(mousePosition, out var hitPos))
			return;

		int tileX = Math.Clamp((int)MathF.Floor(hitPos.X), 0, MuConfig.TerrainSize - 1);
		int tileY = Math.Clamp((int)MathF.Floor(-hitPos.Z), 0, MuConfig.TerrainSize - 1);
		TrySetMoveTargetTile(tileX, tileY, showMarker: true);
	}

	private bool TrySetMoveTargetTile(int tileX, int tileY, bool showMarker)
	{
		if (_root == null || !GodotObject.IsInstanceValid(_root))
			return false;

		tileX = Math.Clamp(tileX, 0, MuConfig.TerrainSize - 1);
		tileY = Math.Clamp(tileY, 0, MuConfig.TerrainSize - 1);
		if (!IsTileWalkable(tileX, tileY))
			return false;

		int startX = Math.Clamp((int)MathF.Floor(_root.Position.X), 0, MuConfig.TerrainSize - 1);
		int startY = Math.Clamp((int)MathF.Floor(-_root.Position.Z), 0, MuConfig.TerrainSize - 1);
		var startTile = new Vector2I(startX, startY);
		var targetTile = new Vector2I(tileX, tileY);

		var path = FindPath(startTile, targetTile);
		if (path.Count == 0)
			return false;

		ClearSpecialPose();
		_path.Clear();
		for (int i = 0; i < path.Count; i++)
			_path.Enqueue(path[i]);

		if (showMarker)
		{
			float markerX = Mathf.Clamp(tileX + 0.5f, 0f, MuConfig.TerrainSize - 1.001f);
			float markerY = Mathf.Clamp(tileY + 0.5f, 0f, MuConfig.TerrainSize - 1.001f);
			float markerHeight = _terrainBuilder.GetHeightInterpolated(markerX, markerY) + DarkWizardHeightOffset;
			ShowMoveTargetMarker(new Vector3(markerX, markerHeight, -markerY));
		}

		if (!AdvancePath(_root.Position))
		{
			SetAnimationState(isMoving: false, force: true);
			return false;
		}

		MovePathPlanned?.Invoke(startTile, path);

		return true;
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

	private void UpdateMovement(double delta)
	{
		if (_root == null || !GodotObject.IsInstanceValid(_root))
			return;

		var current = _root.Position;
		const float moveEpsilon = 0.01f;
		float stepRemaining = MathF.Max(0.1f, DarkWizardMoveSpeed) * (float)delta;
		Vector2 lastMoveDir = Vector2.Zero;
		bool movedThisFrame = false;
		int guard = 0;

		// Consume full movement step even when crossing multiple path nodes in a single frame.
		while (stepRemaining > 0f && guard++ < 64)
		{
			Vector2 moveDir = new Vector2(
				_moveTarget.X - current.X,
				_moveTarget.Z - current.Z);
			float distance = moveDir.Length();

			if (!float.IsFinite(distance))
			{
				if (!AdvancePath(current))
					break;
				continue;
			}

			if (distance <= moveEpsilon)
			{
				if (!AdvancePath(current))
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
				current.X = _moveTarget.X;
				current.Z = _moveTarget.Z;
				if (!AdvancePath(current))
					_moveTarget = current;
			}
			else
			{
				break;
			}
		}

		if (movedThisFrame && lastMoveDir.LengthSquared() > 0f)
			SetFacingTarget(lastMoveDir);

		if (TryGetTerrainHeightAtWorld(current.X, current.Z, out float height))
			current.Y = height + DarkWizardHeightOffset;

		_root.Position = current;
		ApplyFacingInterpolation(delta);
		UpdateMoveTargetMarker(delta);

		Vector2 remaining = new Vector2(
			_moveTarget.X - current.X,
			_moveTarget.Z - current.Z);
		float remainingDistance = remaining.Length();
		bool isMoving = float.IsFinite(remainingDistance) &&
			(remainingDistance > moveEpsilon || _path.Count > 0);
		UpdateFootstepAudio((float)delta, current, isMoving);
		if (isMoving && _specialPose != SpecialPoseMode.None)
			_specialPose = SpecialPoseMode.None;
		SetAnimationState(isMoving);
		UpdateEquipmentAttachmentTransforms();
	}

	private void SetMoveTargetFromTile(Vector2I tile)
	{
		int clampedTileX = Math.Clamp(tile.X, 0, MuConfig.TerrainSize - 1);
		int clampedTileY = Math.Clamp(tile.Y, 0, MuConfig.TerrainSize - 1);
		float targetX = Mathf.Clamp(clampedTileX + 0.5f, 0f, MuConfig.TerrainSize - 1.001f);
		float targetY = Mathf.Clamp(clampedTileY + 0.5f, 0f, MuConfig.TerrainSize - 1.001f);

		float terrainHeightBase;
		if (!TryGetTerrainHeightAtWorld(targetX, -targetY, out terrainHeightBase))
			terrainHeightBase = _root != null && GodotObject.IsInstanceValid(_root)
				? _root.Position.Y - DarkWizardHeightOffset
				: 0f;

		float worldY = terrainHeightBase + DarkWizardHeightOffset;
		_moveTarget = new Vector3(targetX, worldY, -targetY);
	}

	private bool AdvancePath(Vector3 currentPosition)
	{
		const float minDistanceSq = 0.0001f; // 0.01^2
		while (_path.Count > 0)
		{
			var nextTile = _path.Dequeue();
			SetMoveTargetFromTile(nextTile);
			if (!IsFiniteVector3(_moveTarget))
				continue;

			var delta = new Vector2(
				_moveTarget.X - currentPosition.X,
				_moveTarget.Z - currentPosition.Z);
			if (delta.LengthSquared() > minDistanceSq)
				return true;
		}

		_moveTarget = currentPosition;
		return false;
	}

	private void SetFacingTarget(Vector2 moveDir)
	{
		if (moveDir.LengthSquared() <= 0.000001f)
			return;

		_targetYaw = MathF.Atan2(moveDir.X, moveDir.Y) + Mathf.DegToRad(DarkWizardFacingOffsetDegrees);
		_hasTargetYaw = true;
	}

	private void EnterSpecialPose(SpecialPoseMode pose, float facingYaw)
	{
		if (_root == null || !GodotObject.IsInstanceValid(_root))
			return;

		_specialPose = pose;
		_path.Clear();
		_moveTarget = _root.Position;
		_targetYaw = facingYaw;
		_hasTargetYaw = true;
		_moving = false;
		SetAnimationState(isMoving: false, force: true);
		PlayPoseSound(pose);
	}

	private void EnsureAudioPlayers()
	{
		if (_root == null || !GodotObject.IsInstanceValid(_root))
			return;

		_footstepPlayer = _root.GetNodeOrNull<AudioStreamPlayer3D>("FootstepAudio");
		if (_footstepPlayer == null || !GodotObject.IsInstanceValid(_footstepPlayer))
		{
			_footstepPlayer = new AudioStreamPlayer3D
			{
				Name = "FootstepAudio",
				UnitSize = 8f,
				MaxDistance = 80f,
				AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance
			};
			_root.AddChild(_footstepPlayer);
		}
		_footstepPlayer.VolumeDb = FootstepVolumeDb;

		_posePlayer = _root.GetNodeOrNull<AudioStreamPlayer3D>("PoseAudio");
		if (_posePlayer == null || !GodotObject.IsInstanceValid(_posePlayer))
		{
			_posePlayer = new AudioStreamPlayer3D
			{
				Name = "PoseAudio",
				UnitSize = 8f,
				MaxDistance = 80f,
				AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance
			};
			_root.AddChild(_posePlayer);
		}
		_posePlayer.VolumeDb = PoseVolumeDb;

		_walkGrassSound ??= LoadAudioAsset("Sound/pWalk(Grass).wav");
		_walkSnowSound ??= LoadAudioAsset("Sound/pWalk(Snow).wav");
		_walkSoilSound ??= LoadAudioAsset("Sound/pWalk(Soil).wav");
		_swimSound ??= LoadAudioAsset("Sound/pSwim.wav");
		_sitSound ??= LoadAudioAsset(SitSoundPath);
		_restSound ??= LoadAudioAsset(RestSoundPath);
	}

	private void UpdateFootstepAudio(float delta, Vector3 current, bool isMoving)
	{
		if (_footstepPlayer == null || !GodotObject.IsInstanceValid(_footstepPlayer))
			return;

		if (!isMoving)
		{
			_footstepTimer = 0f;
			return;
		}

		int tileX = Math.Clamp((int)MathF.Floor(current.X), 0, MuConfig.TerrainSize - 1);
		int tileY = Math.Clamp((int)MathF.Floor(-current.Z), 0, MuConfig.TerrainSize - 1);
		byte tex = _terrainBuilder.GetBaseTextureIndexAt(tileX, tileY);
		bool isSwimming = tex == 5;

		_footstepTimer += delta;
		float interval = isSwimming ? 2.0f : 0.4f;
		if (_footstepTimer < interval)
			return;

		_footstepTimer = 0f;
		_footstepPlayer.Stream = ResolveFootstepStream(tex, isSwimming);
		if (_footstepPlayer.Stream != null)
			_footstepPlayer.Play();
	}

	private AudioStream? ResolveFootstepStream(byte terrainTexture, bool isSwimming)
	{
		if (isSwimming)
			return _swimSound;

		// Reference client uses special mapping in Devias.
		if (_worldIndex == 2)
		{
			if (terrainTexture == 0 || terrainTexture == 1)
				return _walkSnowSound;
			if (terrainTexture == 4)
				return _walkSoilSound;
			return _walkSoilSound;
		}

		if (terrainTexture == 0 || terrainTexture == 1)
			return _walkGrassSound;
		if (terrainTexture == 4)
			return _walkSnowSound;
		return _walkSoilSound;
	}

	private void PlayPoseSound(SpecialPoseMode pose)
	{
		if (_posePlayer == null || !GodotObject.IsInstanceValid(_posePlayer))
			return;

		AudioStream? sound = pose switch
		{
			SpecialPoseMode.Sit => _sitSound,
			SpecialPoseMode.Rest => _restSound,
			_ => null
		};
		if (sound == null)
			return;

		_posePlayer.Stream = sound;
		_posePlayer.Play();
	}

	private static AudioStream? LoadAudioAsset(string relativePath)
	{
		if (string.IsNullOrWhiteSpace(relativePath))
			return null;

		string normalized = relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);
		string inData = System.IO.Path.Combine(MuConfig.DataPath, normalized);
		if (System.IO.File.Exists(inData))
			return MuAudioLoader.LoadFromFile(inData);

		var parent = System.IO.Directory.GetParent(MuConfig.DataPath);
		if (parent == null)
			return null;

		string fallback = System.IO.Path.Combine(parent.FullName, "Data", normalized);
		return System.IO.File.Exists(fallback)
			? MuAudioLoader.LoadFromFile(fallback)
			: null;
	}

	private void ApplyFacingInterpolation(double delta)
	{
		if (_root == null || !GodotObject.IsInstanceValid(_root) || !_hasTargetYaw)
			return;

		float currentYaw = _root.Rotation.Y;
		float targetYaw = _targetYaw;
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

		var rotation = _root.Rotation;
		rotation.Y = Mathf.Wrap(nextYaw, -Mathf.Pi, Mathf.Pi);
		_root.Rotation = rotation;
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

	private void SetAnimationState(bool isMoving, bool force = false)
	{
		if (!force && _moving == isMoving)
			return;

		_moving = isMoving;
		bool hasWalkControllers = _walkControllers.Count > 0;
		bool useSitPose = !isMoving && _specialPose == SpecialPoseMode.Sit && _sitControllers.Count > 0;
		bool useRestPose = !isMoving && _specialPose == SpecialPoseMode.Rest && _restControllers.Count > 0;
		bool useIdle = !useSitPose && !useRestPose;

		for (int i = 0; i < _idleControllers.Count; i++)
		{
			var controller = _idleControllers[i];
			if (controller == null || !GodotObject.IsInstanceValid(controller))
				continue;

			controller.SetExternalAnimationEnabled(useIdle && (!isMoving || !hasWalkControllers));
		}

		for (int i = 0; i < _walkControllers.Count; i++)
		{
			var controller = _walkControllers[i];
			if (controller == null || !GodotObject.IsInstanceValid(controller))
				continue;

			controller.SetExternalAnimationEnabled(isMoving);
		}

		for (int i = 0; i < _sitControllers.Count; i++)
		{
			var controller = _sitControllers[i];
			if (controller == null || !GodotObject.IsInstanceValid(controller))
				continue;

			controller.SetExternalAnimationEnabled(useSitPose);
		}

		for (int i = 0; i < _restControllers.Count; i++)
		{
			var controller = _restControllers[i];
			if (controller == null || !GodotObject.IsInstanceValid(controller))
				continue;

			controller.SetExternalAnimationEnabled(useRestPose);
		}
	}

	private bool TryGetCurrentAnimationSample(out int actionIndex, out float framePos)
	{
		actionIndex = IdleAction;
		framePos = 0f;

		if (_specialPose == SpecialPoseMode.Sit && TryGetSampleFrom(_sitControllers, out actionIndex, out framePos))
			return true;
		if (_specialPose == SpecialPoseMode.Rest && TryGetSampleFrom(_restControllers, out actionIndex, out framePos))
			return true;
		if (_moving && TryGetSampleFrom(_walkControllers, out actionIndex, out framePos))
			return true;
		return TryGetSampleFrom(_idleControllers, out actionIndex, out framePos);
	}

	private static bool TryGetSampleFrom(List<MuAnimatedMeshController> controllers, out int actionIndex, out float framePos)
	{
		actionIndex = 0;
		framePos = 0f;
		for (int i = 0; i < controllers.Count; i++)
		{
			var controller = controllers[i];
			if (controller == null || !GodotObject.IsInstanceValid(controller))
				continue;

			actionIndex = controller.ActionIndex;
			framePos = controller.FramePosition;
			return true;
		}

		return false;
	}

	private void UpdateEquipmentAttachmentTransforms()
	{
		if (_root == null || !GodotObject.IsInstanceValid(_root) || _skeletonBmd == null)
			return;
		if (!TryGetCurrentAnimationSample(out int actionIndex, out float framePos))
			return;

		UpdateAttachmentTransform(_leftAttachment, actionIndex, framePos);
		UpdateAttachmentTransform(_rightAttachment, actionIndex, framePos);
		UpdateAttachmentTransform(_wingAttachment, actionIndex, framePos);
	}

	private void UpdateAttachmentTransform(EquipmentAttachmentRuntime runtime, int actionIndex, float framePos)
	{
		if (runtime.Pivot == null || !GodotObject.IsInstanceValid(runtime.Pivot) || _skeletonBmd == null)
			return;
		if (!_modelBuilder.TryGetBoneTransform(_skeletonBmd, actionIndex, framePos, runtime.BoneIndex, out var boneTransform))
			return;

		runtime.Pivot.Transform = boneTransform;
		ApplyAttachmentLocalOffset(runtime);
	}

	private void ApplyAttachmentLocalOffset(EquipmentAttachmentRuntime runtime)
	{
		if (runtime.MeshInstance == null || !GodotObject.IsInstanceValid(runtime.MeshInstance))
			return;

		Vector3 offset;
		Vector3 rotDeg;
		if (ReferenceEquals(runtime, _leftAttachment))
		{
			offset = LeftHandItemOffset;
			rotDeg = LeftHandItemRotationDegrees;
		}
		else if (ReferenceEquals(runtime, _rightAttachment))
		{
			offset = RightHandItemOffset;
			rotDeg = RightHandItemRotationDegrees;
		}
		else
		{
			offset = WingItemOffset;
			rotDeg = WingItemRotationDegrees;
		}

		runtime.MeshInstance.Position = offset;
		runtime.MeshInstance.Rotation = new Vector3(
			Mathf.DegToRad(rotDeg.X),
			Mathf.DegToRad(rotDeg.Y),
			Mathf.DegToRad(rotDeg.Z));
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

	private static void ClearChildren(Node node)
	{
		foreach (Node child in node.GetChildren())
			child.QueueFree();
	}

	private async Task EnsureMoveTargetMarkerAsync()
	{
		if (_root == null || !GodotObject.IsInstanceValid(_root) || _root.GetParent() == null)
			return;

		_moveTargetMarker = _root.GetParent().GetNodeOrNull<Node3D>("MoveTargetMarker");
		if (_moveTargetMarker != null && GodotObject.IsInstanceValid(_moveTargetMarker))
			return;

		var marker = new Node3D
		{
			Name = "MoveTargetMarker",
			Visible = false,
			Scale = new Vector3(0.7f, 0.7f, 0.7f)
		};
		_root.GetParent().AddChild(marker);
		_moveTargetMarker = marker;

		const string moveTargetModelPath = "Effect/MoveTargetPosEffect.bmd";
		var bmd = await _modelBuilder.LoadBmdAsync(moveTargetModelPath, logMissing: false);
		if (bmd == null)
		{
			GD.PrintErr($"[DarkWizard] Move target effect missing: {moveTargetModelPath}");
			return;
		}

		var materials = await _modelBuilder.LoadModelTexturesAsync(moveTargetModelPath);
		if (materials.Length > 0)
		{
			_moveTargetBaseAlbedo = new Color[materials.Length];
			for (int i = 0; i < materials.Length; i++)
			{
				var mat = materials[i];
				if (mat == null)
					continue;

				mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
				mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
				_moveTargetBaseAlbedo[i] = mat.AlbedoColor;

				// MonoGame MoveTargetPostEffectObject uses BlendMesh=0 and warm light tint.
				if (i == 0)
				{
					mat.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
					mat.EmissionEnabled = true;
					mat.Emission = new Color(1f, 0.7f, 0.3f);
					mat.EmissionEnergyMultiplier = 1.0f;
				}
			}
			_moveTargetMaterials = materials;
		}

		var mesh = new MeshInstance3D
		{
			Name = "MoveTargetMesh",
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		};
		marker.AddChild(mesh);
		_moveTargetMesh = mesh;

		var anim = new MuAnimatedMeshController
		{
			Name = "MoveTargetAnim"
		};
		marker.AddChild(anim);
		_moveTargetAnimController = anim;
		anim.Initialize(
			_modelBuilder,
			bmd,
			materials,
			actionIndex: 0,
			animationSpeed: 4f,
			subFrameSamples: 8,
			useRealtimeInterpolation: true);
		anim.RegisterInstance(mesh);
		anim.SetExternalAnimationEnabled(true);
	}

	private void ShowMoveTargetMarker(Vector3 position)
	{
		if (_moveTargetMarker == null || !GodotObject.IsInstanceValid(_moveTargetMarker))
			_ = EnsureMoveTargetMarkerAsync();
		if (_moveTargetMarker == null || !GodotObject.IsInstanceValid(_moveTargetMarker))
			return;

		_moveTargetMarker.Position = position + new Vector3(0f, 0.4f, 0f);
		_moveTargetMarker.Visible = true;
		_moveTargetVisibleMs = MoveTargetDurationMs;
		ApplyMoveTargetAlpha(1f);
	}

	private void UpdateMoveTargetMarker(double delta)
	{
		if (_moveTargetMarker == null || !GodotObject.IsInstanceValid(_moveTargetMarker))
			return;

		if (_moveTargetVisibleMs <= 0f)
		{
			_moveTargetMarker.Visible = false;
			return;
		}

		_moveTargetVisibleMs -= (float)(delta * 1000.0);
		float life = Mathf.Clamp(_moveTargetVisibleMs / MoveTargetDurationMs, 0f, 1f);
		ApplyMoveTargetAlpha(life);
		_moveTargetMarker.Visible = life > 0f;
	}

	private void ApplyMoveTargetAlpha(float alpha)
	{
		alpha = Mathf.Clamp(alpha, 0f, 1f);
		if (_moveTargetMaterials.Length == 0)
			return;

		int count = Math.Min(_moveTargetMaterials.Length, _moveTargetBaseAlbedo.Length);
		for (int i = 0; i < count; i++)
		{
			var mat = _moveTargetMaterials[i];
			if (mat == null)
				continue;

			var baseColor = _moveTargetBaseAlbedo[i];
			mat.AlbedoColor = new Color(baseColor.R, baseColor.G, baseColor.B, alpha);
		}
	}
}
