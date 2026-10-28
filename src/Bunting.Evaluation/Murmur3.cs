using System.Buffers.Binary;
using System.Numerics;

namespace Bunting.Evaluation;

/// <summary>MurmurHash3 x86 32-bit (Austin Appleby, public domain).</summary>
public static class Murmur3
{
    private const uint C1 = 0xcc9e2d51;
    private const uint C2 = 0x1b873593;

    public static uint Hash32(ReadOnlySpan<byte> data, uint seed = 0)
    {
        var h = seed;
        var blocks = data.Length / 4;

        for (var i = 0; i < blocks; i++)
        {
            var k = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
            k *= C1;
            k = BitOperations.RotateLeft(k, 15);
            k *= C2;
            h ^= k;
            h = BitOperations.RotateLeft(h, 13);
            h = (h * 5) + 0xe6546b64;
        }

        var tail = data[(blocks * 4)..];
        uint k1 = 0;
        if (tail.Length >= 3)
        {
            k1 ^= (uint)tail[2] << 16;
        }

        if (tail.Length >= 2)
        {
            k1 ^= (uint)tail[1] << 8;
        }

        if (tail.Length >= 1)
        {
            k1 ^= tail[0];
            k1 *= C1;
            k1 = BitOperations.RotateLeft(k1, 15);
            k1 *= C2;
            h ^= k1;
        }

        h ^= (uint)data.Length;
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        h *= 0xc2b2ae35;
        h ^= h >> 16;
        return h;
    }
}
