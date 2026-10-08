namespace HelmSharp.Tests;

/// <summary>
/// Resolves paths to the shared chart fixtures that ship next to the test binaries.
/// Use this instead of hard-coding fixture locations so tests work from any runner layout.
/// </summary>
internal static class TestFixtures
{
    public static string ChartPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Charts", name);
}
