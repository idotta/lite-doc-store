using System.Text;
using Xunit;

namespace LiteDocumentStore.UnitTests;

[Trait("Category", "Unit")]
public class Crc32CTests
{
    [Theory]
    // The standard CRC-32C check value, and the empty input.
    [InlineData("123456789", 0xE3069283u)]
    [InlineData("", 0x00000000u)]
    // Non-ASCII and not a multiple of eight bytes: runs both the 8-byte loop and the byte tail.
    [InlineData("CREATE TABLE Ünïcödé (id TEXT) -- 🦆", 0xE9A16D1Au)]
    public void Compute_MatchesTheReferenceValue(string text, uint expected)
    {
        Assert.Equal(expected, Crc32C.Compute(text));
    }

    [Fact]
    public void Compute_IsTheSameWhateverTheInputLengthModuloEight()
    {
        // Byte-at-a-time is the definition; the 8-byte path must agree at every split.
        var bytes = Encoding.UTF8.GetBytes("abcdefghijklmnopqrstuvwxyz0123456789");
        for (var length = 0; length <= bytes.Length; length++)
        {
            var slice = bytes.AsSpan(0, length);
            var expected = uint.MaxValue;
            foreach (var b in slice)
            {
                expected = System.Numerics.BitOperations.Crc32C(expected, b);
            }

            Assert.Equal(~expected, Crc32C.Compute(slice));
        }
    }
}
