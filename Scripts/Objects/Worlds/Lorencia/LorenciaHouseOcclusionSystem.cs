using Godot;

namespace MuGodot.Objects.Worlds.Lorencia;

[Tool]
public partial class LorenciaHouseOcclusionSystem : Node3D
{
	[ExportGroup("Lorencia House Occlusion")]
	[Export] public bool EnableHouseOcclusion { get; set; } = true;
	[Export] public float WallTargetAlpha { get; set; } = 0.30f;
	[Export] public float FadePerFrameAt60Fps { get; set; } = 0.30f;
	[Export] public float WallBehindXDistance { get; set; } = 3.0f;
	[Export] public float WallForwardProximity { get; set; } = 2.5f;

	private readonly List<MeshInstance3D> _fadeWalls = new();
	private readonly List<MeshInstance3D> _pubRoofs = new();
	private readonly Dictionary<MeshInstance3D, float> _alphaByMesh = new();
	private readonly Dictionary<MeshInstance3D, GeometryInstance3D.ShadowCastingSetting> _originalShadowCastingByMesh = new();
	private Func<Vector3>? _getPlayerPosition;
	private bool _resetToOpaquePending;

	public void Initialize(Func<Vector3> getPlayerPosition)
	{
		_getPlayerPosition = getPlayerPosition;
	}

	public void RebuildCaches(Node3D objectsRoot, int worldIndex)
	{
		_fadeWalls.Clear();
		_pubRoofs.Clear();
		_alphaByMesh.Clear();
		_originalShadowCastingByMesh.Clear();
		_resetToOpaquePending = true;

		if (!GodotObject.IsInstanceValid(objectsRoot) || worldIndex != 1)
			return;

		foreach (Node child in objectsRoot.GetChildren())
		{
			if (child is not MeshInstance3D mesh)
				continue;

			if (!TryGetObjectType(mesh, out short type))
				continue;

			if (type is >= 121 and <= 124)
			{
				_fadeWalls.Add(mesh);
				_alphaByMesh[mesh] = 1f;
				_originalShadowCastingByMesh[mesh] = mesh.CastShadow;
			}
			else if (type is 125 or 126)
			{
				_pubRoofs.Add(mesh);
				_alphaByMesh[mesh] = 1f;
				_originalShadowCastingByMesh[mesh] = mesh.CastShadow;
			}
		}
	}

	public void UpdateOcclusion(double delta, int worldIndex, bool enabledInCurrentMode)
	{
		if (!EnableHouseOcclusion || !enabledInCurrentMode || worldIndex != 1 || _getPlayerPosition == null)
		{
			if (_resetToOpaquePending)
				ResetToOpaque();
			return;
		}

		_resetToOpaquePending = true;
		Vector3 playerPos = _getPlayerPosition();
		float t = GetFrameRateAwareLerpFactor(delta);

		for (int i = 0; i < _fadeWalls.Count; i++)
		{
			var mesh = _fadeWalls[i];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			Vector3 wallPos = mesh.GlobalPosition;
			bool behind = playerPos.X < wallPos.X && MathF.Abs(playerPos.X - wallPos.X) < WallBehindXDistance;
			bool withinForward = MathF.Abs(playerPos.Z - wallPos.Z) <= WallForwardProximity;
			float targetAlpha = (behind && withinForward) ? WallTargetAlpha : 1f;
			ApplyAlpha(mesh, targetAlpha, t);
		}

		for (int i = 0; i < _pubRoofs.Count; i++)
		{
			var mesh = _pubRoofs[i];
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			Vector3 roofPos = mesh.GlobalPosition;
			bool underRoof =
				playerPos.X >= roofPos.X - 6f &&
				playerPos.X <= roofPos.X + 4f &&
				playerPos.Z >= roofPos.Z - 6f &&
				playerPos.Z <= roofPos.Z + 6f;

			float targetAlpha = underRoof ? 0f : 1f;
			ApplyAlpha(mesh, targetAlpha, t);
		}
	}

	public void ResetToOpaque()
	{
		foreach (var pair in _alphaByMesh)
		{
			var mesh = pair.Key;
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
				continue;

			mesh.Transparency = 0f;
			RestoreShadowCasting(mesh);
		}

		var keys = new List<MeshInstance3D>(_alphaByMesh.Keys);
		for (int i = 0; i < keys.Count; i++)
			_alphaByMesh[keys[i]] = 1f;

		_resetToOpaquePending = false;
	}

	private void ApplyAlpha(MeshInstance3D mesh, float targetAlpha, float t)
	{
		if (!_alphaByMesh.TryGetValue(mesh, out float currentAlpha))
			currentAlpha = 1f;

		float nextAlpha = Mathf.Lerp(currentAlpha, Mathf.Clamp(targetAlpha, 0f, 1f), t);
		_alphaByMesh[mesh] = nextAlpha;
		mesh.Transparency = Mathf.Clamp(1f - nextAlpha, 0f, 1f);
		UpdateShadowCasting(mesh, nextAlpha);
	}

	private float GetFrameRateAwareLerpFactor(double delta)
	{
		float factorPer60 = Mathf.Clamp(FadePerFrameAt60Fps, 0.001f, 0.999f);
		float frames = MathF.Max(0f, (float)delta * 60f);
		float remaining = MathF.Pow(1f - factorPer60, frames);
		return Mathf.Clamp(1f - remaining, 0f, 1f);
	}

	private static bool TryGetObjectType(MeshInstance3D mesh, out short type)
	{
		type = -1;

		if (mesh.HasMeta("mu_type"))
		{
			Variant value = mesh.GetMeta("mu_type");
			if (value.VariantType == Variant.Type.Int)
			{
				type = (short)(int)value;
				return true;
			}
		}

		string name = mesh.Name.ToString();
		if (!name.StartsWith("Obj_", StringComparison.Ordinal))
			return false;

		int first = name.IndexOf('_');
		int second = name.IndexOf('_', first + 1);
		if (first < 0 || second <= first + 1)
			return false;

		string rawType = name.Substring(first + 1, second - first - 1);
		if (!short.TryParse(rawType, out type))
			return false;

		return true;
	}

	private void UpdateShadowCasting(MeshInstance3D mesh, float alpha)
	{
		if (!_originalShadowCastingByMesh.TryGetValue(mesh, out var original))
			original = GeometryInstance3D.ShadowCastingSetting.On;

		// Keep reference behavior: faded/hidden walls should not block sunlight in the pub.
		if (alpha < 0.98f)
			mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		else
			mesh.CastShadow = original;
	}

	private void RestoreShadowCasting(MeshInstance3D mesh)
	{
		if (_originalShadowCastingByMesh.TryGetValue(mesh, out var original))
			mesh.CastShadow = original;
	}
}
