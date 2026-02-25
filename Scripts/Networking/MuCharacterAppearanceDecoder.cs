using MUnique.OpenMU.Network.Packets;

namespace MuGodot.Networking;

public sealed class MuCharacterSpawnSetup
{
    public int ClassModelId { get; init; } = 1;

    public int ArmorSetId { get; init; } = -1;

    public int ItemLevel { get; init; } = 0;

    public bool IsExcellent { get; init; }

    public bool IsAncient { get; init; }

    public int LeftHandGroup { get; init; } = -1;

    public int LeftHandId { get; init; } = -1;

    public int RightHandGroup { get; init; } = -1;

    public int RightHandId { get; init; } = -1;

    public int WingId { get; init; } = -1;
}

public static class MuCharacterAppearanceDecoder
{
    private const short CapeOfLordItemIndex = 30;

    public static int GetClassModelId(CharacterClassNumber cls)
    {
        return cls switch
        {
            CharacterClassNumber.DarkWizard
            or CharacterClassNumber.SoulMaster
            or CharacterClassNumber.GrandMaster => 1,

            CharacterClassNumber.DarkKnight
            or CharacterClassNumber.BladeKnight
            or CharacterClassNumber.BladeMaster => 2,

            CharacterClassNumber.FairyElf
            or CharacterClassNumber.MuseElf
            or CharacterClassNumber.HighElf => 3,

            CharacterClassNumber.MagicGladiator
            or CharacterClassNumber.DuelMaster => 4,

            CharacterClassNumber.DarkLord
            or CharacterClassNumber.LordEmperor => 5,

            CharacterClassNumber.Summoner
            or CharacterClassNumber.BloodySummoner
            or CharacterClassNumber.DimensionMaster => 6,

            CharacterClassNumber.RageFighter
            or CharacterClassNumber.FistMaster => 7,

            _ => 1,
        };
    }

    public static bool TryCreateSpawnSetup(MuCharacterInfo? character, out MuCharacterSpawnSetup setup)
    {
        if (character is null)
        {
            setup = new MuCharacterSpawnSetup();
            return false;
        }

        setup = CreateSpawnSetup(character);
        return true;
    }

    private static MuCharacterSpawnSetup CreateSpawnSetup(MuCharacterInfo character)
    {
        var appearance = character.Appearance.AsSpan();
        bool isExtended = appearance.Length >= 25;

        int classModelId = GetClassModelId(character.Class);

        SlotData right = ParseRightHand(appearance, isExtended);
        SlotData left = ParseLeftHand(appearance, isExtended);
        SlotData helm = ParseHelm(appearance, isExtended);
        SlotData armor = ParseArmor(appearance, isExtended);
        SlotData pants = ParsePants(appearance, isExtended);
        SlotData gloves = ParseGloves(appearance, isExtended);
        SlotData boots = ParseBoots(appearance, isExtended);

        int armorSetId = ResolveArmorSetId(armor.Index, pants.Index, gloves.Index, boots.Index, helm.Index);
        int wingId = ParseWingId(appearance, isExtended, character.Class);

        int itemLevel = Max(
            left.Level,
            right.Level,
            helm.Level,
            armor.Level,
            pants.Level,
            gloves.Level,
            boots.Level);

        bool isExcellent = left.Excellent || right.Excellent || helm.Excellent || armor.Excellent
            || pants.Excellent || gloves.Excellent || boots.Excellent;

        bool isAncient = left.Ancient || right.Ancient || helm.Ancient || armor.Ancient
            || pants.Ancient || gloves.Ancient || boots.Ancient
            || (!isExtended && appearance.Length > 11 && (appearance[11] & 0x1) == 1);

        return new MuCharacterSpawnSetup
        {
            ClassModelId = classModelId,
            ArmorSetId = armorSetId,
            ItemLevel = itemLevel,
            IsExcellent = isExcellent,
            IsAncient = isAncient,
            LeftHandGroup = IsValidItem(left.Index) ? left.Group : -1,
            LeftHandId = IsValidItem(left.Index) ? left.Index : -1,
            RightHandGroup = IsValidItem(right.Index) ? right.Group : -1,
            RightHandId = IsValidItem(right.Index) ? right.Index : -1,
            WingId = wingId,
        };
    }

    private static int ParseWingId(ReadOnlySpan<byte> appearance, bool isExtended, CharacterClassNumber cls)
    {
        if (isExtended)
        {
            (short index, byte group) = ParseExtendedWings(appearance, 21);
            return group == 12 && index >= 0 ? index : -1;
        }

        if (appearance.Length < 10)
        {
            return -1;
        }

        byte wingLevel = (byte)((appearance[5] >> 2) & 0x3);
        byte wingType = (byte)(appearance[9] & 0x7);
        if (wingLevel == 0 || wingType == 0)
        {
            return -1;
        }

        short? mapped = TryMapWingAppearanceToItemIndex(wingLevel, wingType, cls);
        return mapped is { } id ? id : -1;
    }

    private static short? TryMapWingAppearanceToItemIndex(byte wingLevel, byte wingType, CharacterClassNumber cls)
    {
        if (wingLevel == 0 || wingType == 0)
        {
            return null;
        }

        if (IsDarkLordClass(cls) && wingLevel == 2 && wingType == 5)
        {
            return CapeOfLordItemIndex;
        }

        short[] tierIds = wingLevel switch
        {
            1 => [0, 1, 2, 3, 4, 5, 6],
            2 => [3, 4, 5, 6, 42],
            3 => [36, 37, 38, 39, 40, 41, 43],
            _ => [],
        };

        int index = wingType - 1;
        if (index < 0 || index >= tierIds.Length)
        {
            return null;
        }

        return tierIds[index];
    }

    private static bool IsDarkLordClass(CharacterClassNumber cls)
    {
        return cls is CharacterClassNumber.DarkLord or CharacterClassNumber.LordEmperor;
    }

    private static int ResolveArmorSetId(params int[] ids)
    {
        for (int i = 0; i < ids.Length; i++)
        {
            if (IsValidArmorItem(ids[i]))
            {
                return ids[i];
            }
        }

        return -1;
    }

    private static bool IsValidArmorItem(int index) => index >= 0 && index < 0x1FF;

    private static bool IsValidItem(int index) => index >= 0 && index != 0xFF && index != 0x1FF;

    private static SlotData ParseRightHand(ReadOnlySpan<byte> appearance, bool isExtended)
    {
        if (isExtended)
        {
            return ParseExtendedSlot(appearance, 0);
        }

        int index = appearance.Length > 2 ? appearance[2] : 0xFF;
        int group = appearance.Length > 13 ? (appearance[13] >> 5) & 0x07 : 0x07;
        return new SlotData(index, group, GetItemLevel18(appearance, 1), GetFlag18(appearance, 10, 1), GetFlag18(appearance, 11, 1));
    }

    private static SlotData ParseLeftHand(ReadOnlySpan<byte> appearance, bool isExtended)
    {
        if (isExtended)
        {
            return ParseExtendedSlot(appearance, 3);
        }

        int index = appearance.Length > 1 ? appearance[1] : 0xFF;
        int group = appearance.Length > 12 ? (appearance[12] >> 5) & 0x07 : 0x07;
        return new SlotData(index, group, GetItemLevel18(appearance, 0), GetFlag18(appearance, 10, 2), GetFlag18(appearance, 11, 2));
    }

    private static SlotData ParseHelm(ReadOnlySpan<byte> appearance, bool isExtended)
    {
        if (isExtended)
        {
            return ParseExtendedSlot(appearance, 6);
        }

        int index = 0xFF;
        if (appearance.Length >= 14)
        {
            int lower4 = (appearance[3] >> 4) & 0x0F;
            int bit5 = (appearance[9] >> 7) & 0x01;
            int upper4 = appearance[13] & 0x0F;
            index = lower4 | (bit5 << 4) | (upper4 << 5);
        }

        return new SlotData(index, 7, GetItemLevel18(appearance, 2), GetFlag18(appearance, 10, 7), GetFlag18(appearance, 11, 7));
    }

    private static SlotData ParseArmor(ReadOnlySpan<byte> appearance, bool isExtended)
    {
        if (isExtended)
        {
            return ParseExtendedSlot(appearance, 9);
        }

        int index = 0xFF;
        if (appearance.Length >= 15)
        {
            int lower4 = appearance[3] & 0x0F;
            int bit5 = (appearance[9] >> 6) & 0x01;
            int upper4 = (appearance[14] >> 4) & 0x0F;
            index = lower4 | (bit5 << 4) | (upper4 << 5);
        }

        return new SlotData(index, 8, GetItemLevel18(appearance, 3), GetFlag18(appearance, 10, 6), GetFlag18(appearance, 11, 6));
    }

    private static SlotData ParsePants(ReadOnlySpan<byte> appearance, bool isExtended)
    {
        if (isExtended)
        {
            return ParseExtendedSlot(appearance, 12);
        }

        int index = 0xFF;
        if (appearance.Length >= 15)
        {
            int lower4 = (appearance[4] >> 4) & 0x0F;
            int bit5 = (appearance[9] >> 5) & 0x01;
            int upper4 = appearance[14] & 0x0F;
            index = lower4 | (bit5 << 4) | (upper4 << 5);
        }

        return new SlotData(index, 9, GetItemLevel18(appearance, 4), GetFlag18(appearance, 10, 5), GetFlag18(appearance, 11, 5));
    }

    private static SlotData ParseGloves(ReadOnlySpan<byte> appearance, bool isExtended)
    {
        if (isExtended)
        {
            return ParseExtendedSlot(appearance, 15);
        }

        int index = 0xFF;
        if (appearance.Length >= 16)
        {
            int lower4 = appearance[4] & 0x0F;
            int bit5 = (appearance[9] >> 4) & 0x01;
            int upper4 = (appearance[15] >> 4) & 0x0F;
            index = lower4 | (bit5 << 4) | (upper4 << 5);
        }

        return new SlotData(index, 10, GetItemLevel18(appearance, 5), GetFlag18(appearance, 10, 4), GetFlag18(appearance, 11, 4));
    }

    private static SlotData ParseBoots(ReadOnlySpan<byte> appearance, bool isExtended)
    {
        if (isExtended)
        {
            return ParseExtendedSlot(appearance, 18);
        }

        int index = 0xFF;
        if (appearance.Length >= 16)
        {
            int lower4 = (appearance[5] >> 4) & 0x0F;
            int bit5 = (appearance[9] >> 3) & 0x01;
            int upper4 = appearance[15] & 0x0F;
            index = lower4 | (bit5 << 4) | (upper4 << 5);
        }

        return new SlotData(index, 11, GetItemLevel18(appearance, 6), GetFlag18(appearance, 10, 3), GetFlag18(appearance, 11, 3));
    }

    private static (short Index, byte Group) ParseExtendedWings(ReadOnlySpan<byte> data, int offset)
    {
        if (data.Length < offset + 2)
        {
            return (-1, 0xFF);
        }

        byte b0 = data[offset];
        byte b1 = data[offset + 1];
        if (b0 == 0xFF && b1 == 0xFF)
        {
            return (-1, 0xFF);
        }

        short itemNumber = (short)(b1 + ((b0 & 0x0F) << 4));
        byte group = (byte)((b0 >> 4) & 0x0F);
        return (itemNumber, group);
    }

    private static SlotData ParseExtendedSlot(ReadOnlySpan<byte> data, int offset)
    {
        if (data.Length < offset + 3)
        {
            return new SlotData(-1, -1, 0, false, false);
        }

        byte b0 = data[offset];
        byte b1 = data[offset + 1];
        byte b2 = data[offset + 2];

        if (b0 == 0xFF && b1 == 0xFF)
        {
            return new SlotData(-1, -1, 0, false, false);
        }

        int itemNumber = b1 | ((b0 & 0x0F) << 8);
        int group = (b0 >> 4) & 0x0F;

        bool ancient = (b2 & 0x04) != 0;
        bool excellent = (b2 & 0x08) != 0;
        byte glowLevel = (byte)((b2 >> 4) & 0x0F);

        return new SlotData(itemNumber, group, ConvertGlowToItemLevel(glowLevel), excellent, ancient);
    }

    private static int GetItemLevel18(ReadOnlySpan<byte> appearance, int slotIndex)
    {
        if (appearance.Length < 9)
        {
            return 0;
        }

        int levelIndex = (appearance[6] << 16) | (appearance[7] << 8) | appearance[8];
        byte glow = (byte)((levelIndex >> (slotIndex * 3)) & 0x07);
        return ConvertGlowToItemLevel(glow);
    }

    private static bool GetFlag18(ReadOnlySpan<byte> appearance, int byteIndex, int bitIndex)
    {
        if (appearance.Length <= byteIndex || bitIndex < 0 || bitIndex > 7)
        {
            return false;
        }

        return ((appearance[byteIndex] >> bitIndex) & 0x01) == 1;
    }

    private static int ConvertGlowToItemLevel(byte glow)
    {
        return glow switch
        {
            0 => 0,
            1 => 3,
            2 => 5,
            3 => 7,
            4 => 9,
            5 => 11,
            6 => 13,
            7 => 15,
            _ => 0,
        };
    }

    private static int Max(params int[] values)
    {
        int max = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] > max)
            {
                max = values[i];
            }
        }

        return max;
    }

    private readonly record struct SlotData(int Index, int Group, int Level, bool Excellent, bool Ancient);
}
