using Godot;
using Client.Data.ATT;
using Client.Data.BMD;
using Client.Data.CAP;

namespace MuGodot;

[Tool]
public partial class DarkWizardController : Node3D
{
	private const float MonoGameMoveSpeed = 300f * MuConfig.WorldToGodot;
	private const int IdleAction = 1;
	private const int WalkAction = 15;
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

	private MuModelBuilder _modelBuilder = null!;
	private MuTerrainBuilder _terrainBuilder = null!;
	private Camera3D? _camera;
	private string _dataPath = "";
	private Action? _onCameraReset;

	private Node3D? _root;
	private readonly List<MeshInstance3D> _meshes = new();
	private readonly List<MuAnimatedMeshController> _idleControllers = new();
	private readonly List<MuAnimatedMeshController> _walkControllers = new();
	private bool _moving;
	private Vector3 _moveTarget;
	private readonly Queue<Vector2I> _path = new();
	private float _targetYaw;
	private bool _hasTargetYaw;
	private Vector3 _fallbackPosition = new Vector3(128f, 0f, -128f);

	public void Initialize(MuModelBuilder modelBuilder, MuTerrainBuilder terrainBuilder, Camera3D? camera, string dataPath, Action onCameraReset)
	{
		_modelBuilder = modelBuilder;
		_terrainBuilder = terrainBuilder;
		_camera = camera;
		_dataPath = dataPath;
		_onCameraReset = onCameraReset;
	}

	public Vector3 GetPosition()
	{
		if (_root != null && GodotObject.IsInstanceValid(_root))
			return _root.GlobalPosition;
		return _fallbackPosition;
	}

	public void HandleInput(Vector2 mousePosition)
	{
		TrySetMoveTarget(mousePosition);
	}

	public void Update(double delta)
	{
		UpdateMovement(delta);
	}

	public void Reset()
	{
		_root = null;
		_meshes.Clear();
		_idleControllers.Clear();
		_walkControllers.Clear();
		_moving = false;
		_path.Clear();
		_hasTargetYaw = false;
	}

	public async Task SpawnAsync(Node3D charactersRoot, int worldIndex, Action<string>? onStatus = null)
	{
		if (charactersRoot == null || !GodotObject.IsInstanceValid(charactersRoot))
			return;

		ClearChildren(charactersRoot);
		Reset();

		var spawn = await ResolveSpawnPositionAsync(worldIndex);
		_fallbackPosition = spawn;

		var root = new Node3D { Name = "DarkWizard" };
		charactersRoot.AddChild(root);
		_root = root;

		BMD? skeletonBmd = await _modelBuilder.LoadBmdAsync(DarkWizardModelPath);
		if (skeletonBmd == null)
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
			_onCameraReset?.Invoke();
			onStatus?.Invoke("DarkWizard model missing (using fallback).");
			return;
		}

		int idleAction = ClampActionIndex(skeletonBmd, IdleAction);
		int walkAction = ClampActionIndex(skeletonBmd, WalkAction);
		int classId = Math.Max(1, DarkWizardClassModelId);
		int subFrameSamples = Math.Clamp(DarkWizardSubFrameSamples, 1, 32);
		float animationSyncStartSeconds = Time.GetTicksMsec() * 0.001f;
		bool hasRenderablePart = false;

		for (int i = 0; i < BodyPartPrefixes.Length; i++)
		{
			string prefix = BodyPartPrefixes[i];
			string partModelPath = BuildPartModelPath(prefix, classId);
			BMD? partBmd = await _modelBuilder.LoadBmdAsync(partModelPath, logMissing: false);
			if (partBmd == null)
				continue;

			var materials = await _modelBuilder.LoadModelTexturesAsync(partModelPath);
			var meshInstance = new MeshInstance3D { Name = prefix };
			root.AddChild(meshInstance);
			_meshes.Add(meshInstance);

			var idleController = CreateAnimationController(
				root, partBmd, materials, idleAction,
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
					root, partBmd, materials, walkAction,
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

			hasRenderablePart = true;
		}

		if (!hasRenderablePart)
		{
			GD.PrintErr($"[DarkWizard] No class body parts found for class {classId}. Using fallback mesh.");
			var meshInstance = new MeshInstance3D { Name = "FallbackBody" };
			root.AddChild(meshInstance);
			_meshes.Add(meshInstance);
			ApplyFallbackMesh(meshInstance);
			_root.Position = spawn;
			_moveTarget = spawn;
			_path.Clear();
			_targetYaw = _root.Rotation.Y;
			_hasTargetYaw = true;
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
		_onCameraReset?.Invoke();
		GD.Print($"[DarkWizard] Spawned at {spawn}. Parts: {_meshes.Count}, walk controllers: {_walkControllers.Count}");
	}

	private MuAnimatedMeshController CreateAnimationController(
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
		if (!IsTileWalkable(tileX, tileY))
			return;

		int startX = Math.Clamp((int)MathF.Floor(_root.Position.X), 0, MuConfig.TerrainSize - 1);
		int startY = Math.Clamp((int)MathF.Floor(-_root.Position.Z), 0, MuConfig.TerrainSize - 1);
		var startTile = new Vector2I(startX, startY);
		var targetTile = new Vector2I(tileX, tileY);

		var path = FindPath(startTile, targetTile);
		if (path.Count == 0)
			return;

		_path.Clear();
		for (int i = 0; i < path.Count; i++)
			_path.Enqueue(path[i]);

		if (!AdvancePath(_root.Position))
			SetAnimationState(isMoving: false, force: true);
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

		Vector2 remaining = new Vector2(
			_moveTarget.X - current.X,
			_moveTarget.Z - current.Z);
		float remainingDistance = remaining.Length();
		bool isMoving = float.IsFinite(remainingDistance) &&
			(remainingDistance > moveEpsilon || _path.Count > 0);
		SetAnimationState(isMoving);
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

		for (int i = 0; i < _idleControllers.Count; i++)
		{
			var controller = _idleControllers[i];
			if (controller == null || !GodotObject.IsInstanceValid(controller))
				continue;

			controller.SetExternalAnimationEnabled(!isMoving || !hasWalkControllers);
		}

		for (int i = 0; i < _walkControllers.Count; i++)
		{
			var controller = _walkControllers[i];
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

	private static void ClearChildren(Node node)
	{
		foreach (Node child in node.GetChildren())
			child.QueueFree();
	}
}
