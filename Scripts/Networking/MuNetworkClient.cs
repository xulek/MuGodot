using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Godot;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets;
using MUnique.OpenMU.Network.Packets.ConnectServer;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Network.SimpleModulus;
using MUnique.OpenMU.Network.Xor;

namespace MuGodot.Networking;

public sealed class MuNetworkClient : IAsyncDisposable
{
    private const byte NoSubCode = 0xFF;
    private const int ClientVersionLength = 5;
    private const int ClientSerialLength = 16;

    private static readonly SimpleModulusKeys EncryptKeys = PipelinedSimpleModulusEncryptor.DefaultClientKey;
    private static readonly SimpleModulusKeys DecryptKeys = PipelinedSimpleModulusDecryptor.DefaultClientKey;
    private static readonly byte[] Xor3Keys = DefaultKeys.Xor3Keys;

    private static readonly Lazy<MuNetworkClient> LazyInstance = new(() => new MuNetworkClient());

    private readonly ConcurrentQueue<MuNetworkEvent> _events = new();
    private readonly ConcurrentQueue<MuWorldEvent> _worldEvents = new();
    private readonly MuConnectionManager _connectionManager = new(EncryptKeys, DecryptKeys);

    private readonly object _stateSync = new();

    private MuNetworkSettings _settings = new();
    private byte[] _clientVersionBytes = new byte[ClientVersionLength];
    private byte[] _clientSerialBytes = new byte[ClientSerialLength];

    private ClientConnectionState _state = ClientConnectionState.Initial;
    private bool _connectServerRouting = true;

    private List<MuServerInfo> _serverList = [];
    private List<MuCharacterInfo> _characters = [];

    private ushort _selfObjectId;
    private string _selectedCharacterName = string.Empty;
    private uint _currentHealth;
    private uint _maximumHealth;
    private uint _currentShield;
    private uint _maximumShield;
    private uint _currentMana;
    private uint _maximumMana;
    private uint _currentAbility;
    private uint _maximumAbility;
    private byte _currentMapId;

    private CancellationTokenSource _managerCts = new();

    private string _currentHost = string.Empty;
    private int _currentPort;

    public static MuNetworkClient Instance => LazyInstance.Value;

    public ClientConnectionState CurrentState
    {
        get
        {
            lock (_stateSync)
            {
                return _state;
            }
        }
    }

    public IReadOnlyList<MuServerInfo> GetCachedServerList()
    {
        lock (_stateSync)
        {
            return new ReadOnlyCollection<MuServerInfo>(_serverList.Select(s => new MuServerInfo
            {
                ServerId = s.ServerId,
                LoadPercentage = s.LoadPercentage,
            }).ToList());
        }
    }

    public IReadOnlyList<MuCharacterInfo> GetCachedCharacterList()
    {
        lock (_stateSync)
        {
            return new ReadOnlyCollection<MuCharacterInfo>(_characters.Select(c => new MuCharacterInfo
            {
                Name = c.Name,
                Class = c.Class,
                Level = c.Level,
                Appearance = [.. c.Appearance],
            }).ToList());
        }
    }

    public void Configure(MuNetworkSettings settings)
    {
        _settings = settings;
        _clientVersionBytes = BuildClientVersionBytes(_settings.ClientVersion);
        _clientSerialBytes = BuildClientSerialBytes(_settings.ClientSerial);
    }

    public bool TryDequeueEvent(out MuNetworkEvent networkEvent) => _events.TryDequeue(out networkEvent!);

    public bool TryDequeueWorldEvent(out MuWorldEvent worldEvent) => _worldEvents.TryDequeue(out worldEvent!);

    public async Task ConnectToConnectServerAsync()
    {
        if (_connectionManager.IsConnected)
        {
            EnqueueError("Connection is already active.");
            return;
        }

        UpdateState(ClientConnectionState.ConnectingToConnectServer);
        _connectServerRouting = true;

        bool connected = await _connectionManager.ConnectAsync(
            _settings.ConnectServerHost,
            _settings.ConnectServerPort,
            useEncryption: false,
            _managerCts.Token);

        if (!connected)
        {
            EnqueueError($"Unable to connect to Connect Server {_settings.ConnectServerHost}:{_settings.ConnectServerPort}.");
            UpdateState(ClientConnectionState.Disconnected);
            return;
        }

        _currentHost = _settings.ConnectServerHost;
        _currentPort = _settings.ConnectServerPort;

        AttachConnectionHandlers();
        _connectionManager.StartReceiving(_managerCts.Token);
    }

    public async Task RequestGameServerConnectionAsync(ushort serverId)
    {
        if (CurrentState >= ClientConnectionState.RequestingConnectionInfo
            && CurrentState < ClientConnectionState.InGame)
        {
            return;
        }

        if (CurrentState != ClientConnectionState.ReceivedServerList)
        {
            EnqueueError($"Cannot select server in state {CurrentState}.");
            return;
        }

        if (!_connectionManager.IsConnected)
        {
            EnqueueError("No connection to Connect Server.");
            return;
        }

        UpdateState(ClientConnectionState.RequestingConnectionInfo);

        try
        {
            await _connectionManager.Connection.SendConnectionInfoRequestAsync(serverId);
        }
        catch (Exception ex)
        {
            EnqueueError($"Error sending game server connection request: {ex.Message}");
        }
    }

    public async Task SendLoginRequestAsync(string username, string password)
    {
        if (CurrentState != ClientConnectionState.ConnectedToGameServer
            && CurrentState != ClientConnectionState.Authenticating)
        {
            EnqueueError($"Cannot log in in state {CurrentState}.");
            return;
        }

        if (!_connectionManager.IsConnected)
        {
            EnqueueError("No connection to Game Server.");
            return;
        }

        UpdateState(ClientConnectionState.Authenticating);

        try
        {
            await _connectionManager.Connection.SendAsync(() => MuPacketBuilder.BuildLoginPacket(
                _connectionManager.Connection.Output,
                username,
                password,
                _clientVersionBytes,
                _clientSerialBytes,
                Xor3Keys));
        }
        catch (Exception ex)
        {
            EnqueueError($"Error sending login request: {ex.Message}");
            UpdateState(ClientConnectionState.ConnectedToGameServer);
        }
    }

    public async Task SendSelectCharacterRequestAsync(string characterName)
    {
        if (CurrentState != ClientConnectionState.ConnectedToGameServer)
        {
            EnqueueError($"Cannot select character in state {CurrentState}.");
            return;
        }

        if (!_connectionManager.IsConnected)
        {
            EnqueueError("No connection to Game Server.");
            return;
        }

        _selectedCharacterName = characterName;
        _selfObjectId = 0;
        UpdateState(ClientConnectionState.SelectingCharacter);

        try
        {
            await _connectionManager.Connection.SendAsync(() =>
                MuPacketBuilder.BuildSelectCharacterPacket(_connectionManager.Connection.Output, characterName));
        }
        catch (Exception ex)
        {
            EnqueueError($"Error sending character selection request: {ex.Message}");
            UpdateState(ClientConnectionState.ConnectedToGameServer);
        }
    }

    public async Task SendClientReadyAfterMapChangeAsync()
    {
        ClientConnectionState state = CurrentState;
        if (state != ClientConnectionState.SelectingCharacter
            && state != ClientConnectionState.InGame
            && state != ClientConnectionState.ConnectedToGameServer)
        {
            return;
        }

        if (!_connectionManager.IsConnected)
        {
            return;
        }

        try
        {
            await _connectionManager.Connection.SendAsync(() =>
                MuPacketBuilder.BuildClientReadyAfterMapChangePacket(_connectionManager.Connection.Output));
        }
        catch (Exception ex)
        {
            EnqueueError($"Error sending ClientReadyAfterMapChange: {ex.Message}");
        }
    }

    public async Task SendWalkRequestAsync(byte startX, byte startY, byte[] path)
    {
        if (path == null || path.Length == 0)
        {
            return;
        }

        if (CurrentState != ClientConnectionState.InGame || !_connectionManager.IsConnected)
        {
            return;
        }

        int stepCount = Math.Min(path.Length, 15);
        byte[] clippedPath = stepCount == path.Length ? path : path[..stepCount];
        try
        {
            await _connectionManager.Connection.SendAsync(() =>
                MuPacketBuilder.BuildWalkRequestPacket(_connectionManager.Connection.Output, startX, startY, clippedPath));
        }
        catch (Exception ex)
        {
            EnqueueError($"Error sending walk request: {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        DetachConnectionHandlers(_connectionManager.CurrentConnection);

        try
        {
            _managerCts.Cancel();
        }
        catch
        {
            // Ignore cancellation race.
        }

        _managerCts.Dispose();
        _managerCts = new CancellationTokenSource();

        await _connectionManager.DisconnectAsync();
        UpdateState(ClientConnectionState.Disconnected);
    }

    private async Task RequestServerListAsync()
    {
        if (CurrentState != ClientConnectionState.ConnectedToConnectServer
            && CurrentState != ClientConnectionState.ReceivedServerList)
        {
            return;
        }

        if (!_connectionManager.IsConnected)
        {
            return;
        }

        UpdateState(ClientConnectionState.RequestingServerList);

        try
        {
            await _connectionManager.Connection.SendServerListRequestAsync();
        }
        catch (Exception ex)
        {
            EnqueueError($"Error requesting server list: {ex.Message}");
        }
    }

    private async Task RequestCharacterListAsync()
    {
        if (!_connectionManager.IsConnected)
        {
            EnqueueError("No connection - cannot request character list.");
            return;
        }

        try
        {
            await _connectionManager.Connection.SendAsync(() =>
                MuPacketBuilder.BuildRequestCharacterListPacket(_connectionManager.Connection.Output));
        }
        catch (Exception ex)
        {
            EnqueueError($"Error requesting character list: {ex.Message}");
        }
    }

    private void AttachConnectionHandlers()
    {
        var connection = _connectionManager.CurrentConnection;
        if (connection is null)
        {
            return;
        }

        connection.PacketReceived += HandlePacketAsync;
        connection.Disconnected += HandleDisconnectAsync;
    }

    private static void DetachConnectionHandlers(IConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        try
        {
            connection.PacketReceived -= Instance.HandlePacketAsync;
            connection.Disconnected -= Instance.HandleDisconnectAsync;
        }
        catch
        {
            // Ignore detaching races.
        }
    }

    private ValueTask HandlePacketAsync(ReadOnlySequence<byte> sequence)
    {
        byte[] packet = sequence.ToArray();

        try
        {
            if (_connectServerRouting)
            {
                HandleConnectServerPacket(packet);
            }
            else
            {
                HandleGameServerPacket(packet);
            }
        }
        catch (Exception ex)
        {
            EnqueueError($"Packet processing error: {ex.Message}");
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask HandleDisconnectAsync()
    {
        UpdateState(ClientConnectionState.Disconnected);
        EnqueueError("Connection to server lost.");
        return ValueTask.CompletedTask;
    }

    private void HandleConnectServerPacket(ReadOnlySpan<byte> packet)
    {
        if (!TryParseHeader(packet, out byte code, out byte subCode))
        {
            return;
        }

        if (code == Hello.Code && subCode == Hello.SubCode)
        {
            ProcessHelloPacket();
            return;
        }

        if (code == ServerListRequest.Code && subCode == ServerListResponse.SubCode)
        {
            ProcessServerList(packet);
            return;
        }

        if (code == ConnectionInfoRequest.Code && subCode == ConnectionInfo.SubCode)
        {
            ProcessConnectionInfo(packet);
        }
    }

    private void HandleGameServerPacket(ReadOnlySpan<byte> packet)
    {
        if (!TryParseHeader(packet, out byte code, out byte subCode))
        {
            return;
        }

        if (code == 0xF1 && subCode == 0x00)
        {
            ProcessGameServerEntered(packet);
            return;
        }

        if (code == 0xF1 && subCode == 0x01)
        {
            ProcessLoginResponse(packet);
            return;
        }

        if (code == 0xF3 && subCode == 0x00)
        {
            ProcessCharacterList(packet);
            return;
        }

        if (code == 0xF3 && subCode == 0x03)
        {
            ProcessCharacterInformation(packet);
            return;
        }

        if (code == 0xF3 && subCode == 0x04)
        {
            ProcessRespawnAfterDeath(packet);
            return;
        }

        if (code == 0x1C && subCode == 0x0F)
        {
            ProcessMapChanged(packet);
            return;
        }

        if (code == 0x12)
        {
            ProcessCharactersInScope(packet);
            return;
        }

        if (code == 0x13 || code == 0x16)
        {
            ProcessNpcsInScope(packet, isMonsterPacket: code == 0x16);
            return;
        }

        if (code == 0x14)
        {
            ProcessOutOfScope(packet);
            return;
        }

        if (code == 0x15)
        {
            ProcessObjectMoved(packet);
            return;
        }

        if (code == 0xD4)
        {
            ProcessObjectWalked(packet);
            return;
        }

        if (code == 0x18)
        {
            ProcessObjectAnimation(packet);
            return;
        }

        if (code == 0x19)
        {
            ProcessSkillAnimation(packet);
            return;
        }

        if (code == 0x1E)
        {
            ProcessAreaSkillAnimation(packet);
            return;
        }

        if (code == 0x11)
        {
            ProcessObjectHit(packet);
            return;
        }

        if (code == 0x17)
        {
            ProcessObjectKilled(packet);
            return;
        }

        if (code == 0x20)
        {
            ProcessItemsDropped(packet);
            return;
        }

        if (code == 0x21)
        {
            ProcessItemDropRemoved(packet);
            return;
        }

        if (code == 0x2F)
        {
            ProcessMoneyDroppedExtended(packet);
            return;
        }

        if (code == 0x26 && subCode == 0xFF)
        {
            ProcessCurrentHealthShield(packet);
            return;
        }

        if (code == 0x26 && subCode == 0xFE)
        {
            ProcessMaximumHealthShield(packet);
            return;
        }

        if (code == 0x27 && subCode == 0xFF)
        {
            ProcessCurrentManaAbility(packet);
            return;
        }

        if (code == 0x27 && subCode == 0xFE)
        {
            ProcessMaximumManaAbility(packet);
            return;
        }

        if (code == 0x00)
        {
            ProcessChat(packet);
        }
    }

    private void ProcessHelloPacket()
    {
        UpdateState(ClientConnectionState.ConnectedToConnectServer);
        _ = RequestServerListAsync();
    }

    private void ProcessServerList(ReadOnlySpan<byte> packet)
    {
        var response = new ServerListResponse(packet.ToArray());
        var servers = new List<MuServerInfo>();
        for (int i = 0; i < response.ServerCount; i++)
        {
            var info = response[i];
            servers.Add(new MuServerInfo
            {
                ServerId = info.ServerId,
                LoadPercentage = info.LoadPercentage,
            });
        }

        lock (_stateSync)
        {
            _serverList = servers;
        }

        UpdateState(ClientConnectionState.ReceivedServerList);
        _events.Enqueue(MuNetworkEvent.ServerList(GetCachedServerList()));
    }

    private void ProcessConnectionInfo(ReadOnlySpan<byte> packet)
    {
        var info = new ConnectionInfo(packet.ToArray());
        _ = SwitchToGameServerAsync(info.IpAddress, info.Port);
    }

    private async Task SwitchToGameServerAsync(string host, int port)
    {
        if (CurrentState != ClientConnectionState.RequestingConnectionInfo
            && CurrentState != ClientConnectionState.ReceivedConnectionInfo)
        {
            return;
        }

        UpdateState(ClientConnectionState.ReceivedConnectionInfo);

        var oldConnection = _connectionManager.CurrentConnection;
        if (oldConnection is not null)
        {
            try
            {
                oldConnection.PacketReceived -= HandlePacketAsync;
                oldConnection.Disconnected -= HandleDisconnectAsync;
            }
            catch
            {
                // Ignore detaching races.
            }
        }

        await _connectionManager.DisconnectAsync();

        UpdateState(ClientConnectionState.ConnectingToGameServer);
        _connectServerRouting = false;

        bool connected = await _connectionManager.ConnectAsync(host, port, useEncryption: true, _managerCts.Token);
        if (!connected)
        {
            EnqueueError($"Unable to connect to Game Server {host}:{port}.");
            UpdateState(ClientConnectionState.Disconnected);
            return;
        }

        _currentHost = host;
        _currentPort = port;

        AttachConnectionHandlers();
        _connectionManager.StartReceiving(_managerCts.Token);
    }

    private void ProcessGameServerEntered(ReadOnlySpan<byte> packet)
    {
        _ = new GameServerEntered(packet.ToArray());
        UpdateState(ClientConnectionState.ConnectedToGameServer);
    }

    private void ProcessLoginResponse(ReadOnlySpan<byte> packet)
    {
        var response = new LoginResponse(packet.ToArray());
        var result = (LoginResponse.LoginResult)response.Success;

        if (result == LoginResponse.LoginResult.Okay)
        {
            UpdateState(ClientConnectionState.ConnectedToGameServer);
            _events.Enqueue(MuNetworkEvent.LoginOk());
            _ = RequestCharacterListAsync();
            return;
        }

        _events.Enqueue(MuNetworkEvent.LoginError(result));
        UpdateState(ClientConnectionState.ConnectedToGameServer);
    }

    private void ProcessCharacterList(ReadOnlySpan<byte> packet)
    {
        var characters = ParseCharacterListSeason6(packet);

        lock (_stateSync)
        {
            _characters = characters;
        }

        _events.Enqueue(MuNetworkEvent.CharacterList(GetCachedCharacterList()));
    }

    private void ProcessCharacterInformation(ReadOnlySpan<byte> packet)
    {
        bool parsed = false;
        byte x = 0;
        byte y = 0;
        byte direction = 0;
        byte mapId = 0;
        bool hasMapId = false;

        try
        {
            var info = new CharacterInformationExtended(packet.ToArray());
            x = info.X;
            y = info.Y;
            mapId = (byte)(info.MapId & 0xFF);
            hasMapId = true;
            _currentHealth = info.CurrentHealth;
            _maximumHealth = info.MaximumHealth;
            _currentShield = info.CurrentShield;
            _maximumShield = info.MaximumShield;
            _currentMana = info.CurrentMana;
            _maximumMana = info.MaximumMana;
            _currentAbility = info.CurrentAbility;
            _maximumAbility = info.MaximumAbility;
            parsed = true;
        }
        catch
        {
            // Fallback below.
        }

        if (!parsed)
        {
            try
            {
                var info = new CharacterInformation(packet.ToArray());
                x = info.X;
                y = info.Y;
                mapId = (byte)(info.MapId & 0xFF);
                hasMapId = true;
                _currentHealth = info.CurrentHealth;
                _maximumHealth = info.MaximumHealth;
                _currentShield = info.CurrentShield;
                _maximumShield = info.MaximumShield;
                _currentMana = info.CurrentMana;
                _maximumMana = info.MaximumMana;
                _currentAbility = info.CurrentAbility;
                _maximumAbility = info.MaximumAbility;
                parsed = true;
            }
            catch
            {
                // Fallback below.
            }
        }

        if (!parsed)
        {
            try
            {
                var info = new CharacterInformation097(packet.ToArray());
                x = info.X;
                y = info.Y;
                direction = info.Direction;
                mapId = (byte)(info.MapId & 0xFF);
                hasMapId = true;
                _currentHealth = info.CurrentHealth;
                _maximumHealth = info.MaximumHealth;
                _currentMana = info.CurrentMana;
                _maximumMana = info.MaximumMana;
                _currentAbility = info.CurrentAbility;
                _maximumAbility = info.MaximumAbility;
                parsed = true;
            }
            catch
            {
                // Fallback below.
            }
        }

        if (!parsed)
        {
            try
            {
                var info = new CharacterInformation075(packet.ToArray());
                x = info.X;
                y = info.Y;
                mapId = (byte)(info.MapId & 0xFF);
                hasMapId = true;
                _currentHealth = info.CurrentHealth;
                _maximumHealth = info.MaximumHealth;
                _currentMana = info.CurrentMana;
                _maximumMana = info.MaximumMana;
                parsed = true;
            }
            catch
            {
                // Leave as not parsed.
            }
        }

        if (!parsed)
        {
            return;
        }

        if (hasMapId)
        {
            _currentMapId = mapId;
            MuLoginSession.SetLocation(mapId, x, y, direction);
            _worldEvents.Enqueue(MuWorldEvent.MapChanged(mapId, x, y, direction));
        }

        _worldEvents.Enqueue(MuWorldEvent.HeroPosition(x, y, direction));
        _worldEvents.Enqueue(MuWorldEvent.HeroStats(
            _currentHealth,
            _maximumHealth,
            _currentShield,
            _maximumShield,
            _currentMana,
            _maximumMana,
            _currentAbility,
            _maximumAbility));

        if (CurrentState == ClientConnectionState.SelectingCharacter
            || CurrentState == ClientConnectionState.ConnectedToGameServer)
        {
            if (!string.IsNullOrWhiteSpace(_selectedCharacterName) && _selfObjectId != 0)
            {
                _worldEvents.Enqueue(MuWorldEvent.Spawn(
                    MuWorldEntityKind.Player,
                    _selfObjectId,
                    x,
                    y,
                    direction,
                    name: _selectedCharacterName,
                    isSelf: true));
            }

            UpdateState(ClientConnectionState.InGame);
            _events.Enqueue(MuNetworkEvent.EnteredGame());
        }
    }

    private void ProcessRespawnAfterDeath(ReadOnlySpan<byte> packet)
    {
        bool parsed = false;
        byte x = 0;
        byte y = 0;
        byte direction = 0;

        try
        {
            var respawn = new RespawnAfterDeath095(packet.ToArray());
            x = respawn.PositionX;
            y = respawn.PositionY;
            direction = respawn.Direction;
            _currentHealth = respawn.CurrentHealth;
            _currentMana = respawn.CurrentMana;
            _currentAbility = respawn.CurrentAbility;
            parsed = true;
        }
        catch
        {
            // Fallback below.
        }

        if (!parsed)
        {
            try
            {
                var respawn = new RespawnAfterDeath075(packet.ToArray());
                x = respawn.PositionX;
                y = respawn.PositionY;
                direction = respawn.Direction;
                _currentHealth = respawn.CurrentHealth;
                _currentMana = respawn.CurrentMana;
                parsed = true;
            }
            catch
            {
                // Leave as not parsed.
            }
        }

        if (!parsed)
        {
            return;
        }

        MuLoginSession.SetLocation(_currentMapId, x, y, direction);
        _worldEvents.Enqueue(MuWorldEvent.HeroPosition(x, y, direction));
        _worldEvents.Enqueue(MuWorldEvent.HeroStats(
            _currentHealth,
            _maximumHealth,
            _currentShield,
            _maximumShield,
            _currentMana,
            _maximumMana,
            _currentAbility,
            _maximumAbility));

        if (CurrentState == ClientConnectionState.SelectingCharacter)
        {
            UpdateState(ClientConnectionState.InGame);
            _events.Enqueue(MuNetworkEvent.EnteredGame());
        }

        _ = SendClientReadyAfterMapChangeAsync();
    }

    private void ProcessMapChanged(ReadOnlySpan<byte> packet)
    {
        try
        {
            if (_settings.ProtocolVersion == MuProtocolVersion.Version075)
            {
                var map = new MapChanged075(packet.ToArray());
                _currentMapId = map.MapNumber;
                MuLoginSession.SetLocation(_currentMapId, map.PositionX, map.PositionY, map.Rotation);
                _worldEvents.Enqueue(MuWorldEvent.MapChanged(_currentMapId, map.PositionX, map.PositionY, map.Rotation));
                _worldEvents.Enqueue(MuWorldEvent.HeroPosition(map.PositionX, map.PositionY, map.Rotation));
            }
            else
            {
                var map = new MapChanged(packet.ToArray());
                _currentMapId = (byte)(map.MapNumber & 0xFF);
                MuLoginSession.SetLocation(_currentMapId, map.PositionX, map.PositionY, map.Rotation);
                _worldEvents.Enqueue(MuWorldEvent.MapChanged(_currentMapId, map.PositionX, map.PositionY, map.Rotation));
                _worldEvents.Enqueue(MuWorldEvent.HeroPosition(map.PositionX, map.PositionY, map.Rotation));
            }

            if (CurrentState == ClientConnectionState.SelectingCharacter)
            {
                UpdateState(ClientConnectionState.InGame);
                _events.Enqueue(MuNetworkEvent.EnteredGame());
            }
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessCharactersInScope(ReadOnlySpan<byte> packet)
    {
        try
        {
            switch (_settings.ProtocolVersion)
            {
                case MuProtocolVersion.Season6:
                {
                    byte[] scopeBytes = packet.ToArray();
                    var scope = new AddCharactersToScopeRef(scopeBytes);
                    for (int i = 0; i < scope.CharacterCount; i++)
                    {
                        var c = scope[i];
                        ushort id = MaskObjectId(c.Id);
                        bool isSelf = id == _selfObjectId && _selfObjectId != 0;
                        if (!isSelf && !string.IsNullOrWhiteSpace(_selectedCharacterName)
                            && string.Equals(c.Name, _selectedCharacterName, StringComparison.OrdinalIgnoreCase))
                        {
                            _selfObjectId = id;
                            isSelf = true;
                        }

                        _worldEvents.Enqueue(MuWorldEvent.Spawn(
                            MuWorldEntityKind.Player,
                            id,
                            c.CurrentPositionX,
                            c.CurrentPositionY,
                            c.Rotation,
                            name: c.Name,
                            isSelf: isSelf));
                    }

                    break;
                }
                case MuProtocolVersion.Version097:
                {
                    var scope = new AddCharactersToScope095(packet.ToArray());
                    for (int i = 0; i < scope.CharacterCount; i++)
                    {
                        var c = scope[i];
                        ushort id = MaskObjectId(c.Id);
                        bool isSelf = id == _selfObjectId && _selfObjectId != 0;
                        if (!isSelf && !string.IsNullOrWhiteSpace(_selectedCharacterName)
                            && string.Equals(c.Name, _selectedCharacterName, StringComparison.OrdinalIgnoreCase))
                        {
                            _selfObjectId = id;
                            isSelf = true;
                        }

                        _worldEvents.Enqueue(MuWorldEvent.Spawn(
                            MuWorldEntityKind.Player,
                            id,
                            c.CurrentPositionX,
                            c.CurrentPositionY,
                            c.Rotation,
                            name: c.Name,
                            isSelf: isSelf));
                    }

                    break;
                }
                case MuProtocolVersion.Version075:
                {
                    var scope = new AddCharactersToScope075(packet.ToArray());
                    for (int i = 0; i < scope.CharacterCount; i++)
                    {
                        var c = scope[i];
                        ushort id = MaskObjectId(c.Id);
                        bool isSelf = id == _selfObjectId && _selfObjectId != 0;
                        if (!isSelf && !string.IsNullOrWhiteSpace(_selectedCharacterName)
                            && string.Equals(c.Name, _selectedCharacterName, StringComparison.OrdinalIgnoreCase))
                        {
                            _selfObjectId = id;
                            isSelf = true;
                        }

                        _worldEvents.Enqueue(MuWorldEvent.Spawn(
                            MuWorldEntityKind.Player,
                            id,
                            c.CurrentPositionX,
                            c.CurrentPositionY,
                            c.Rotation,
                            name: c.Name,
                            isSelf: isSelf));
                    }

                    break;
                }
            }
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessNpcsInScope(ReadOnlySpan<byte> packet, bool isMonsterPacket)
    {
        MuWorldEntityKind kind = isMonsterPacket ? MuWorldEntityKind.Monster : MuWorldEntityKind.Npc;

        try
        {
            switch (_settings.ProtocolVersion)
            {
                case MuProtocolVersion.Season6:
                {
                    var scope = new AddNpcsToScope(packet.ToArray());
                    for (int i = 0; i < scope.NpcCount; i++)
                    {
                        var n = scope[i];
                        _worldEvents.Enqueue(MuWorldEvent.Spawn(
                            kind,
                            MaskObjectId(n.Id),
                            n.CurrentPositionX,
                            n.CurrentPositionY,
                            n.Rotation,
                            type: n.TypeNumber,
                            name: $"{(isMonsterPacket ? "Monster" : "Npc")} {n.TypeNumber}"));
                    }

                    break;
                }
                case MuProtocolVersion.Version097:
                {
                    var scope = new AddNpcsToScope095(packet.ToArray());
                    for (int i = 0; i < scope.NpcCount; i++)
                    {
                        var n = scope[i];
                        _worldEvents.Enqueue(MuWorldEvent.Spawn(
                            kind,
                            MaskObjectId(n.Id),
                            n.CurrentPositionX,
                            n.CurrentPositionY,
                            n.Rotation,
                            type: n.TypeNumber,
                            name: $"{(isMonsterPacket ? "Monster" : "Npc")} {n.TypeNumber}"));
                    }

                    break;
                }
                case MuProtocolVersion.Version075:
                {
                    var scope = new AddNpcsToScope075(packet.ToArray());
                    for (int i = 0; i < scope.NpcCount; i++)
                    {
                        var n = scope[i];
                        _worldEvents.Enqueue(MuWorldEvent.Spawn(
                            kind,
                            MaskObjectId(n.Id),
                            n.CurrentPositionX,
                            n.CurrentPositionY,
                            n.Rotation,
                            type: n.TypeNumber,
                            name: $"{(isMonsterPacket ? "Monster" : "Npc")} {n.TypeNumber}"));
                    }

                    break;
                }
            }
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessOutOfScope(ReadOnlySpan<byte> packet)
    {
        try
        {
            var outOfScope = new MapObjectOutOfScope(packet.ToArray());
            for (int i = 0; i < outOfScope.ObjectCount; i++)
            {
                ushort id = MaskObjectId(outOfScope[i].Id);
                if (id == _selfObjectId)
                {
                    continue;
                }

                _worldEvents.Enqueue(MuWorldEvent.Remove(id));
            }
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessObjectMoved(ReadOnlySpan<byte> packet)
    {
        try
        {
            var moved = new ObjectMoved(packet.ToArray());
            ushort id = MaskObjectId(moved.ObjectId);
            _worldEvents.Enqueue(MuWorldEvent.Move(
                id,
                moved.PositionX,
                moved.PositionY,
                0));

            if (_selfObjectId != 0 && id == _selfObjectId)
            {
                MuLoginSession.SetLocation(_currentMapId, moved.PositionX, moved.PositionY, 0);
                _worldEvents.Enqueue(MuWorldEvent.HeroPosition(moved.PositionX, moved.PositionY, 0));
            }
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessObjectWalked(ReadOnlySpan<byte> packet)
    {
        try
        {
            if (_settings.ProtocolVersion == MuProtocolVersion.Version075)
            {
                var walked = new ObjectWalked075(packet.ToArray());
                ushort id = MaskObjectId(walked.ObjectId);
                _worldEvents.Enqueue(MuWorldEvent.Move(
                    id,
                    walked.TargetX,
                    walked.TargetY,
                    walked.TargetRotation));
                if (_selfObjectId != 0 && id == _selfObjectId)
                {
                    MuLoginSession.SetLocation(_currentMapId, walked.TargetX, walked.TargetY, walked.TargetRotation);
                    _worldEvents.Enqueue(MuWorldEvent.HeroPosition(walked.TargetX, walked.TargetY, walked.TargetRotation));
                }
                return;
            }

            try
            {
                var walked = new ObjectWalkedExtended(packet.ToArray());
                ushort id = MaskObjectId(walked.ObjectId);
                _worldEvents.Enqueue(MuWorldEvent.Move(
                    id,
                    walked.TargetX,
                    walked.TargetY,
                    walked.TargetRotation));
                if (_selfObjectId != 0 && id == _selfObjectId)
                {
                    MuLoginSession.SetLocation(_currentMapId, walked.TargetX, walked.TargetY, walked.TargetRotation);
                    _worldEvents.Enqueue(MuWorldEvent.HeroPosition(walked.TargetX, walked.TargetY, walked.TargetRotation));
                }
            }
            catch
            {
                var walked = new ObjectWalked(packet.ToArray());
                ushort id = MaskObjectId(walked.ObjectId);
                _worldEvents.Enqueue(MuWorldEvent.Move(
                    id,
                    walked.TargetX,
                    walked.TargetY,
                    walked.TargetRotation));
                if (_selfObjectId != 0 && id == _selfObjectId)
                {
                    MuLoginSession.SetLocation(_currentMapId, walked.TargetX, walked.TargetY, walked.TargetRotation);
                    _worldEvents.Enqueue(MuWorldEvent.HeroPosition(walked.TargetX, walked.TargetY, walked.TargetRotation));
                }
            }
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessObjectAnimation(ReadOnlySpan<byte> packet)
    {
        try
        {
            var animation = new ObjectAnimation(packet.ToArray());
            _worldEvents.Enqueue(MuWorldEvent.Animation(
                MaskObjectId(animation.ObjectId),
                MaskObjectId(animation.TargetId),
                animation.Direction,
                animation.Animation));
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessSkillAnimation(ReadOnlySpan<byte> packet)
    {
        try
        {
            if (_settings.ProtocolVersion == MuProtocolVersion.Version075)
            {
                var animation = new SkillAnimation075(packet.ToArray());
                _worldEvents.Enqueue(MuWorldEvent.Animation(
                    MaskObjectId(animation.PlayerId),
                    MaskObjectId(animation.TargetId),
                    0,
                    animation.SkillId));
                return;
            }

            if (_settings.ProtocolVersion == MuProtocolVersion.Version097)
            {
                var animation = new SkillAnimation095(packet.ToArray());
                _worldEvents.Enqueue(MuWorldEvent.Animation(
                    MaskObjectId(animation.PlayerId),
                    MaskObjectId(animation.TargetId),
                    0,
                    animation.SkillId));
                return;
            }

            var s6Animation = new SkillAnimation(packet.ToArray());
            _worldEvents.Enqueue(MuWorldEvent.Animation(
                MaskObjectId(s6Animation.PlayerId),
                MaskObjectId(s6Animation.TargetId),
                0,
                s6Animation.SkillId));
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessAreaSkillAnimation(ReadOnlySpan<byte> packet)
    {
        try
        {
            if (_settings.ProtocolVersion == MuProtocolVersion.Version075)
            {
                var animation = new AreaSkillAnimation075(packet.ToArray());
                _worldEvents.Enqueue(MuWorldEvent.Animation(
                    MaskObjectId(animation.PlayerId),
                    0,
                    animation.Rotation,
                    animation.SkillId));
                return;
            }

            if (_settings.ProtocolVersion == MuProtocolVersion.Version097)
            {
                var animation = new AreaSkillAnimation095(packet.ToArray());
                _worldEvents.Enqueue(MuWorldEvent.Animation(
                    MaskObjectId(animation.PlayerId),
                    0,
                    animation.Rotation,
                    animation.SkillId));
                return;
            }

            var s6Animation = new AreaSkillAnimation(packet.ToArray());
            _worldEvents.Enqueue(MuWorldEvent.Animation(
                MaskObjectId(s6Animation.PlayerId),
                0,
                s6Animation.Rotation,
                s6Animation.SkillId));
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessObjectHit(ReadOnlySpan<byte> packet)
    {
        try
        {
            if (packet.Length >= ObjectHitExtended.Length)
            {
                var hit = new ObjectHitExtended(packet.ToArray());
                float? healthFraction = hit.HealthStatus == byte.MaxValue ? null : hit.HealthStatus / 250f;
                float? shieldFraction = hit.ShieldStatus == byte.MaxValue ? null : hit.ShieldStatus / 250f;
                _worldEvents.Enqueue(MuWorldEvent.Hit(
                    MaskObjectId(hit.ObjectId),
                    (int)hit.HealthDamage,
                    (int)hit.ShieldDamage,
                    hit.Kind,
                    healthFraction,
                    shieldFraction));
                return;
            }

            var shortHit = new ObjectHit(packet.ToArray());
            _worldEvents.Enqueue(MuWorldEvent.Hit(
                MaskObjectId(shortHit.ObjectId),
                (int)shortHit.HealthDamage,
                (int)shortHit.ShieldDamage,
                shortHit.Kind,
                null,
                null));
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessObjectKilled(ReadOnlySpan<byte> packet)
    {
        try
        {
            var killed = new ObjectGotKilled(packet.ToArray());
            _worldEvents.Enqueue(MuWorldEvent.Kill(
                MaskObjectId(killed.KilledId),
                MaskObjectId(killed.KillerId),
                killed.SkillId));
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessItemsDropped(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 6)
        {
            return;
        }

        // Minimal parser: extract ids and positions, skip variable item payload.
        // This keeps drop spawning responsive without depending on per-item length heuristics.
        int count = packet[4];
        int offset = 5;
        int minEntrySize = 4;

        for (int i = 0; i < count; i++)
        {
            if (offset + minEntrySize > packet.Length)
            {
                break;
            }

            ushort id = MaskObjectId(BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2)));
            byte x = packet[offset + 2];
            byte y = packet[offset + 3];
            _worldEvents.Enqueue(MuWorldEvent.Spawn(MuWorldEntityKind.Item, id, x, y, 0, name: "Item Drop"));

            // Try to skip classic 12-byte item data first; fallback to raw step if packet ends sooner.
            int nextOffset = offset + 4 + 12;
            offset = nextOffset <= packet.Length ? nextOffset : offset + 4;
        }
    }

    private void ProcessItemDropRemoved(ReadOnlySpan<byte> packet)
    {
        try
        {
            var removed = new ItemDropRemoved(packet.ToArray());
            for (int i = 0; i < removed.ItemCount; i++)
            {
                _worldEvents.Enqueue(MuWorldEvent.Remove(MaskObjectId(removed[i].Id)));
            }
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessMoneyDroppedExtended(ReadOnlySpan<byte> packet)
    {
        try
        {
            var money = new MoneyDroppedExtended(packet.ToArray());
            _worldEvents.Enqueue(MuWorldEvent.Spawn(
                MuWorldEntityKind.Money,
                MaskObjectId(money.Id),
                money.PositionX,
                money.PositionY,
                0,
                name: "Money",
                moneyAmount: money.Amount));
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessCurrentHealthShield(ReadOnlySpan<byte> packet)
    {
        try
        {
            if (packet.Length >= CurrentStatsExtended.Length)
            {
                var statsExtended = new CurrentStatsExtended(packet.ToArray());
                _currentHealth = statsExtended.Health;
                _currentShield = statsExtended.Shield;
                _currentMana = statsExtended.Mana;
                _currentAbility = statsExtended.Ability;
                PushHeroStats();
                return;
            }

            var stats = new CurrentHealthAndShield(packet.ToArray());
            _currentHealth = stats.Health;
            _currentShield = stats.Shield;
            PushHeroStats();
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessMaximumHealthShield(ReadOnlySpan<byte> packet)
    {
        try
        {
            if (packet.Length >= MaximumStatsExtended.Length)
            {
                var statsExtended = new MaximumStatsExtended(packet.ToArray());
                _maximumHealth = statsExtended.Health;
                _maximumShield = statsExtended.Shield;
                _maximumMana = statsExtended.Mana;
                _maximumAbility = statsExtended.Ability;
                PushHeroStats();
                return;
            }

            var stats = new MaximumHealthAndShield(packet.ToArray());
            _maximumHealth = stats.Health;
            _maximumShield = stats.Shield;
            PushHeroStats();
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessCurrentManaAbility(ReadOnlySpan<byte> packet)
    {
        try
        {
            var stats = new CurrentManaAndAbility(packet.ToArray());
            _currentMana = stats.Mana;
            _currentAbility = stats.Ability;
            PushHeroStats();
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessMaximumManaAbility(ReadOnlySpan<byte> packet)
    {
        try
        {
            var stats = new MaximumManaAndAbility(packet.ToArray());
            _maximumMana = stats.Mana;
            _maximumAbility = stats.Ability;
            PushHeroStats();
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void ProcessChat(ReadOnlySpan<byte> packet)
    {
        try
        {
            var chat = new ChatMessage(packet.ToArray());
            _worldEvents.Enqueue(MuWorldEvent.Chat(chat.Sender, chat.Message));
        }
        catch
        {
            // Ignore malformed packet.
        }
    }

    private void PushHeroStats()
    {
        _worldEvents.Enqueue(MuWorldEvent.HeroStats(
            _currentHealth,
            _maximumHealth,
            _currentShield,
            _maximumShield,
            _currentMana,
            _maximumMana,
            _currentAbility,
            _maximumAbility));
    }

    private static ushort MaskObjectId(ushort rawId) => (ushort)(rawId & 0x7FFF);

    private static List<MuCharacterInfo> ParseCharacterListSeason6(ReadOnlySpan<byte> packet)
    {
        var list = new List<MuCharacterInfo>();

        const int headerLength = 8;
        if (packet.Length < headerLength)
        {
            return list;
        }

        byte count = packet[6];
        int offset = headerLength;
        if (count == 0)
        {
            return list;
        }

        int remaining = packet.Length - offset;
        int entryLength;
        if (remaining == count * 44)
        {
            entryLength = 44;
        }
        else if (remaining == count * 42)
        {
            entryLength = 42;
        }
        else
        {
            entryLength = 34;
        }

        int maxCount = Math.Max(0, remaining / entryLength);
        if (count > maxCount)
        {
            count = (byte)maxCount;
        }

        for (int i = 0; i < count; i++)
        {
            int pos = offset + (i * entryLength);
            if (pos + entryLength > packet.Length)
            {
                break;
            }

            var span = packet.Slice(pos, entryLength);
            string name;
            ushort level;
            CharacterClassNumber cls;
            byte[] appearance;

            try
            {
                if (entryLength == 34)
                {
                    var entry = new CharacterList.CharacterData(packet.Slice(pos, entryLength).ToArray());
                    name = entry.Name;
                    level = entry.Level;
                    appearance = entry.Appearance.ToArray();
                    cls = DecodeClassFromAppearance(appearance);
                }
                else if (entryLength == 42)
                {
                    name = Encoding.UTF8.GetString(span.Slice(1, 10)).TrimEnd('\0');
                    level = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(11, 2));
                    cls = DecodeClassFromSourceMainField(span[14]);
                    appearance = span.Slice(16, Math.Min(25, span.Length - 16)).ToArray();
                }
                else
                {
                    name = Encoding.UTF8.GetString(span.Slice(1, 10)).TrimEnd('\0');
                    level = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(12, 2));

                    byte flags = span[16];
                    if (IsLikelySourceMainEquipmentFlags(flags))
                    {
                        cls = DecodeClassFromSourceMainField(span[15]);
                        appearance = span.Slice(17, Math.Min(25, span.Length - 17)).ToArray();
                    }
                    else
                    {
                        var fullAppearance = span.Slice(15, Math.Min(27, span.Length - 15));
                        appearance = fullAppearance.Length >= 18
                            ? fullAppearance.Slice(0, 18).ToArray()
                            : fullAppearance.ToArray();
                        cls = DecodeClassFromAppearance(appearance);
                    }
                }

                list.Add(new MuCharacterInfo
                {
                    Name = name,
                    Class = cls,
                    Level = level,
                    Appearance = appearance,
                });
            }
            catch
            {
                // Skip broken entries and continue parsing the rest.
            }
        }

        return list;
    }

    private void UpdateState(ClientConnectionState newState)
    {
        bool changed;

        lock (_stateSync)
        {
            changed = _state != newState;
            _state = newState;
        }

        if (changed)
        {
            _events.Enqueue(MuNetworkEvent.StateChanged(newState));
        }
    }

    private void EnqueueError(string message)
    {
        GD.PrintErr($"[MuNetworkClient] {message}");
        _events.Enqueue(MuNetworkEvent.Error(message));
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> packet, out byte code, out byte subCode)
    {
        code = 0;
        subCode = NoSubCode;

        if (packet.Length < 3)
        {
            return false;
        }

        byte header = packet[0];
        switch (header)
        {
            case 0xC1:
            case 0xC3:
                code = packet[2];
                if (packet.Length > 3)
                {
                    subCode = packet[3];
                }
                return true;
            case 0xC2:
            case 0xC4:
                if (packet.Length < 4)
                {
                    return false;
                }

                code = packet[3];
                if (packet.Length > 4)
                {
                    subCode = packet[4];
                }
                return true;
            default:
                return false;
        }
    }

    private static byte[] BuildClientVersionBytes(string clientVersion)
    {
        if (string.IsNullOrWhiteSpace(clientVersion))
        {
            return new byte[ClientVersionLength];
        }

        string trimmed = clientVersion.Trim();

        if (trimmed.Length == ClientVersionLength && trimmed.All(char.IsDigit))
        {
            return Encoding.ASCII.GetBytes(trimmed);
        }

        if (TryNormalizeClientVersion(trimmed, out string normalized))
        {
            return Encoding.ASCII.GetBytes(normalized);
        }

        return ToFixedLengthAsciiBytes(trimmed, ClientVersionLength);
    }

    private static byte[] BuildClientSerialBytes(string clientSerial)
    {
        if (string.IsNullOrWhiteSpace(clientSerial))
        {
            return new byte[ClientSerialLength];
        }

        return ToFixedLengthAsciiBytes(clientSerial.Trim(), ClientSerialLength);
    }

    private static byte[] ToFixedLengthAsciiBytes(string value, int length)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        var fixedLength = new byte[length];
        Array.Copy(bytes, fixedLength, Math.Min(length, bytes.Length));
        return fixedLength;
    }

    private static bool TryNormalizeClientVersion(string input, out string normalized)
    {
        normalized = string.Empty;
        string[] parts = input.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            return false;
        }

        if (!TryParseFirstDigit(parts[0], out int major))
        {
            return false;
        }

        int minor;
        int patch;

        if (parts.Length == 2)
        {
            if (!TryParseMinorAndPatch(parts[1], out minor, out patch))
            {
                return false;
            }
        }
        else
        {
            if (!TryParseTwoDigits(parts[1], out minor) || !TryParsePatchToken(parts[2], out patch))
            {
                return false;
            }
        }

        normalized = $"{major}{minor:00}{patch:00}";
        return normalized.Length == ClientVersionLength;
    }

    private static bool TryParseFirstDigit(string token, out int value)
    {
        foreach (char ch in token)
        {
            if (char.IsDigit(ch))
            {
                value = ch - '0';
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryParseTwoDigits(string token, out int value)
    {
        value = 0;
        int digits = 0;

        foreach (char ch in token)
        {
            if (!char.IsDigit(ch))
            {
                break;
            }

            value = (value * 10) + (ch - '0');
            digits++;
            if (digits == 2)
            {
                break;
            }
        }

        return digits > 0;
    }

    private static bool TryParseMinorAndPatch(string token, out int minor, out int patch)
    {
        minor = 0;
        patch = 0;
        int digits = 0;
        int patchDigits = 0;
        char patchLetter = '\0';

        foreach (char ch in token)
        {
            if (char.IsDigit(ch))
            {
                if (digits < 2)
                {
                    minor = (minor * 10) + (ch - '0');
                }
                else if (patchDigits < 2)
                {
                    patch = (patch * 10) + (ch - '0');
                    patchDigits++;
                }

                digits++;
            }
            else if (patchLetter == '\0' && char.IsLetter(ch))
            {
                patchLetter = ch;
            }
        }

        if (digits == 0)
        {
            return false;
        }

        if (patchLetter != '\0')
        {
            patch = LetterToNumber(patchLetter);
        }

        return true;
    }

    private static bool TryParsePatchToken(string token, out int patch)
    {
        patch = 0;
        int digits = 0;

        foreach (char ch in token)
        {
            if (char.IsDigit(ch))
            {
                patch = (patch * 10) + (ch - '0');
                digits++;
                if (digits == 2)
                {
                    return true;
                }
            }
            else if (char.IsLetter(ch))
            {
                patch = LetterToNumber(ch);
                return patch > 0;
            }
        }

        return digits > 0;
    }

    private static int LetterToNumber(char letter)
    {
        char upper = char.ToUpperInvariant(letter);
        return upper is >= 'A' and <= 'Z' ? (upper - 'A') + 1 : 0;
    }

    private static CharacterClassNumber DecodeClassFromSourceMainField(byte rawClassValue)
    {
        if (IsKnownServerClassValue(rawClassValue))
        {
            return MapClassValueToEnum(rawClassValue);
        }

        int shiftedBy3 = (rawClassValue >> 3) & 0b1_1111;
        if (IsKnownServerClassValue(shiftedBy3))
        {
            return MapClassValueToEnum(shiftedBy3);
        }

        int renderedClass = (rawClassValue >> 4) & 0x0F;
        if (TryMapRenderedClassToEnum(renderedClass, out CharacterClassNumber rendered))
        {
            return rendered;
        }

        int baseClass = rawClassValue & 0x07;
        if (TryMapBaseClassToEnum(baseClass, out CharacterClassNumber baseMapped))
        {
            return baseMapped;
        }

        return CharacterClassNumber.DarkWizard;
    }

    private static CharacterClassNumber DecodeClassFromAppearance(ReadOnlySpan<byte> appearance)
    {
        if (appearance.IsEmpty)
        {
            return CharacterClassNumber.DarkWizard;
        }

        byte raw = appearance[0];

        int raw5 = (raw >> 3) & 0b1_1111;
        if (IsKnownServerClassValue(raw5))
        {
            return MapClassValueToEnum(raw5);
        }

        int renderedClass = (raw >> 4) & 0x0F;
        if (TryMapRenderedClassToEnum(renderedClass, out CharacterClassNumber rendered))
        {
            return rendered;
        }

        int baseClass = raw & 0x07;
        if (TryMapBaseClassToEnum(baseClass, out CharacterClassNumber baseMapped))
        {
            return baseMapped;
        }

        return CharacterClassNumber.DarkWizard;
    }

    private static CharacterClassNumber MapClassValueToEnum(int value)
    {
        return value switch
        {
            0 or 2 or 3 or 4 or 6 or 7 or 8 or 10 or 11 or 12 or 13
            or 16 or 17 or 20 or 22 or 23 or 24 or 25 => (CharacterClassNumber)value,
            _ => CharacterClassNumber.DarkWizard,
        };
    }

    private static bool TryMapRenderedClassToEnum(int renderedClass, out CharacterClassNumber cls)
    {
        cls = renderedClass switch
        {
            0 => CharacterClassNumber.DarkWizard,
            1 => CharacterClassNumber.DarkKnight,
            2 => CharacterClassNumber.FairyElf,
            3 => CharacterClassNumber.MagicGladiator,
            4 => CharacterClassNumber.DarkLord,
            5 => CharacterClassNumber.Summoner,
            6 => CharacterClassNumber.RageFighter,
            8 => CharacterClassNumber.SoulMaster,
            9 => CharacterClassNumber.BladeKnight,
            10 => CharacterClassNumber.MuseElf,
            11 => CharacterClassNumber.DuelMaster,
            12 => CharacterClassNumber.LordEmperor,
            13 => CharacterClassNumber.BloodySummoner,
            14 => CharacterClassNumber.FistMaster,
            15 => CharacterClassNumber.GrandMaster,
            _ => CharacterClassNumber.DarkWizard,
        };

        return renderedClass is 0 or 1 or 2 or 3 or 4 or 5 or 6 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15;
    }

    private static bool TryMapBaseClassToEnum(int baseClass, out CharacterClassNumber cls)
    {
        cls = baseClass switch
        {
            0 => CharacterClassNumber.DarkWizard,
            1 => CharacterClassNumber.DarkKnight,
            2 => CharacterClassNumber.FairyElf,
            3 => CharacterClassNumber.MagicGladiator,
            4 => CharacterClassNumber.DarkLord,
            5 => CharacterClassNumber.Summoner,
            6 => CharacterClassNumber.RageFighter,
            _ => CharacterClassNumber.DarkWizard,
        };

        return baseClass is >= 0 and <= 6;
    }

    private static bool IsLikelySourceMainEquipmentFlags(byte flags)
    {
        return (flags & 0xCF) == 0;
    }

    private static bool IsKnownServerClassValue(int value)
    {
        return value is 0 or 2 or 3 or 4 or 6 or 7 or 8 or 10 or 11 or 12 or 13
            or 16 or 17 or 20 or 22 or 23 or 24 or 25;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        await _connectionManager.DisposeAsync();
    }
}
