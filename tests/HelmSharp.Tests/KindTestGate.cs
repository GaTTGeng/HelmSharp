namespace HelmSharp.Tests;

/// <summary>
/// Opt-in gate for kind-backed Kubernetes lifecycle tests. The fast CI suite must stay
/// cluster-independent, so these tests skip unless maintainers set
/// <c>HELM_SHARP_KIND_TESTS=1</c> and point kubeconfig at a disposable cluster.
/// </summary>
internal static class KindTestGate
{
    public const string SkipReason =
        "Kind lifecycle tests are opt-in. Set HELM_SHARP_KIND_TESTS=1 and configure kubeconfig for a disposable cluster.";

    public static bool IsEnabled
        => string.Equals(Environment.GetEnvironmentVariable("HELM_SHARP_KIND_TESTS"), "1", StringComparison.Ordinal);
}

/// <summary>xUnit fact that runs only when the kind lifecycle lane is enabled.</summary>
public sealed class KindFactAttribute : FactAttribute
{
    public KindFactAttribute()
    {
        if (!KindTestGate.IsEnabled)
            Skip = KindTestGate.SkipReason;
    }
}
