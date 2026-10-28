using System.Text;

namespace Bunting.Evaluation.Tests;

public class Murmur3Tests
{
    [Theory]
    [InlineData("", 0u, 0x00000000u)]
    [InlineData("", 1u, 0x514E28B7u)]
    [InlineData("", 0xffffffffu, 0x81F16F39u)]
    [InlineData("test", 0u, 0xBA6BD213u)]
    [InlineData("Hello, world!", 0u, 0xC0363E43u)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0u, 0x2E4FF723u)]
    [InlineData("aaaa", 0x9747b28cu, 0x5A97808Au)]
    [InlineData("abc", 0u, 0xB3DD93FAu)]
    public void Matches_reference_vectors(string input, uint seed, uint expected) =>
        Assert.Equal(expected, Murmur3.Hash32(Encoding.UTF8.GetBytes(input), seed));
}
