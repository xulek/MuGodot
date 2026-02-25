using MUnique.OpenMU.Network.Packets;

namespace MuGodot.Networking;

public enum MuProtocolVersion
{
    Season6,
    Version097,
    Version075,
}

public sealed class MuServerInfo
{
    public ushort ServerId { get; set; }

    public byte LoadPercentage { get; set; }
}

public sealed class MuCharacterInfo
{
    public string Name { get; set; } = string.Empty;

    public CharacterClassNumber Class { get; set; } = CharacterClassNumber.DarkWizard;

    public ushort Level { get; set; }

    public byte[] Appearance { get; set; } = [];
}

public sealed class MuNetworkSettings
{
    public string ConnectServerHost { get; set; } = "127.0.0.1";

    public int ConnectServerPort { get; set; } = 44405;

    public MuProtocolVersion ProtocolVersion { get; set; } = MuProtocolVersion.Season6;

    public string ClientVersion { get; set; } = "2.04d";

    public string ClientSerial { get; set; } = "k1Pk2jcET48mxL3b";
}
