namespace MuGodot.Networking;

public enum ClientConnectionState
{
    Initial,
    ConnectingToConnectServer,
    ConnectedToConnectServer,
    RequestingServerList,
    ReceivedServerList,
    SelectingServer,
    RequestingConnectionInfo,
    ReceivedConnectionInfo,
    ConnectingToGameServer,
    ConnectedToGameServer,
    Authenticating,
    SelectingCharacter,
    InGame,
    Disconnected,
}
