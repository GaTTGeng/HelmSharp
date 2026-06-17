using HelmSharp.Action;

namespace HelmSharp.Tests;

public class KubeVersionValidatorTests
{
    [Theory]
    [InlineData(">=1.20", "v1.25.0", true)]
    [InlineData(">=1.20", "v1.20.0", true)]
    [InlineData(">=1.20", "v1.19.0", false)]
    [InlineData(">=1.20.0", "v1.20.0", true)]
    [InlineData(">1.20", "v1.21.0", true)]
    [InlineData(">1.20", "v1.20.0", false)]
    [InlineData("<1.30", "v1.25.0", true)]
    [InlineData("<1.30", "v1.30.0", false)]
    [InlineData("<=1.30", "v1.30.0", true)]
    [InlineData("<=1.30", "v1.31.0", false)]
    [InlineData("~1.25", "v1.25.5", true)]
    [InlineData("~1.25", "v1.26.0", false)]
    [InlineData("^1.20", "v1.29.0", true)]
    [InlineData("^1.20", "v2.0.0", false)]
    [InlineData("1.25.x", "v1.25.3", true)]
    [InlineData("1.25.x", "v1.26.0", false)]
    [InlineData("1.25.*", "v1.25.0", true)]
    [InlineData("", "v1.25.0", true)]
    public void IsCompatible_ReturnsCorrectResult(string constraint, string clusterVersion, bool expected)
    {
        var result = KubeVersionValidator.IsCompatible(constraint, clusterVersion);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(">=1.20", "v1.25.0", true)]
    [InlineData(">=1.20", "v1.19.0", false)]
    public void Validate_ReturnsDetailedResult(string constraint, string clusterVersion, bool expected)
    {
        var (compatible, message) = KubeVersionValidator.Validate(constraint, clusterVersion);
        Assert.Equal(expected, compatible);
        Assert.NotNull(message);
        Assert.NotEmpty(message);
    }

    [Fact]
    public void Validate_IncompatibleResult_HasUsefulMessage()
    {
        var (compatible, message) = KubeVersionValidator.Validate(">=1.25", "v1.20.0");
        Assert.False(compatible);
        Assert.Contains("1.25", message);
        Assert.Contains("1.20", message);
    }

    [Fact]
    public void Validate_NullClusterVersion_ReturnsCompatible()
    {
        var (compatible, _) = KubeVersionValidator.Validate(">=1.20", "");
        Assert.True(compatible);
    }

    [Fact]
    public void Validate_NullConstraint_ReturnsCompatible()
    {
        var (compatible, _) = KubeVersionValidator.Validate("", "v1.25.0");
        Assert.True(compatible);
    }

    [Fact]
    public void IsCompatible_WithVPrefix()
    {
        Assert.True(KubeVersionValidator.IsCompatible(">=1.20", "v1.25.0"));
        Assert.True(KubeVersionValidator.IsCompatible(">=1.20", "V1.25.0"));
    }

    [Fact]
    public void IsCompatible_ComplexRange()
    {
        Assert.True(KubeVersionValidator.IsCompatible(">=1.20", "v1.25.0"));
        Assert.False(KubeVersionValidator.IsCompatible(">=1.20", "v1.19.0"));
        Assert.True(KubeVersionValidator.IsCompatible("<1.30", "v1.25.0"));
        Assert.False(KubeVersionValidator.IsCompatible("<1.30", "v1.30.0"));
    }

    [Fact]
    public void Validate_CompatibleResult_HasPositiveMessage()
    {
        var (compatible, message) = KubeVersionValidator.Validate(">=1.20", "v1.25.0");
        Assert.True(compatible);
        Assert.Contains("compatible", message);
    }
}
