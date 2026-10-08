namespace HelmSharp.Tests;

/// <summary>
/// Marks a single-case test that requires the Helm CLI on PATH.
/// Skips (rather than fails) when <c>helm</c> is unavailable so the suite stays portable.
/// </summary>
public sealed class HelmCliFactAttribute : FactAttribute
{
    public HelmCliFactAttribute()
    {
        if (!HelmCliRunner.IsAvailable())
            Skip = "Helm CLI is not available on PATH.";
    }
}

/// <summary>
/// Data-driven counterpart of <see cref="HelmCliFactAttribute"/>; skips the whole
/// theory when the Helm CLI is not on PATH.
/// </summary>
public sealed class HelmCliTheoryAttribute : TheoryAttribute
{
    public HelmCliTheoryAttribute()
    {
        if (!HelmCliRunner.IsAvailable())
            Skip = "Helm CLI is not available on PATH.";
    }
}
