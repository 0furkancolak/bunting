namespace Bunting.Evaluation.Tests;

public class SemVerTests
{
    private static SemVer P(string s)
    {
        Assert.True(SemVer.TryParse(s, out var v), s);
        return v;
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.2", "1.2.1")]
    public void Orders_by_semver_precedence(string lower, string higher)
    {
        Assert.True(P(lower).CompareTo(P(higher)) < 0);
        Assert.True(P(higher).CompareTo(P(lower)) > 0);
    }

    [Theory]
    [InlineData("1", "1.0.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.3+build.5", "1.2.3+other")]
    public void Treats_as_equal(string a, string b) => Assert.Equal(0, P(a).CompareTo(P(b)));

    [Theory]
    [InlineData("")]
    [InlineData("v1.0.0")]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    [InlineData("1..2")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-01")]
    [InlineData("-1.0.0")]
    public void Rejects_invalid(string input) => Assert.False(SemVer.TryParse(input, out _));
}
