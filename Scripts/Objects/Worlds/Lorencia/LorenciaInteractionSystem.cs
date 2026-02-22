using Godot;

namespace MuGodot.Objects.Worlds.Lorencia;

public sealed class LorenciaInteractionSystem
{
	private enum CursorVisual
	{
		Normal,
		Push,
		LeanAgainst,
		SitDown
	}

	private enum InteractionKind
	{
		None,
		Rest,
		Sit
	}

	private sealed class InteractiveEntry
	{
		public required Node3D Node { get; init; }
		public required short Type { get; init; }
		public required Vector2I Tile { get; init; }
		public required float FacingYaw { get; init; }
		public required Vector3 BoundsCenter { get; init; }
		public required Vector3 BoundsExtents { get; init; }
	}

	private readonly List<InteractiveEntry> _entries = new();
	private readonly Dictionary<CursorVisual, Texture2D?> _cursorTextures = new();
	private Camera3D? _camera;
	private DarkWizardController? _wizard;
	private CursorVisual _currentCursor = CursorVisual.Normal;
	private InteractiveEntry? _hovered;
	private InteractionKind _pendingKind = InteractionKind.None;
	private Vector2I _pendingTile;
	private float _pendingYaw;
	private bool _cursorLoaded;

	public async Task InitializeAsync(Camera3D? camera, DarkWizardController wizard)
	{
		_camera = camera;
		_wizard = wizard;
		if (_cursorLoaded)
			return;

		_cursorLoaded = true;
		_cursorTextures[CursorVisual.Normal] = await LoadCursorTextureAsync("Cursor");
		_cursorTextures[CursorVisual.Push] = await LoadCursorTextureAsync("CursorPush");
		_cursorTextures[CursorVisual.LeanAgainst] = await LoadCursorTextureAsync("CursorLeanAgainst");
		_cursorTextures[CursorVisual.SitDown] = await LoadCursorTextureAsync("CursorSitDown");
		ApplyCursor(CursorVisual.Normal);
	}

	public void RebuildCaches(Node3D objectsRoot, int worldIndex)
	{
		_entries.Clear();
		_hovered = null;
		_pendingKind = InteractionKind.None;

		if (worldIndex != 1 || objectsRoot == null || !GodotObject.IsInstanceValid(objectsRoot))
			return;

		foreach (Node child in objectsRoot.GetChildren())
		{
			if (child is not Node3D node || !GodotObject.IsInstanceValid(node))
				continue;

			if (!TryGetObjectType(node, out short type))
				continue;

			if (type is not 133 and not 145 and not 146)
				continue;

			Vector3 center;
			Vector3 extents;
			if (node is MeshInstance3D mesh)
			{
				if (!TryBuildMeshWorldBounds(mesh, out center, out extents))
					continue;
			}
			else
			{
				center = node.GlobalPosition + new Vector3(0f, 1f, 0f);
				extents = new Vector3(0.25f, 1f, 0.25f);
			}

			Vector3 p = node.GlobalPosition;
			var tile = new Vector2I(
				Math.Clamp((int)MathF.Floor(p.X), 0, MuConfig.TerrainSize - 1),
				Math.Clamp((int)MathF.Floor(-p.Z), 0, MuConfig.TerrainSize - 1));

			_entries.Add(new InteractiveEntry
			{
				Node = node,
				Type = type,
				Tile = tile,
				FacingYaw = node.GlobalRotation.Y,
				BoundsCenter = center,
				BoundsExtents = extents
			});
		}
	}

	public bool TryHandleLeftClick(Vector2 mousePosition)
	{
		if (_wizard == null)
			return false;

		_hovered = PickHovered(mousePosition);
		if (_hovered == null)
			return false;

		if (!_wizard.TryMoveToTile(_hovered.Tile, showMarker: true))
			return false;

		_pendingKind = _hovered.Type == 133 ? InteractionKind.Rest : InteractionKind.Sit;
		_pendingTile = _hovered.Tile;
		_pendingYaw = _hovered.FacingYaw;
		return true;
	}

	public void Update(double delta, Vector2 mousePosition, bool leftPressed)
	{
		_ = delta;
		_hovered = PickHovered(mousePosition);

		if (_pendingKind != InteractionKind.None && _wizard != null)
		{
			if (_wizard.IsNearTile(_pendingTile, 0.14f) && !_wizard.IsMoving())
			{
				if (_pendingKind == InteractionKind.Sit)
					_wizard.EnterSitPose(_pendingYaw);
				else
					_wizard.EnterRestPose(_pendingYaw);

				_pendingKind = InteractionKind.None;
			}
			else if (!_wizard.IsMoving())
			{
				Vector2I tile = _wizard.GetCurrentTile();
				int dx = Math.Abs(tile.X - _pendingTile.X);
				int dy = Math.Abs(tile.Y - _pendingTile.Y);
				if (dx > 1 || dy > 1)
					_pendingKind = InteractionKind.None;
			}
		}

		if (leftPressed)
		{
			ApplyCursor(CursorVisual.Push);
			return;
		}

		if (_hovered == null)
		{
			ApplyCursor(CursorVisual.Normal);
			return;
		}

		ApplyCursor(_hovered.Type == 133 ? CursorVisual.LeanAgainst : CursorVisual.SitDown);
	}

	private InteractiveEntry? PickHovered(Vector2 mousePosition)
	{
		if (_camera == null || !GodotObject.IsInstanceValid(_camera) || _entries.Count == 0)
			return null;

		Vector3 rayOrigin = _camera.ProjectRayOrigin(mousePosition);
		Vector3 rayDir = _camera.ProjectRayNormal(mousePosition);
		if (rayDir.LengthSquared() < 0.0001f)
			return null;
		rayDir = rayDir.Normalized();

		float best = float.PositiveInfinity;
		InteractiveEntry? bestEntry = null;

		for (int i = 0; i < _entries.Count; i++)
		{
			var e = _entries[i];
			if (e.Node == null || !GodotObject.IsInstanceValid(e.Node))
				continue;

			Vector3 min = e.BoundsCenter - e.BoundsExtents;
			Vector3 max = e.BoundsCenter + e.BoundsExtents;
			if (!RayIntersectsAabb(rayOrigin, rayDir, min, max, out float t))
				continue;

			if (t < best)
			{
				best = t;
				bestEntry = e;
			}
		}

		return bestEntry;
	}

	private void ApplyCursor(CursorVisual visual)
	{
		if (_currentCursor == visual)
			return;

		_currentCursor = visual;
		if (!_cursorTextures.TryGetValue(visual, out Texture2D? texture))
			texture = null;

		Input.SetCustomMouseCursor(texture, Input.CursorShape.Arrow, Vector2.Zero);
	}

	private static bool TryGetObjectType(Node3D node, out short type)
	{
		type = 0;
		if (!node.HasMeta("mu_type"))
			return false;

		Variant meta = node.GetMeta("mu_type");
		if (meta.VariantType == Variant.Type.Int)
		{
			type = (short)(int)meta;
			return true;
		}
		return false;
	}

	private static bool TryBuildMeshWorldBounds(MeshInstance3D mesh, out Vector3 center, out Vector3 extents)
	{
		center = mesh.GlobalPosition;
		extents = new Vector3(0.25f, 1f, 0.25f);
		if (mesh.Mesh == null)
			return false;

		Aabb local = mesh.GetAabb();
		if (local.Size.LengthSquared() <= 0.000001f)
			return true;

		Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
		Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

		Vector3 p = local.Position;
		Vector3 s = local.Size;
		Span<Vector3> corners = stackalloc Vector3[8]
		{
			new Vector3(p.X, p.Y, p.Z),
			new Vector3(p.X + s.X, p.Y, p.Z),
			new Vector3(p.X, p.Y + s.Y, p.Z),
			new Vector3(p.X, p.Y, p.Z + s.Z),
			new Vector3(p.X + s.X, p.Y + s.Y, p.Z),
			new Vector3(p.X + s.X, p.Y, p.Z + s.Z),
			new Vector3(p.X, p.Y + s.Y, p.Z + s.Z),
			new Vector3(p.X + s.X, p.Y + s.Y, p.Z + s.Z)
		};

		for (int i = 0; i < corners.Length; i++)
		{
			Vector3 w = mesh.GlobalTransform * corners[i];
			min = min.Min(w);
			max = max.Max(w);
		}

		center = (min + max) * 0.5f;
		extents = (max - min) * 0.5f;
		extents = new Vector3(Mathf.Max(0.08f, extents.X), Mathf.Max(0.25f, extents.Y), Mathf.Max(0.08f, extents.Z));
		return true;
	}

	private static bool RayIntersectsAabb(Vector3 origin, Vector3 dir, Vector3 min, Vector3 max, out float t)
	{
		t = 0f;
		float tMin = 0f;
		float tMax = float.PositiveInfinity;

		for (int axis = 0; axis < 3; axis++)
		{
			float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
			float d = axis == 0 ? dir.X : axis == 1 ? dir.Y : dir.Z;
			float mn = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
			float mx = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;

			if (MathF.Abs(d) < 1e-6f)
			{
				if (o < mn || o > mx)
					return false;
				continue;
			}

			float inv = 1f / d;
			float t1 = (mn - o) * inv;
			float t2 = (mx - o) * inv;
			if (t1 > t2)
				(t1, t2) = (t2, t1);

			tMin = MathF.Max(tMin, t1);
			tMax = MathF.Min(tMax, t2);
			if (tMin > tMax)
				return false;
		}

		t = tMin;
		return true;
	}

	private static async Task<Texture2D?> LoadCursorTextureAsync(string baseName)
	{
		string interfaceDir = System.IO.Path.Combine(MuConfig.DataPath, "Interface");
		if (!System.IO.Directory.Exists(interfaceDir))
		{
			interfaceDir = FindDirectoryInsensitive(MuConfig.DataPath, "Interface") ?? interfaceDir;
		}

		if (!System.IO.Directory.Exists(interfaceDir))
			return null;

		string[] exts = [".ozt", ".ozj", ".ozp", ".tga", ".jpg", ".png"];
		for (int i = 0; i < exts.Length; i++)
		{
			string candidate = System.IO.Path.Combine(interfaceDir, baseName + exts[i]);
			if (!System.IO.File.Exists(candidate))
				candidate = FindFileInsensitive(interfaceDir, baseName + exts[i]) ?? candidate;

			if (!System.IO.File.Exists(candidate))
				continue;

			var tex = await MuTextureHelper.LoadTextureAsync(candidate, generateMipmaps: false);
			if (tex != null)
				return tex;
		}

		return null;
	}

	private static string? FindDirectoryInsensitive(string parentDir, string dirName)
	{
		if (!System.IO.Directory.Exists(parentDir))
			return null;

		return System.IO.Directory.GetDirectories(parentDir)
			.FirstOrDefault(d => string.Equals(System.IO.Path.GetFileName(d), dirName, StringComparison.OrdinalIgnoreCase));
	}

	private static string? FindFileInsensitive(string dir, string fileName)
	{
		if (!System.IO.Directory.Exists(dir))
			return null;

		return System.IO.Directory.GetFiles(dir)
			.FirstOrDefault(f => string.Equals(System.IO.Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
	}
}
