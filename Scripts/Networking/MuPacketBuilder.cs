using System.Buffers;
using System.Text;
using MUnique.OpenMU.Network.Packets.ClientToServer;

namespace MuGodot.Networking;

internal static class MuPacketBuilder
{
    public static int BuildLoginPacket(
        IBufferWriter<byte> writer,
        string username,
        string password,
        byte[] clientVersion,
        byte[] clientSerial,
        byte[] xor3Keys)
    {
        int length = LoginLongPassword.Length;
        var memory = writer.GetMemory(length).Slice(0, length);
        var packet = new LoginLongPassword(memory);

        Span<byte> userSpan = stackalloc byte[packet.Username.Length];
        Span<byte> passSpan = stackalloc byte[packet.Password.Length];

        Encoding.ASCII.GetBytes(username, userSpan);
        Encoding.ASCII.GetBytes(password, passSpan);

        userSpan.CopyTo(packet.Username);
        passSpan.CopyTo(packet.Password);

        EncryptXor3(packet.Username, xor3Keys);
        EncryptXor3(packet.Password, xor3Keys);

        packet.TickCount = (uint)Environment.TickCount;
        clientVersion.CopyTo(packet.ClientVersion);
        clientSerial.CopyTo(packet.ClientSerial);

        return length;
    }

    public static int BuildRequestCharacterListPacket(IBufferWriter<byte> writer)
    {
        int length = RequestCharacterList.Length;
        var memory = writer.GetMemory(length).Slice(0, length);
        _ = new RequestCharacterList(memory);
        return length;
    }

	public static int BuildSelectCharacterPacket(IBufferWriter<byte> writer, string characterName)
	{
		int length = SelectCharacter.Length;
		var memory = writer.GetMemory(length).Slice(0, length);
		var packet = new SelectCharacter(memory);
		packet.Name = characterName;
		return length;
	}

	public static int BuildClientReadyAfterMapChangePacket(IBufferWriter<byte> writer)
	{
		int length = ClientReadyAfterMapChange.Length;
		var memory = writer.GetMemory(length).Slice(0, length);
		_ = new ClientReadyAfterMapChange(memory);
		return length;
	}

	public static int BuildWalkRequestPacket(IBufferWriter<byte> writer, byte startX, byte startY, byte[] path)
	{
		if (path.Length == 0)
		{
			return 0;
		}

		int stepsBytes = (path.Length + 1) / 2;
		int length = WalkRequest.GetRequiredSize(stepsBytes);
		var memory = writer.GetMemory(length).Slice(0, length);
		var packet = new WalkRequest(memory);

		packet.SourceX = startX;
		packet.SourceY = startY;
		packet.StepCount = (byte)path.Length;
		packet.TargetRotation = (byte)(path[path.Length - 1] & 0x0F);

		var directions = packet.Directions;
		int idx = 0;
		for (int i = 0; i < stepsBytes; i++)
		{
			byte high = idx < path.Length ? (byte)(path[idx++] & 0x0F) : (byte)0x0F;
			byte low = idx < path.Length ? (byte)(path[idx++] & 0x0F) : (byte)0x0F;
			directions[i] = (byte)((high << 4) | low);
		}

		return length;
	}

    private static void EncryptXor3(Span<byte> data, ReadOnlySpan<byte> keys)
    {
        if (keys.Length == 0)
        {
            return;
        }

        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= keys[i % keys.Length];
        }
    }
}
