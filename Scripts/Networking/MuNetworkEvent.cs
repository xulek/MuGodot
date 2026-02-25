using MUnique.OpenMU.Network.Packets.ServerToClient;

namespace MuGodot.Networking;

public enum MuNetworkEventKind
{
    StateChanged,
    ServerListReceived,
    LoginSucceeded,
    LoginFailed,
    CharacterListReceived,
    EnteredGame,
    Error,
}

public sealed class MuNetworkEvent
{
    public MuNetworkEventKind Kind { get; init; }

    public ClientConnectionState State { get; init; } = ClientConnectionState.Initial;

    public string Message { get; init; } = string.Empty;

    public LoginResponse.LoginResult LoginResult { get; init; } = LoginResponse.LoginResult.ConnectionError;

    public IReadOnlyList<MuServerInfo> Servers { get; init; } = Array.Empty<MuServerInfo>();

    public IReadOnlyList<MuCharacterInfo> Characters { get; init; } = Array.Empty<MuCharacterInfo>();

    public static MuNetworkEvent StateChanged(ClientConnectionState state)
        => new() { Kind = MuNetworkEventKind.StateChanged, State = state };

    public static MuNetworkEvent ServerList(IReadOnlyList<MuServerInfo> servers)
        => new() { Kind = MuNetworkEventKind.ServerListReceived, Servers = servers };

    public static MuNetworkEvent LoginOk()
        => new() { Kind = MuNetworkEventKind.LoginSucceeded };

    public static MuNetworkEvent LoginError(LoginResponse.LoginResult result)
        => new() { Kind = MuNetworkEventKind.LoginFailed, LoginResult = result };

    public static MuNetworkEvent CharacterList(IReadOnlyList<MuCharacterInfo> characters)
        => new() { Kind = MuNetworkEventKind.CharacterListReceived, Characters = characters };

    public static MuNetworkEvent EnteredGame()
        => new() { Kind = MuNetworkEventKind.EnteredGame };

    public static MuNetworkEvent Error(string message)
        => new() { Kind = MuNetworkEventKind.Error, Message = message };
}
