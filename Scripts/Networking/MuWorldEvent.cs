using MUnique.OpenMU.Network.Packets.ServerToClient;

namespace MuGodot.Networking;

public enum MuWorldEntityKind
{
    Player,
    Npc,
    Monster,
    Item,
    Money,
}

public enum MuWorldEventKind
{
    SpawnOrUpdate,
    Move,
    Remove,
    Animation,
    Hit,
    Kill,
    MapChanged,
    HeroPosition,
    HeroStats,
    Chat,
}

public sealed class MuWorldEvent
{
    public MuWorldEventKind Kind { get; init; }

    public MuWorldEntityKind EntityKind { get; init; }

    public ushort Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public ushort TypeNumber { get; init; }

    public byte X { get; init; }

    public byte Y { get; init; }

    public byte Direction { get; init; }

    public byte MapId { get; init; }

    public bool IsSelf { get; init; }

    public int HealthDamage { get; init; }

    public int ShieldDamage { get; init; }

    public DamageKind DamageKind { get; init; }

    public ushort TargetId { get; init; }

    public ushort SkillId { get; init; }

    public uint MoneyAmount { get; init; }

    public float? HealthFraction { get; init; }

    public float? ShieldFraction { get; init; }

    public uint CurrentHealth { get; init; }

    public uint MaximumHealth { get; init; }

    public uint CurrentShield { get; init; }

    public uint MaximumShield { get; init; }

    public uint CurrentMana { get; init; }

    public uint MaximumMana { get; init; }

    public uint CurrentAbility { get; init; }

    public uint MaximumAbility { get; init; }

    public string ChatSender { get; init; } = string.Empty;

    public string ChatMessage { get; init; } = string.Empty;

    public static MuWorldEvent Spawn(
        MuWorldEntityKind kind,
        ushort id,
        byte x,
        byte y,
        byte direction,
        ushort type = 0,
        string name = "",
        bool isSelf = false,
        uint moneyAmount = 0)
        => new()
        {
            Kind = MuWorldEventKind.SpawnOrUpdate,
            EntityKind = kind,
            Id = id,
            X = x,
            Y = y,
            Direction = direction,
            TypeNumber = type,
            Name = name,
            IsSelf = isSelf,
            MoneyAmount = moneyAmount,
        };

    public static MuWorldEvent Move(ushort id, byte x, byte y, byte direction)
        => new()
        {
            Kind = MuWorldEventKind.Move,
            Id = id,
            X = x,
            Y = y,
            Direction = direction,
        };

    public static MuWorldEvent Remove(ushort id)
        => new()
        {
            Kind = MuWorldEventKind.Remove,
            Id = id,
        };

    public static MuWorldEvent Animation(ushort id, ushort targetId, byte direction, ushort skillId)
        => new()
        {
            Kind = MuWorldEventKind.Animation,
            Id = id,
            TargetId = targetId,
            Direction = direction,
            SkillId = skillId,
        };

    public static MuWorldEvent Hit(ushort id, int healthDamage, int shieldDamage, DamageKind damageKind, float? healthFraction, float? shieldFraction)
        => new()
        {
            Kind = MuWorldEventKind.Hit,
            Id = id,
            HealthDamage = healthDamage,
            ShieldDamage = shieldDamage,
            DamageKind = damageKind,
            HealthFraction = healthFraction,
            ShieldFraction = shieldFraction,
        };

    public static MuWorldEvent Kill(ushort id, ushort killerId, ushort skillId)
        => new()
        {
            Kind = MuWorldEventKind.Kill,
            Id = id,
            TargetId = killerId,
            SkillId = skillId,
        };

    public static MuWorldEvent HeroPosition(byte x, byte y, byte direction)
        => new()
        {
            Kind = MuWorldEventKind.HeroPosition,
            IsSelf = true,
            X = x,
            Y = y,
            Direction = direction,
        };

    public static MuWorldEvent MapChanged(byte mapId, byte x, byte y, byte direction)
        => new()
        {
            Kind = MuWorldEventKind.MapChanged,
            IsSelf = true,
            MapId = mapId,
            X = x,
            Y = y,
            Direction = direction,
        };

    public static MuWorldEvent HeroStats(
        uint currentHealth,
        uint maxHealth,
        uint currentShield,
        uint maxShield,
        uint currentMana,
        uint maxMana,
        uint currentAbility,
        uint maxAbility)
        => new()
        {
            Kind = MuWorldEventKind.HeroStats,
            IsSelf = true,
            CurrentHealth = currentHealth,
            MaximumHealth = maxHealth,
            CurrentShield = currentShield,
            MaximumShield = maxShield,
            CurrentMana = currentMana,
            MaximumMana = maxMana,
            CurrentAbility = currentAbility,
            MaximumAbility = maxAbility,
        };

    public static MuWorldEvent Chat(string sender, string message)
        => new()
        {
            Kind = MuWorldEventKind.Chat,
            ChatSender = sender,
            ChatMessage = message,
        };
}
