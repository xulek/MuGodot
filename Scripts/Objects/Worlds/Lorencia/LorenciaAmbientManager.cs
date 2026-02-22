using Godot;
using MuGodot;

namespace MuGodot.Objects.Worlds.Lorencia;

[Tool]
public partial class LorenciaAmbientManager : Node3D
{
	[ExportGroup("Lorencia Birds")]
	[Export] public bool EnableLorenciaBirds { get; set; } = true;
	[Export] public bool ShowBirdsInEditor { get; set; } = false;
	[Export] public int LorenciaBirdMaxCount { get; set; } = 20;
	[Export] public bool LorenciaBirdSounds { get; set; } = true;
	[Export] public int EditorBirdMaxCount { get; set; } = 8;
	[Export] public bool EditorDisableBirdSounds { get; set; } = true;

	[ExportGroup("Lorencia Leaves")]
	[Export] public bool EnableLorenciaLeaves { get; set; } = true;
	[Export] public bool ShowLeavesInEditor { get; set; } = false;
	[Export] public int LorenciaLeafMaxParticles { get; set; } = 140;
	[Export] public float LorenciaLeafSpawnRate { get; set; } = 25f;
	[Export] public float LorenciaLeafDensityScale { get; set; } = 1f;
	[Export] public string LorenciaLeafTexturePath { get; set; } = "World1/leaf01.OZT";
	[Export] public int EditorLeafMaxParticles { get; set; } = 64;
	[Export] public float EditorLeafSpawnRateScale { get; set; } = 0.50f;

	private LorenciaLeafParticleSystem? _leafSystem;
	private LorenciaBirdSystem? _birdSystem;

	public void Initialize(MuModelBuilder modelBuilder, MuTerrainBuilder terrainBuilder)
	{
		_leafSystem = new LorenciaLeafParticleSystem(terrainBuilder);
		_birdSystem = new LorenciaBirdSystem(modelBuilder, terrainBuilder);
	}

	public async Task ConfigureForWorldAsync(int worldIndex, Node3D objectsRoot, bool limitEffects)
	{
		if (_leafSystem == null || _birdSystem == null)
			return;

		_leafSystem.Clear();
		_birdSystem.Clear();

		if (worldIndex != 1)
			return;

		bool isEditor = Engine.IsEditorHint();

		bool leavesAllowed = !isEditor || ShowLeavesInEditor;
		if (EnableLorenciaLeaves && leavesAllowed)
		{
			int maxParticles = Math.Clamp(LorenciaLeafMaxParticles, 1, 2000);
			float spawnRate = MathF.Max(0f, LorenciaLeafSpawnRate);
			if (isEditor && limitEffects)
			{
				maxParticles = Math.Min(maxParticles, Math.Clamp(EditorLeafMaxParticles, 1, 2000));
				spawnRate *= Mathf.Clamp(EditorLeafSpawnRateScale, 0.05f, 1f);
			}

			_leafSystem.MaxParticles = maxParticles;
			_leafSystem.SpawnRate = spawnRate;
			_leafSystem.DensityScale = Mathf.Clamp(LorenciaLeafDensityScale, 0.1f, 3f);
			_leafSystem.TexturePath = string.IsNullOrWhiteSpace(LorenciaLeafTexturePath)
				? "World1/leaf01.OZT"
				: LorenciaLeafTexturePath.Trim();
			await _leafSystem.InitializeAsync(objectsRoot);
		}

		bool birdsAllowed = !isEditor || ShowBirdsInEditor;
		if (EnableLorenciaBirds && birdsAllowed)
		{
			int birdMaxCount = Math.Clamp(LorenciaBirdMaxCount, 0, 100);
			bool birdSounds = LorenciaBirdSounds;
			if (isEditor && limitEffects)
			{
				birdMaxCount = Math.Min(birdMaxCount, Math.Clamp(EditorBirdMaxCount, 0, 100));
				if (EditorDisableBirdSounds)
					birdSounds = false;
			}

			_birdSystem.MaxBirds = birdMaxCount;
			_birdSystem.EnableSounds = birdSounds;
			await _birdSystem.InitializeAsync(objectsRoot);
		}
	}

	public void Update(double delta, Vector3 cameraPosition)
	{
		_leafSystem?.Update(delta, cameraPosition, cameraPosition);
		_birdSystem?.Update(delta, cameraPosition);
	}

	public void Clear()
	{
		_leafSystem?.Clear();
		_birdSystem?.Clear();
	}
}
