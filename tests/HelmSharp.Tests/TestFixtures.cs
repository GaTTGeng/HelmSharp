namespace HelmSharp.Tests;

internal static class TestFixtures
{
    public static string ChartPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Charts", name);
}
