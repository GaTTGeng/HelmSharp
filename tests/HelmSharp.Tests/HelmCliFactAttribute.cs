namespace HelmSharp.Tests;

public sealed class HelmCliFactAttribute : FactAttribute
{
    public HelmCliFactAttribute()
    {
        if (!HelmCliRunner.IsAvailable())
            Skip = "Helm CLI is not available on PATH.";
    }
}

public sealed class HelmCliTheoryAttribute : TheoryAttribute
{
    public HelmCliTheoryAttribute()
    {
        if (!HelmCliRunner.IsAvailable())
            Skip = "Helm CLI is not available on PATH.";
    }
}
