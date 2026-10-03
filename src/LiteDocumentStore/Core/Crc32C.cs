using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace LiteDocumentStore;

/// <summary>
/// CRC-32C (Castagnoli) over UTF-8 text, for the store's two non-cryptographic digests: the
/// auto-derived index-name suffix and <see cref="SqlMigration.Checksum"/>.
/// </summary>
/// <remarks>
/// Neither digest is a security boundary — one separates names, the other detects an edited
/// migration — so neither needs a cryptographic hash. <c>SHA256</c> was used before and, on
/// Linux, routes through OpenSSL: the process <c>dlopen</c>s <c>libssl</c> on the first hash, so
/// an image without it failed on the first index creation or migration.
/// <see cref="BitOperations.Crc32C(uint, ulong)"/> is in the BCL, uses the SSE4.2 / Arm64 CRC
/// instruction where present and a managed table elsewhere, and needs no native library.
/// </remarks>
internal static class Crc32C
{
    /// <summary>Computes the standard CRC-32C (initial and final XOR <c>0xFFFFFFFF</c>).</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        while (data.Length >= sizeof(ulong))
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[sizeof(ulong)..];
        }

        foreach (var b in data)
        {
            crc = BitOperations.Crc32C(crc, b);
        }

        return ~crc;
    }

    /// <summary>Computes the CRC-32C of <paramref name="text"/>'s UTF-8 encoding.</summary>
    public static uint Compute(string text) => Compute(Encoding.UTF8.GetBytes(text));
}
