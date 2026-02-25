using Godot;
using MuGodot.Networking;

namespace MuGodot.Login;

public partial class LoginScene : Control
{
    [Export] public string ConnectServerHost { get; set; } = "192.168.55.220";
    [Export] public int ConnectServerPort { get; set; } = 44405;
    [Export] public string ClientVersion { get; set; } = "2.04d";
    [Export] public string ClientSerial { get; set; } = "k1Pk2jcET48mxL3b";

    private Label _statusLabel = null!;
    private VBoxContainer _serverPanel = null!;
    private OptionButton _serverOption = null!;
    private Button _selectServerButton = null!;

    private VBoxContainer _loginPanel = null!;
    private LineEdit _usernameEdit = null!;
    private LineEdit _passwordEdit = null!;
    private Button _loginButton = null!;
    private Button _offlineButton = null!;

    private bool _forceLoginVisibility;

    public override void _Ready()
    {
        _statusLabel = GetNode<Label>("CenterPanel/Margin/Content/StatusLabel");

        _serverPanel = GetNode<VBoxContainer>("CenterPanel/Margin/Content/ServerPanel");
        _serverOption = GetNode<OptionButton>("CenterPanel/Margin/Content/ServerPanel/ServerOption");
        _selectServerButton = GetNode<Button>("CenterPanel/Margin/Content/ServerPanel/SelectServerButton");

        _loginPanel = GetNode<VBoxContainer>("CenterPanel/Margin/Content/LoginPanel");
        _usernameEdit = GetNode<LineEdit>("CenterPanel/Margin/Content/LoginPanel/UsernameEdit");
        _passwordEdit = GetNode<LineEdit>("CenterPanel/Margin/Content/LoginPanel/PasswordEdit");
        _loginButton = GetNode<Button>("CenterPanel/Margin/Content/LoginPanel/LoginButton");
        _offlineButton = GetNode<Button>("CenterPanel/Margin/Content/OfflineButton");

        _selectServerButton.Pressed += OnSelectServerPressed;
        _loginButton.Pressed += OnLoginPressed;
        _offlineButton.Pressed += OnOfflinePressed;
        _passwordEdit.TextSubmitted += _ => OnLoginPressed();
        _usernameEdit.TextSubmitted += _ => OnLoginPressed();

        MuLoginSession.OfflineTestMode = false;

        var settings = new MuNetworkSettings
        {
            ConnectServerHost = ConnectServerHost,
            ConnectServerPort = ConnectServerPort,
            ClientVersion = ClientVersion,
            ClientSerial = ClientSerial,
        };

        MuNetworkClient.Instance.Configure(settings);

        UpdateStatus("Status: Initializing...");
        ApplyState(MuNetworkClient.Instance.CurrentState);

        _ = MuNetworkClient.Instance.ConnectToConnectServerAsync();
    }

    public override void _Process(double delta)
    {
        while (MuNetworkClient.Instance.TryDequeueEvent(out MuNetworkEvent networkEvent))
        {
            HandleNetworkEvent(networkEvent);
        }
    }

    private void HandleNetworkEvent(MuNetworkEvent networkEvent)
    {
        switch (networkEvent.Kind)
        {
            case MuNetworkEventKind.StateChanged:
                ApplyState(networkEvent.State);
                UpdateStatus($"Status: {networkEvent.State}");
                break;
            case MuNetworkEventKind.ServerListReceived:
                PopulateServerList(networkEvent.Servers);
                break;
            case MuNetworkEventKind.LoginSucceeded:
                UpdateStatus("Status: Login OK - requesting character list...");
                break;
            case MuNetworkEventKind.LoginFailed:
                UpdateStatus($"Status: Login failed ({networkEvent.LoginResult})");
                break;
            case MuNetworkEventKind.CharacterListReceived:
                MuLoginSession.OfflineTestMode = false;
                MuLoginSession.Characters = networkEvent.Characters.ToList();
                MuLoginSession.SelectedCharacter = null;
                MuLoginSession.SelectedCharacterName = string.Empty;
                MuLoginSession.CurrentMapId = null;
                MuLoginSession.CurrentPositionX = 0;
                MuLoginSession.CurrentPositionY = 0;
                MuLoginSession.CurrentDirection = 0;
                GetTree().ChangeSceneToFile("res://Scenes/SelectCharacterScene.tscn");
                break;
            case MuNetworkEventKind.Error:
                UpdateStatus($"Error: {networkEvent.Message}");
                break;
        }
    }

    private void ApplyState(ClientConnectionState state)
    {
        bool showServerSelection = state is ClientConnectionState.ConnectedToConnectServer
            or ClientConnectionState.RequestingServerList
            or ClientConnectionState.ReceivedServerList;

        bool showLogin = state is ClientConnectionState.RequestingConnectionInfo
            or ClientConnectionState.ReceivedConnectionInfo
            or ClientConnectionState.ConnectingToGameServer
            or ClientConnectionState.ConnectedToGameServer
            or ClientConnectionState.Authenticating;

        if (state is ClientConnectionState.Disconnected or ClientConnectionState.Initial)
        {
            _forceLoginVisibility = false;
        }

        _serverPanel.Visible = showServerSelection && !_forceLoginVisibility;
        _loginPanel.Visible = showLogin || _forceLoginVisibility;

        _selectServerButton.Disabled = state is not ClientConnectionState.ReceivedServerList;
        _loginButton.Disabled = state is not ClientConnectionState.ConnectedToGameServer;
    }

    private void PopulateServerList(IReadOnlyList<MuServerInfo> servers)
    {
        _serverOption.Clear();

        for (int i = 0; i < servers.Count; i++)
        {
            MuServerInfo server = servers[i];
            string label = $"Server {server.ServerId} ({server.LoadPercentage}%)";
            _serverOption.AddItem(label, i);
            _serverOption.SetItemMetadata(i, server.ServerId);
        }

        if (servers.Count > 0)
        {
            _serverOption.Select(0);
            _selectServerButton.Disabled = false;
        }

        UpdateStatus($"Status: Received server list ({servers.Count})");
    }

    private void OnSelectServerPressed()
    {
        if (_serverOption.ItemCount == 0 || _serverOption.Selected < 0)
        {
            return;
        }

        Variant metadata = _serverOption.GetItemMetadata(_serverOption.Selected);
        ushort serverId;

        if (metadata.VariantType == Variant.Type.Int)
        {
            serverId = (ushort)(int)metadata;
        }
        else
        {
            return;
        }

        _forceLoginVisibility = true;
        _serverPanel.Visible = false;
        _loginPanel.Visible = true;
        _loginButton.Disabled = true;

        UpdateStatus($"Status: Requesting server {serverId}...");

        _ = MuNetworkClient.Instance.RequestGameServerConnectionAsync(serverId);
    }

    private void OnLoginPressed()
    {
        string username = _usernameEdit.Text.Trim();
        string password = _passwordEdit.Text;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            UpdateStatus("Error: Enter username and password.");
            return;
        }

        _loginButton.Disabled = true;
        UpdateStatus("Status: Sending login request...");
        _ = MuNetworkClient.Instance.SendLoginRequestAsync(username, password);
    }

    private void OnOfflinePressed()
    {
        MuLoginSession.OfflineTestMode = true;
        MuLoginSession.Characters = Array.Empty<MuCharacterInfo>();
        MuLoginSession.SelectedCharacter = null;
        MuLoginSession.SelectedCharacterName = "OfflineTester";
        MuLoginSession.SetLocation(mapId: 0, x: 128, y: 128, direction: 0);

        _selectServerButton.Disabled = true;
        _loginButton.Disabled = true;
        _offlineButton.Disabled = true;
        UpdateStatus("Status: Starting offline equipment test mode...");

        _ = MuNetworkClient.Instance.DisconnectAsync();
        GetTree().ChangeSceneToFile("res://Scenes/GameScene.tscn");
    }

    private void UpdateStatus(string text)
    {
        _statusLabel.Text = text;
    }
}
