using System.Buffers.Binary;
using Godot;

namespace MuGodot.Audio;

public static class MuAudioLoader
{
    public static AudioStream? LoadFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            return null;

        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp3" => LoadMp3(path),
            ".wav" => LoadWav(path),
            _ => null,
        };
    }

    private static AudioStream? LoadMp3(string path)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            var stream = new AudioStreamMP3
            {
                Data = bytes,
            };

            return stream;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Audio] Failed to load MP3 '{path}': {ex.Message}");
            return null;
        }
    }

    private static AudioStream? LoadWav(string path)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            if (bytes.Length < 44)
                return null;

            if (!MatchAscii(bytes, 0, "RIFF") || !MatchAscii(bytes, 8, "WAVE"))
                return null;

            int channels = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;
            int dataOffset = -1;
            int dataSize = 0;

            int offset = 12;
            while (offset + 8 <= bytes.Length)
            {
                int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4));
                int chunkDataOffset = offset + 8;
                if (chunkDataOffset + chunkSize > bytes.Length)
                    break;

                if (MatchAscii(bytes, offset, "fmt "))
                {
                    if (chunkSize < 16)
                        return null;

                    ushort format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkDataOffset + 0));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkDataOffset + 2));
                    sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(chunkDataOffset + 4));
                    bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkDataOffset + 14));

                    // 1 = PCM (we keep parser small and deterministic).
                    if (format != 1)
                        return null;
                }
                else if (MatchAscii(bytes, offset, "data"))
                {
                    dataOffset = chunkDataOffset;
                    dataSize = chunkSize;
                }

                // Chunks are word-aligned in RIFF.
                offset = chunkDataOffset + chunkSize + (chunkSize & 1);
            }

            if (channels <= 0 || sampleRate <= 0 || dataOffset < 0 || dataSize <= 0)
                return null;

            var pcm = new byte[dataSize];
            Buffer.BlockCopy(bytes, dataOffset, pcm, 0, dataSize);

            var stream = new AudioStreamWav
            {
                Data = pcm,
                MixRate = sampleRate,
                Stereo = channels > 1,
            };

            stream.Format = bitsPerSample switch
            {
                8 => AudioStreamWav.FormatEnum.Format8Bits,
                16 => AudioStreamWav.FormatEnum.Format16Bits,
                _ => AudioStreamWav.FormatEnum.Format16Bits,
            };

            return stream;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Audio] Failed to load WAV '{path}': {ex.Message}");
            return null;
        }
    }

    private static bool MatchAscii(byte[] data, int offset, string value)
    {
        if (offset < 0 || offset + value.Length > data.Length)
            return false;

        for (int i = 0; i < value.Length; i++)
        {
            if (data[offset + i] != value[i])
                return false;
        }

        return true;
    }
}
