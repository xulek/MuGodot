using Godot;
using MuGodot;

namespace MuGodot.Audio;

[Tool]
public partial class WorldAudioManager : Node3D
{
	[ExportGroup("Audio")]
	[Export] public bool EnableWorldAudio { get; set; } = true;
	[Export] public bool PlayAudioInEditor { get; set; } = false;
	[Export] public float MusicVolumeDb { get; set; } = -7.0f;
	[Export] public float AmbientVolumeDb { get; set; } = -12.0f;

	private AudioStreamPlayer? _musicPlayer;
	private AudioStreamPlayer? _ambientPlayer;
	private readonly Dictionary<string, AudioStream?> _audioCache = new(StringComparer.OrdinalIgnoreCase);
	private AudioStream? _lorenciaThemeMusic;
	private AudioStream? _lorenciaPubMusic;
	private AudioStream? _lorenciaAmbientWind;
	private bool _isInLorenciaPubArea;

	private string _dataPath = "";
	private MuTerrainBuilder? _terrainBuilder;
	private Func<Vector3>? _getPlayerPosition;

	public void Initialize(string dataPath, MuTerrainBuilder terrainBuilder, Func<Vector3> getPlayerPosition)
	{
		_dataPath = dataPath;
		_terrainBuilder = terrainBuilder;
		_getPlayerPosition = getPlayerPosition;
		SetupPlayers();
	}

	public void ConfigureForWorld(int worldIndex)
	{
		_isInLorenciaPubArea = false;

		if (!EnableWorldAudio || !IsAudioAllowed())
			return;

		if (worldIndex != 1)
			return;

		_lorenciaThemeMusic ??= LoadAsset("Music/MuTheme.mp3");
		_lorenciaPubMusic ??= LoadAsset("Music/Pub.mp3");
		_lorenciaAmbientWind ??= LoadAsset("Sound/aWind.wav");

		PlayMusic(_lorenciaThemeMusic);
		PlayAmbient(_lorenciaAmbientWind);
	}

	public void Update(int worldIndex)
	{
		if (!EnableWorldAudio || !IsAudioAllowed())
			return;

		if (worldIndex == 1)
			UpdatePubMusicState();

		EnsurePlaying(_musicPlayer);
		EnsurePlaying(_ambientPlayer);
	}

	public void Stop()
	{
		_musicPlayer?.Stop();
		_ambientPlayer?.Stop();
		_isInLorenciaPubArea = false;
	}

	private void SetupPlayers()
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

	private void UpdatePubMusicState()
	{
		if (_musicPlayer == null || _terrainBuilder == null || _getPlayerPosition == null)
			return;

		Vector3 playerPos = _getPlayerPosition();
		int tileX = Math.Clamp((int)MathF.Floor(playerPos.X), 0, MuConfig.TerrainSize - 1);
		int tileY = Math.Clamp((int)MathF.Floor(-playerPos.Z), 0, MuConfig.TerrainSize - 1);
		bool isInPubArea = _terrainBuilder.GetLayer1TextureIndexAt(tileX, tileY) == 4;

		if (isInPubArea == _isInLorenciaPubArea)
			return;

		_isInLorenciaPubArea = isInPubArea;
		PlayMusic(_isInLorenciaPubArea ? _lorenciaPubMusic : _lorenciaThemeMusic);
	}

	private AudioStream? LoadAsset(string relativePath)
	{
		var resolvedPath = ResolvePath(relativePath);
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

	private string? ResolvePath(string relativePath)
	{
		if (string.IsNullOrWhiteSpace(relativePath))
			return null;

		var normalized = relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);
		var candidate = System.IO.Path.Combine(_dataPath, normalized);
		if (System.IO.File.Exists(candidate))
			return candidate;

		var dataParent = System.IO.Directory.GetParent(_dataPath);
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

	private static void EnsurePlaying(AudioStreamPlayer? player)
	{
		if (player == null || player.Stream == null || player.Playing)
			return;

		player.Play();
	}

	private bool IsAudioAllowed() => !Engine.IsEditorHint() || PlayAudioInEditor;
}
