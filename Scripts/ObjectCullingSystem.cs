using Godot;

namespace MuGodot;

[Tool]
public partial class ObjectCullingSystem : Node3D
{
	private readonly record struct ObjectCullBounds(MeshInstance3D Mesh, Vector3 LocalCenter, float LocalRadius);

	[ExportGroup("Fog & Culling")]
	[Export] public bool EnableDistanceFogAndObjectCulling { get; set; } = true;
	[Export] public float FogAndCullingDistance { get; set; } = 52f;
	[Export] public float FogStartDistance { get; set; } = 44f;
	[Export] public float FogOpaqueBeforeCullMargin { get; set; } = 1.5f;
	[Export] public float FogDensityCurve { get; set; } = 3.2f;
	[Export] public float FogMaxDensity { get; set; } = 0.88f;
	[Export] public float ObjectCullingHysteresis { get; set; } = 2f;
	[Export] public float ObjectCullingRefreshInterval { get; set; } = 0.10f;
	[Export] public bool UseFrustumObjectCulling { get; set; } = true;
	[Export] public float FrustumCullingMargin { get; set; } = 8f;
	[Export] public int ObjectCullingBatchSize { get; set; } = 512;
	[Export] public int AnimationCullingBatchSize { get; set; } = 128;
	[Export] public Color DistanceFogColor { get; set; } = new Color(0.58f, 0.64f, 0.72f, 1f);

	private Camera3D? _camera;
	private WorldEnvironment? _worldEnvironment;
	private Godot.Environment? _sceneEnvironment;
	private readonly List<MeshInstance3D> _objectInstances = new();
	private readonly List<ObjectCullBounds> _objectBounds = new();
	private readonly List<MuAnimatedMeshController> _animationControllers = new();
	private float _cullingTimer;
	private int _objectCursor;
	private int _animationCursor;
	private bool _resetPending = true;

	public void Initialize(Camera3D camera)
	{
		_camera = camera;
		EnsureEnvironment();
	}

	public void RebuildCaches(Node3D objectsRoot)
	{
		_objectInstances.Clear();
		_objectBounds.Clear();
		_animationControllers.Clear();
		_objectCursor = 0;
		_animationCursor = 0;

		if (!GodotObject.IsInstanceValid(objectsRoot))
			return;

		foreach (Node child in objectsRoot.GetChildren())
		{
			if (child is MeshInstance3D mesh &&
				mesh.Name.ToString().StartsWith("Obj_", StringComparison.Ordinal))
			{
				_objectInstances.Add(mesh);
				_objectBounds.Add(BuildBounds(mesh));
				continue;
			}

			if (child is MuAnimatedMeshController controller)
				_animationControllers.Add(controller);
		}
	}

	public void Update(double delta, Vector3 cameraPosition)
	{
		ConfigureFog();

		if (!EnableDistanceFogAndObjectCulling)
		{
			if (_resetPending)
				ResetAll();
			return;
		}

		_resetPending = true;
		_cullingTimer += (float)delta;
		float refresh = Mathf.Clamp(ObjectCullingRefreshInterval, 0.05f, 1.0f);
		if (_cullingTimer < refresh)
			return;

		_cullingTimer = 0f;
		float maxDistance = MathF.Max(10f, FogAndCullingDistance);
		float maxDistanceSq = maxDistance * maxDistance;
		float hysteresis = MathF.Max(0f, ObjectCullingHysteresis);
		bool useFrustum = UseFrustumObjectCulling && _camera != null && GodotObject.IsInstanceValid(_camera);
		float showMargin = MathF.Max(0f, FrustumCullingMargin);
		float hideMargin = showMargin + hysteresis;
		Godot.Collections.Array<Plane>? frustumPlanes = useFrustum ? _camera!.GetFrustum() : null;
		float[]? planeSigns = null;
		if (useFrustum && frustumPlanes != null && frustumPlanes.Count > 0)
		{
			Vector3 insidePoint = GetFrustumInsideSamplePoint(_camera!);
			planeSigns = BuildFrustumPlaneSigns(frustumPlanes, insidePoint);
		}

		if (useFrustum && frustumPlanes != null && frustumPlanes.Count > 0 && planeSigns != null)
			ProcessFrustumBatch(frustumPlanes, planeSigns, showMargin, hideMargin);
		else
		{
			float showDistanceSq = MathF.Max(1f, maxDistance - hysteresis);
			showDistanceSq *= showDistanceSq;
			ProcessDistanceBatch(cameraPosition, maxDistanceSq, showDistanceSq);
		}

		ProcessAnimationBatch(cameraPosition, maxDistanceSq, useFrustum);
	}

	public void ResetAll()
	{
		for (int i = 0; i < _objectInstances.Count; i++)
		{
			var mesh = _objectInstances[i];
			if (mesh != null && GodotObject.IsInstanceValid(mesh) && !mesh.Visible)
				mesh.Visible = true;
		}

		for (int i = 0; i < _animationControllers.Count; i++)
		{
			var ctrl = _animationControllers[i];
			if (ctrl != null && GodotObject.IsInstanceValid(ctrl))
				ctrl.SetExternalAnimationEnabled(true);
		}

		_objectCursor = 0;
		_animationCursor = 0;
		_resetPending = false;
	}

	private void EnsureEnvironment()
	{
		if (_worldEnvironment != null && GodotObject.IsInstanceValid(_worldEnvironment) &&
			_sceneEnvironment != null)
			return;

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
	}

	private void ConfigureFog()
	{
		EnsureEnvironment();
		if (_sceneEnvironment == null)
			return;

		bool enabled = EnableDistanceFogAndObjectCulling;
		_sceneEnvironment.FogEnabled = enabled;
		_sceneEnvironment.VolumetricFogEnabled = false;
		if (!enabled)
			return;

		float cullDistance = MathF.Max(10f, FogAndCullingDistance);
		float fogStart = Mathf.Clamp(FogStartDistance, 1f, cullDistance - 1f);
		float fogEnd = MathF.Max(fogStart + 1f, cullDistance - MathF.Max(0.5f, FogOpaqueBeforeCullMargin));

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

	private void ProcessFrustumBatch(
		Godot.Collections.Array<Plane> planes,
		float[] planeSigns,
		float showMargin,
		float hideMargin)
	{
		int total = _objectBounds.Count;
		if (total <= 0)
			return;

		int batch = ResolveBatchSize(total, ObjectCullingBatchSize);
		if (_objectCursor >= total) _objectCursor = 0;

		for (int n = 0; n < batch; n++)
		{
			int idx = (_objectCursor + n) % total;
			var bounds = _objectBounds[idx];
			var mesh = bounds.Mesh;
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			var transform = mesh.GlobalTransform;
			Vector3 worldCenter = transform * bounds.LocalCenter;
			float worldScale = GetMaxScale(transform);
			float margin = mesh.Visible ? hideMargin : showMargin;
			float worldRadius = bounds.LocalRadius * worldScale + margin;
			bool visible = IsSphereInFrustum(planes, planeSigns, worldCenter, worldRadius);
			if (mesh.Visible != visible)
				mesh.Visible = visible;
		}

		_objectCursor = (_objectCursor + batch) % total;
	}

	private void ProcessDistanceBatch(Vector3 cameraPosition, float maxDistanceSq, float showDistanceSq)
	{
		int total = _objectInstances.Count;
		if (total <= 0)
			return;

		int batch = ResolveBatchSize(total, ObjectCullingBatchSize);
		if (_objectCursor >= total) _objectCursor = 0;

		for (int n = 0; n < batch; n++)
		{
			int idx = (_objectCursor + n) % total;
			var mesh = _objectInstances[idx];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			float distSq = mesh.GlobalPosition.DistanceSquaredTo(cameraPosition);
			bool visible = mesh.Visible ? distSq <= maxDistanceSq : distSq <= showDistanceSq;
			if (mesh.Visible != visible)
				mesh.Visible = visible;
		}

		_objectCursor = (_objectCursor + batch) % total;
	}

	private void ProcessAnimationBatch(Vector3 cameraPosition, float maxDistanceSq, bool useFrustum)
	{
		int total = _animationControllers.Count;
		if (total <= 0)
			return;

		int batch = ResolveBatchSize(total, AnimationCullingBatchSize);
		if (_animationCursor >= total) _animationCursor = 0;

		for (int n = 0; n < batch; n++)
		{
			int idx = (_animationCursor + n) % total;
			var ctrl = _animationControllers[idx];
			if (ctrl == null || !GodotObject.IsInstanceValid(ctrl))
				continue;

			bool animate = useFrustum
				? ctrl.HasAnyVisibleTarget()
				: ctrl.HasAnyVisibleTargetWithinDistance(cameraPosition, maxDistanceSq);
			ctrl.SetExternalAnimationEnabled(animate);
		}

		_animationCursor = (_animationCursor + batch) % total;
	}

	private static int ResolveBatchSize(int total, int requested) =>
		total <= 0 ? 0 : Math.Clamp(requested, 1, total);

	private static ObjectCullBounds BuildBounds(MeshInstance3D mesh)
	{
		if (mesh.Mesh == null)
			return new ObjectCullBounds(mesh, Vector3.Zero, 0.75f);

		var aabb = mesh.Mesh.GetAabb();
		Vector3 localCenter = aabb.Position + (aabb.Size * 0.5f);
		float localRadius = MathF.Max(0.5f, aabb.Size.Length() * 0.5f);
		return new ObjectCullBounds(mesh, localCenter, localRadius);
	}

	private static float GetMaxScale(Transform3D transform)
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

	private static float[] BuildFrustumPlaneSigns(Godot.Collections.Array<Plane> planes, Vector3 insidePoint)
	{
		int count = planes.Count;
		var signs = new float[count];
		for (int i = 0; i < count; i++)
			signs[i] = planes[i].DistanceTo(insidePoint) >= 0f ? 1f : -1f;
		return signs;
	}

	private static bool IsSphereInFrustum(
		Godot.Collections.Array<Plane> planes,
		float[] planeSigns,
		Vector3 center,
		float radius)
	{
		for (int i = 0; i < planes.Count; i++)
		{
			if (planes[i].DistanceTo(center) * planeSigns[i] < -radius)
				return false;
		}
		return true;
	}
}
