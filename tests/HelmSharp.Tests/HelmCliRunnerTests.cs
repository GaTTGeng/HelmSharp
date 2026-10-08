namespace HelmSharp.Tests;

/// <summary>
/// Unit tests for the Helm CLI runner helpers themselves (currently line-ending
/// normalization used by golden comparisons).
/// </summary>
public class HelmCliRunnerTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("line\n", "line\n")]
    [InlineData("first\r\nsecond\r\n", "first\nsecond\n")]
    [InlineData("first\rsecond\r", "first\nsecond\n")]
    [InlineData("first\r\nsecond\rthird\n", "first\nsecond\nthird\n")]
    public void NormalizeLineEndings_NormalizesAllLineEndingStyles(string input, string expected)
    {
        Assert.Equal(expected, HelmCliRunner.NormalizeLineEndings(input));
    }
}
