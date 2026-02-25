using Godot;

namespace MuGodot.Networking;

public partial class OnlineWorldScene : Node
{
    private const int MaxWorldEventsPerFrame = 96;
    private const int MaxUiEventsPerFrame = 32;
    private const ulong MaxWorldEventBudgetUsec = 3_000;
    private const ulong SummaryRefreshIntervalMs = 120;
    private const ulong LastEventLabelUpdateIntervalMs = 80;

    [ExportGroup("Network Perf")]
    [Export] public bool ShowNetworkEntityLabels { get; set; } = false;

    private readonly Dictionary<ushort, MuWorldEntityKind> _entities = new();
    private readonly Dictionary<ushort, NetworkEntityVisual> _entityVisuals = new();
    private readonly Queue<MuWorldEvent> _deferredEntityEvents = new();

    private readonly CapsuleMesh _monsterMesh = new()
    {
        Radius = 0.32f,
        Height = 1.2f,
    };

    private readonly CylinderMesh _npcMesh = new()
    {
        TopRadius = 0.28f,
        BottomRadius = 0.34f,
        Height = 1.3f,
    };

    private readonly StandardMaterial3D _monsterMaterial = new()
    {
        Metallic = 0.08f,
        Roughness = 0.78f,
        AlbedoColor = new Color(0.8f, 0.22f, 0.18f),
    };

    private readonly StandardMaterial3D _npcMaterial = new()
    {
        Metallic = 0.08f,
        Roughness = 0.78f,
        AlbedoColor = new Color(0.2f, 0.55f, 0.88f),
    };

    private MuGodot.Main? _mainScene;
    private Node3D? _networkEntitiesRoot;
    private CanvasLayer? _overlayLayer;

    private Label _summaryLabel = null!;
    private Label _lastEventLabel = null!;

    private uint _currentHealth;
    private uint _maximumHealth;
    private uint _currentShield;
    private uint _maximumShield;
    private uint _currentMana;
    private uint _maximumMana;
    private uint _currentAbility;
    private uint _maximumAbility;
    private byte _currentMapId;

    private bool _mapSwitchRunning;
    private bool _hasPendingMapSwitch;
    private byte _pendingMapId;
    private byte _pendingX;
    private byte _pendingY;
    private byte _pendingDirection;

    private int _playerCount;
    private int _npcCount;
    private int _monsterCount;
    private int _itemCount;
    private bool _summaryDirty = true;
    private ulong _nextSummaryRefreshAtMs;
    private ulong _nextLastEventLabelUpdateAtMs;
    private bool _offlineMode;

    private sealed class NetworkEntityVisual
    {
        public MuWorldEntityKind Kind { get; init; }

        public Node3D Root { get; init; } = null!;

        public Label3D? Label { get; init; }
    }

    public override void _Ready()
    {
        _mainScene = GetNodeOrNull<MuGodot.Main>("Main");
        _offlineMode = MuLoginSession.OfflineTestMode;
        _currentMapId = MuLoginSession.CurrentMapId ?? 0;
        _networkEntitiesRoot = EnsureNetworkEntitiesRoot();
        BuildOverlay();

        if (_offlineMode)
        {
            ApplyOfflineMode();
        }

        RefreshSummary();
    }

    public override void _Process(double delta)
    {
        if (_offlineMode)
        {
            return;
        }

        ulong eventStartUsec = Time.GetTicksUsec();
        int processed = 0;
        while (processed < MaxWorldEventsPerFrame && MuNetworkClient.Instance.TryDequeueWorldEvent(out MuWorldEvent worldEvent))
        {
            HandleWorldEvent(worldEvent);
            processed++;
            if (Time.GetTicksUsec() - eventStartUsec >= MaxWorldEventBudgetUsec)
            {
                break;
            }
        }

        int uiEventsProcessed = 0;
        while (uiEventsProcessed < MaxUiEventsPerFrame && MuNetworkClient.Instance.TryDequeueEvent(out MuNetworkEvent networkEvent))
        {
            if (networkEvent.Kind == MuNetworkEventKind.Error)
            {
                SetLastEventText($"Network error: {networkEvent.Message}", immediate: true);
            }

            uiEventsProcessed++;
        }

        if (!_mapSwitchRunning && !_hasPendingMapSwitch && _deferredEntityEvents.Count > 0)
        {
            FlushDeferredEntityEvents(MaxWorldEventsPerFrame);
        }

        RefreshSummaryIfNeeded();
    }

    private void HandleWorldEvent(MuWorldEvent worldEvent)
    {
        if (ShouldDeferEntityEvent(worldEvent))
        {
            EnqueueDeferredEntityEvent(worldEvent);
            return;
        }

        if (TryHandleEntityEvent(worldEvent))
        {
            return;
        }

        switch (worldEvent.Kind)
        {
            case MuWorldEventKind.Chat:
                SetLastEventText($"[{worldEvent.ChatSender}] {worldEvent.ChatMessage}", immediate: true);
                break;
            case MuWorldEventKind.HeroStats:
                _currentHealth = worldEvent.CurrentHealth;
                _maximumHealth = worldEvent.MaximumHealth;
                _currentShield = worldEvent.CurrentShield;
                _maximumShield = worldEvent.MaximumShield;
                _currentMana = worldEvent.CurrentMana;
                _maximumMana = worldEvent.MaximumMana;
                _currentAbility = worldEvent.CurrentAbility;
                _maximumAbility = worldEvent.MaximumAbility;
                MarkSummaryDirty();
                break;
            case MuWorldEventKind.HeroPosition:
                if (_mainScene is not null)
                {
                    _mainScene.ApplyNetworkHeroPosition(worldEvent.X, worldEvent.Y, worldEvent.Direction);
                }

                SetLastEventText($"Hero pos ({worldEvent.X},{worldEvent.Y}) dir:{worldEvent.Direction}");
                break;
            case MuWorldEventKind.MapChanged:
                _currentMapId = worldEvent.MapId;
                _deferredEntityEvents.Clear();
                _entities.Clear();
                ResetEntityCounters();
                ClearNetworkEntities();
                RequestMapSwitch(worldEvent.MapId, worldEvent.X, worldEvent.Y, worldEvent.Direction);
                SetLastEventText($"Map {worldEvent.MapId} -> ({worldEvent.X},{worldEvent.Y})", immediate: true);
                MarkSummaryDirty();
                break;
        }
    }

    private bool ShouldDeferEntityEvent(MuWorldEvent worldEvent)
    {
        if (!_mapSwitchRunning && !_hasPendingMapSwitch)
        {
            return false;
        }

        return worldEvent.Kind is MuWorldEventKind.SpawnOrUpdate
            or MuWorldEventKind.Move
            or MuWorldEventKind.Remove
            or MuWorldEventKind.Animation
            or MuWorldEventKind.Hit
            or MuWorldEventKind.Kill;
    }

    private void EnqueueDeferredEntityEvent(MuWorldEvent worldEvent)
    {
        if (_deferredEntityEvents.Count >= 4096)
        {
            _deferredEntityEvents.Dequeue();
        }

        _deferredEntityEvents.Enqueue(worldEvent);
    }

    private void FlushDeferredEntityEvents(int maxToProcess)
    {
        int pending = Math.Min(maxToProcess, _deferredEntityEvents.Count);
        for (int i = 0; i < pending; i++)
        {
            MuWorldEvent deferred = _deferredEntityEvents.Dequeue();
            if (!TryHandleEntityEvent(deferred))
            {
                EnqueueDeferredEntityEvent(deferred);
                break;
            }
        }
    }

    private bool TryHandleEntityEvent(MuWorldEvent worldEvent)
    {
        switch (worldEvent.Kind)
        {
            case MuWorldEventKind.SpawnOrUpdate:
                SetEntityKind(worldEvent.Id, worldEvent.EntityKind);
                if (worldEvent.EntityKind is MuWorldEntityKind.Npc or MuWorldEntityKind.Monster)
                {
                    if (!UpsertNetworkEntity(worldEvent))
                    {
                        return false;
                    }
                }

                SetLastEventText($"Spawn {worldEvent.EntityKind} #{worldEvent.Id} ({worldEvent.X},{worldEvent.Y})");
                return true;
            case MuWorldEventKind.Remove:
                RemoveEntityKind(worldEvent.Id);
                RemoveNetworkEntity(worldEvent.Id);
                SetLastEventText($"Remove #{worldEvent.Id}");
                return true;
            case MuWorldEventKind.Move:
                MoveNetworkEntity(worldEvent.Id, worldEvent.X, worldEvent.Y, worldEvent.Direction);
                SetLastEventText($"Move #{worldEvent.Id} -> ({worldEvent.X},{worldEvent.Y})");
                return true;
            case MuWorldEventKind.Animation:
                MarkEntityAnimation(worldEvent.Id, worldEvent.SkillId);
                SetLastEventText($"Anim #{worldEvent.Id} skill:{worldEvent.SkillId} target:{worldEvent.TargetId}");
                return true;
            case MuWorldEventKind.Hit:
                MarkEntityHit(worldEvent.Id, worldEvent.HealthDamage);
                SetLastEventText($"Hit #{worldEvent.Id} dmg:{worldEvent.HealthDamage}/{worldEvent.ShieldDamage}");
                return true;
            case MuWorldEventKind.Kill:
                RemoveEntityKind(worldEvent.Id);
                RemoveNetworkEntity(worldEvent.Id);
                SetLastEventText($"Kill #{worldEvent.Id} by:{worldEvent.TargetId}");
                return true;
            default:
                return false;
        }
    }

    private void RequestMapSwitch(byte mapId, byte x, byte y, byte direction)
    {
        _hasPendingMapSwitch = true;
        _pendingMapId = mapId;
        _pendingX = x;
        _pendingY = y;
        _pendingDirection = direction;

        if (_mapSwitchRunning)
        {
            return;
        }

        _ = ProcessMapSwitchQueueAsync();
    }

    private async Task ProcessMapSwitchQueueAsync()
    {
        if (_mainScene is null)
        {
            return;
        }

        _mapSwitchRunning = true;
        try
        {
            while (_hasPendingMapSwitch)
            {
                byte mapId = _pendingMapId;
                byte x = _pendingX;
                byte y = _pendingY;
                byte direction = _pendingDirection;
                _hasPendingMapSwitch = false;

                await _mainScene.SwitchToNetworkLocationAsync(mapId, x, y, direction);
                _networkEntitiesRoot = EnsureNetworkEntitiesRoot();
                await MuNetworkClient.Instance.SendClientReadyAfterMapChangeAsync();
            }

            FlushDeferredEntityEvents(MaxWorldEventsPerFrame);
        }
        catch (Exception ex)
        {
            SetLastEventText($"Map error: {ex.Message}", immediate: true);
        }
        finally
        {
            _mapSwitchRunning = false;
        }
    }

    private void MarkSummaryDirty()
    {
        _summaryDirty = true;
    }

    private void RefreshSummaryIfNeeded()
    {
        if (!_summaryDirty)
        {
            return;
        }

        ulong nowMs = Time.GetTicksMsec();
        if (nowMs < _nextSummaryRefreshAtMs)
        {
            return;
        }

        RefreshSummary();
        _summaryDirty = false;
        _nextSummaryRefreshAtMs = nowMs + SummaryRefreshIntervalMs;
    }

    private void RefreshSummary()
    {
        _summaryLabel.Text =
            $"Online | {MuLoginSession.SelectedCharacterName}\n" +
            $"Map: {_currentMapId}  " +
            $"Players: {_playerCount} | Npc: {_npcCount} | Monsters: {_monsterCount} | Drops: {_itemCount}\n" +
            $"HP {_currentHealth}/{_maximumHealth}  SD {_currentShield}/{_maximumShield}  " +
            $"MP {_currentMana}/{_maximumMana}  AG {_currentAbility}/{_maximumAbility}";
    }

    private void BuildOverlay()
    {
        _overlayLayer = new CanvasLayer();
        AddChild(_overlayLayer);

        var margin = new MarginContainer();
        margin.OffsetLeft = 10;
        margin.OffsetTop = 10;
        margin.OffsetRight = 10;
        margin.OffsetBottom = 10;
        _overlayLayer.AddChild(margin);

        var panel = new PanelContainer();
        margin.AddChild(panel);

        var content = new VBoxContainer();
        content.CustomMinimumSize = new Vector2(560, 0);
        content.AddThemeConstantOverride("separation", 6);
        panel.AddChild(content);

        _summaryLabel = new Label();
        _summaryLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        content.AddChild(_summaryLabel);

        _lastEventLabel = new Label();
        _lastEventLabel.Text = "Waiting for packets...";
        _lastEventLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        content.AddChild(_lastEventLabel);
    }

    private void ApplyOfflineMode()
    {
        if (_overlayLayer != null && GodotObject.IsInstanceValid(_overlayLayer))
        {
            _overlayLayer.Visible = false;
        }

        if (_mainScene == null || !GodotObject.IsInstanceValid(_mainScene))
        {
            return;
        }

        _mainScene.EnableEquipmentDebugUi = true;
        _mainScene.CallDeferred(nameof(MuGodot.Main.SetEquipmentDebugUiVisible), true);
    }

    private void SetLastEventText(string text, bool immediate = false)
    {
        if (_lastEventLabel == null || !GodotObject.IsInstanceValid(_lastEventLabel))
        {
            return;
        }

        ulong nowMs = Time.GetTicksMsec();
        if (!immediate && nowMs < _nextLastEventLabelUpdateAtMs)
        {
            return;
        }

        _lastEventLabel.Text = text;
        _nextLastEventLabelUpdateAtMs = nowMs + LastEventLabelUpdateIntervalMs;
    }

    private void SetEntityKind(ushort id, MuWorldEntityKind kind)
    {
        if (_entities.TryGetValue(id, out MuWorldEntityKind previousKind))
        {
            if (previousKind == kind)
            {
                return;
            }

            DecrementEntityCounter(previousKind);
            _entities[id] = kind;
            IncrementEntityCounter(kind);
            MarkSummaryDirty();
            return;
        }

        _entities[id] = kind;
        IncrementEntityCounter(kind);
        MarkSummaryDirty();
    }

    private void RemoveEntityKind(ushort id)
    {
        if (!_entities.Remove(id, out MuWorldEntityKind kind))
        {
            return;
        }

        DecrementEntityCounter(kind);
        MarkSummaryDirty();
    }

    private void ResetEntityCounters()
    {
        _playerCount = 0;
        _npcCount = 0;
        _monsterCount = 0;
        _itemCount = 0;
        MarkSummaryDirty();
    }

    private void IncrementEntityCounter(MuWorldEntityKind kind)
    {
        switch (kind)
        {
            case MuWorldEntityKind.Player:
                _playerCount++;
                break;
            case MuWorldEntityKind.Npc:
                _npcCount++;
                break;
            case MuWorldEntityKind.Monster:
                _monsterCount++;
                break;
            case MuWorldEntityKind.Item:
            case MuWorldEntityKind.Money:
                _itemCount++;
                break;
        }
    }

    private void DecrementEntityCounter(MuWorldEntityKind kind)
    {
        switch (kind)
        {
            case MuWorldEntityKind.Player:
                _playerCount = Math.Max(0, _playerCount - 1);
                break;
            case MuWorldEntityKind.Npc:
                _npcCount = Math.Max(0, _npcCount - 1);
                break;
            case MuWorldEntityKind.Monster:
                _monsterCount = Math.Max(0, _monsterCount - 1);
                break;
            case MuWorldEntityKind.Item:
            case MuWorldEntityKind.Money:
                _itemCount = Math.Max(0, _itemCount - 1);
                break;
        }
    }

    private Node3D? EnsureNetworkEntitiesRoot()
    {
        if (_mainScene is null)
        {
            return null;
        }

        Node3D? charactersRoot = _mainScene.GetCharactersRoot();
        if (charactersRoot is null)
        {
            return null;
        }

        if (_networkEntitiesRoot != null &&
            GodotObject.IsInstanceValid(_networkEntitiesRoot) &&
            _networkEntitiesRoot.GetParent() == charactersRoot)
        {
            return _networkEntitiesRoot;
        }

        Node3D? existing = charactersRoot.GetNodeOrNull<Node3D>("NetworkEntities");
        if (existing is not null && GodotObject.IsInstanceValid(existing))
        {
            _networkEntitiesRoot = existing;
            return existing;
        }

        var created = new Node3D { Name = "NetworkEntities" };
        charactersRoot.AddChild(created);
        _networkEntitiesRoot = created;
        return created;
    }

    private bool UpsertNetworkEntity(MuWorldEvent worldEvent)
    {
        Node3D? root = EnsureNetworkEntitiesRoot();
        if (root is null)
        {
            return false;
        }

        if (!_entityVisuals.TryGetValue(worldEvent.Id, out NetworkEntityVisual? visual) ||
            !GodotObject.IsInstanceValid(visual.Root))
        {
            visual = CreateNetworkEntityVisual(worldEvent.EntityKind, worldEvent.Id, worldEvent.Name, worldEvent.TypeNumber);
            root.AddChild(visual.Root);
            _entityVisuals[worldEvent.Id] = visual;
        }

        PlaceEntity(visual.Root, worldEvent.X, worldEvent.Y, worldEvent.Direction, worldEvent.EntityKind);

        if (visual.Label != null && !string.IsNullOrWhiteSpace(worldEvent.Name))
        {
            visual.Label.Text = worldEvent.Name;
        }

        return true;
    }

    private void MoveNetworkEntity(ushort id, byte x, byte y, byte direction)
    {
        if (_entityVisuals.TryGetValue(id, out NetworkEntityVisual? visual) && GodotObject.IsInstanceValid(visual.Root))
        {
            PlaceEntity(visual.Root, x, y, direction, visual.Kind);
        }
    }

    private void RemoveNetworkEntity(ushort id)
    {
        if (!_entityVisuals.TryGetValue(id, out NetworkEntityVisual? visual))
        {
            return;
        }

        _entityVisuals.Remove(id);
        if (GodotObject.IsInstanceValid(visual.Root))
        {
            visual.Root.QueueFree();
        }
    }

    private void ClearNetworkEntities()
    {
        foreach ((_, NetworkEntityVisual visual) in _entityVisuals)
        {
            if (GodotObject.IsInstanceValid(visual.Root))
            {
                visual.Root.QueueFree();
            }
        }

        _entityVisuals.Clear();
    }

    private void PlaceEntity(Node3D node, byte x, byte y, byte direction, MuWorldEntityKind kind)
    {
        if (_mainScene is null)
        {
            return;
        }

        float offset = kind == MuWorldEntityKind.Monster ? 0.85f : 0.75f;
        node.Position = _mainScene.NetworkTileToWorld(x, y, offset);
        var rot = node.Rotation;
        rot.Y = DirectionToYaw(direction);
        node.Rotation = rot;
    }

    private static float DirectionToYaw(byte direction)
    {
        return Mathf.DegToRad((direction & 0x0F) * (360f / 16f));
    }

    private NetworkEntityVisual CreateNetworkEntityVisual(MuWorldEntityKind kind, ushort id, string name, ushort typeNumber)
    {
        var root = new Node3D
        {
            Name = $"{kind}_{id}",
        };

        var mesh = new MeshInstance3D
        {
            Name = "Mesh",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

        if (kind == MuWorldEntityKind.Monster)
        {
            mesh.Mesh = _monsterMesh;
            mesh.MaterialOverride = _monsterMaterial;
        }
        else
        {
            mesh.Mesh = _npcMesh;
            mesh.MaterialOverride = _npcMaterial;
        }
        root.AddChild(mesh);

        Label3D? label = null;
        if (ShowNetworkEntityLabels)
        {
            label = new Label3D
            {
                Name = "Name",
                Text = !string.IsNullOrWhiteSpace(name) ? name : $"{kind} {typeNumber}",
                Position = new Vector3(0f, 1.4f, 0f),
                FontSize = 18,
                Modulate = new Color(1f, 1f, 0.92f),
                OutlineSize = 4,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
            };
            root.AddChild(label);
        }

        return new NetworkEntityVisual
        {
            Kind = kind,
            Root = root,
            Label = label,
        };
    }

    private void MarkEntityAnimation(ushort id, ushort skillId)
    {
        if (_entityVisuals.TryGetValue(id, out NetworkEntityVisual? visual) &&
            visual.Label != null &&
            GodotObject.IsInstanceValid(visual.Label))
        {
            visual.Label.Text = $"{visual.Kind} #{id}  skill:{skillId}";
        }
    }

    private void MarkEntityHit(ushort id, int damage)
    {
        if (_entityVisuals.TryGetValue(id, out NetworkEntityVisual? visual) &&
            visual.Label != null &&
            GodotObject.IsInstanceValid(visual.Label))
        {
            visual.Label.Text = $"{visual.Kind} #{id}  -{Math.Abs(damage)}";
        }
    }
}
