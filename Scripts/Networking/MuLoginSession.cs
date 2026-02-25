namespace MuGodot.Networking;

public static class MuLoginSession
{
    public static bool OfflineTestMode { get; set; }

    public static IReadOnlyList<MuCharacterInfo> Characters { get; set; } = Array.Empty<MuCharacterInfo>();

    public static string SelectedCharacterName { get; set; } = string.Empty;

    public static MuCharacterInfo? SelectedCharacter { get; set; }

    public static byte? CurrentMapId { get; set; }

    public static byte CurrentPositionX { get; set; }

    public static byte CurrentPositionY { get; set; }

    public static byte CurrentDirection { get; set; }

    public static void SetLocation(byte mapId, byte x, byte y, byte direction)
    {
        CurrentMapId = mapId;
        CurrentPositionX = x;
        CurrentPositionY = y;
        CurrentDirection = direction;
    }
}
