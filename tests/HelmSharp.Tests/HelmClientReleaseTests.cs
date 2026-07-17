using HelmSharp.Action;
using HelmSharp.Release;

namespace HelmSharp.Tests;

public class HelmClientReleaseTests
{
    [Fact]
    public void GetStoredValuesYaml_AllValues_MergesChartDefaultsWithOverrides()
    {
        var record = new HelmReleaseRecord
        {
            ChartValuesYaml = """
                replicaCount: 1
                image:
                  repository: nginx
                  tag: stable
                """,
            ValuesYaml = """
                replicaCount: 2
                image:
                  tag: 1.2.3
                """
        };

        var valuesYaml = HelmClient.GetStoredValuesYaml(record, allValues: true);

        Assert.Contains("replicaCount: 2", valuesYaml);
        Assert.Contains("repository: nginx", valuesYaml);
        Assert.Contains("tag: 1.2.3", valuesYaml);
        Assert.Equal(record.ValuesYaml, HelmClient.GetStoredValuesYaml(record, allValues: false));
    }

    [Fact]
    public void GetStoredNotes_ReturnsReleaseNotesWhenPresent()
    {
        var record = new HelmReleaseRecord { Notes = "Installed successfully.\n" };

        Assert.Equal("Installed successfully.\n", HelmClient.GetStoredNotes(record));
    }
}