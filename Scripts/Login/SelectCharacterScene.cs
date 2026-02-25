using Godot;
using MuGodot.Networking;

namespace MuGodot.Login;

public partial class SelectCharacterScene : Control
{
    private Label _statusLabel = null!;
    private ItemList _characterList = null!;
    private Button _enterGameButton = null!;
    private int _selectedIndex = -1;

    public override void _Ready()
    {
        _statusLabel = GetNode<Label>("CenterPanel/Margin/Content/StatusLabel");
        _characterList = GetNode<ItemList>("CenterPanel/Margin/Content/CharacterList");
        _enterGameButton = GetNode<Button>("CenterPanel/Margin/Content/EnterGameButton");

        _characterList.ItemSelected += OnCharacterSelected;
        _characterList.ItemActivated += OnCharacterActivated;
        _enterGameButton.Pressed += OnEnterGamePressed;
        _enterGameButton.Disabled = true;

        PopulateCharacters();
    }

    public override void _Process(double delta)
    {
        while (MuNetworkClient.Instance.TryDequeueEvent(out MuNetworkEvent networkEvent))
        {
            HandleNetworkEvent(networkEvent);
        }
    }

    private void PopulateCharacters()
    {
        _characterList.Clear();
        _selectedIndex = -1;
        _enterGameButton.Disabled = true;

        IReadOnlyList<MuCharacterInfo> characters = MuLoginSession.Characters;
        if (characters.Count == 0)
        {
            _statusLabel.Text = "No characters found on this account.";
            return;
        }

        foreach (MuCharacterInfo character in characters)
        {
            string line = $"{character.Name} | {character.Class} | Lv {character.Level}";
            _characterList.AddItem(line);
        }

        _characterList.Select(0);
        _selectedIndex = 0;
        _enterGameButton.Disabled = false;
        _statusLabel.Text = $"Character selection ({characters.Count})";
    }

    private void HandleNetworkEvent(MuNetworkEvent networkEvent)
    {
        switch (networkEvent.Kind)
        {
            case MuNetworkEventKind.EnteredGame:
                _statusLabel.Text = "Loading world...";
                GetTree().ChangeSceneToFile("res://Scenes/GameScene.tscn");
                break;
            case MuNetworkEventKind.Error:
                _statusLabel.Text = $"Error: {networkEvent.Message}";
                _enterGameButton.Disabled = false;
                break;
            case MuNetworkEventKind.StateChanged:
                if (networkEvent.State == ClientConnectionState.SelectingCharacter)
                {
                    _statusLabel.Text = "Loading character...";
                }

                break;
        }
    }

    private void OnCharacterSelected(long index)
    {
        _selectedIndex = (int)index;
        _enterGameButton.Disabled = _selectedIndex < 0;
    }

    private void OnCharacterActivated(long index)
    {
        _selectedIndex = (int)index;
        OnEnterGamePressed();
    }

    private void OnEnterGamePressed()
    {
        IReadOnlyList<MuCharacterInfo> characters = MuLoginSession.Characters;
        if (_selectedIndex < 0 || _selectedIndex >= characters.Count)
        {
            _statusLabel.Text = "Select a character.";
            return;
        }

        string selectedName = characters[_selectedIndex].Name;
        MuLoginSession.SelectedCharacterName = selectedName;
        MuLoginSession.SelectedCharacter = characters[_selectedIndex];
        _enterGameButton.Disabled = true;
        _statusLabel.Text = $"Connecting character: {selectedName}";
        _ = MuNetworkClient.Instance.SendSelectCharacterRequestAsync(selectedName);
    }
}
