using HelmSharp.Action;
using HelmSharp.Release;

namespace HelmSharp.Tests;

/// <summary>
/// Release record retrieval helpers: stored values YAML merging (all-values mode) and
/// release notes access from persisted releases.
/// </summary>
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
    public void GetStoredValuesYaml_AllValues_UsesPersistedComputedValues()
    {
        var record = new HelmReleaseRecord
        {
            ChartValuesYaml = "root: default\n",
            ValuesYaml = "root: override\n",
            ComputedValuesYaml = """
                root: override
                child:
                  imported: true
                  global: propagated
                """
        };

        var valuesYaml = HelmClient.GetStoredValuesYaml(record, allValues: true);

        Assert.Contains("imported: true", valuesYaml);
        Assert.Contains("global: propagated", valuesYaml);
    }

    [Fact]
    public void GetStoredNotes_ReturnsReleaseNotesWhenPresent()
    {
        var record = new HelmReleaseRecord { Notes = "Installed successfully.\n" };

        Assert.Equal("Installed successfully.\n", HelmClient.GetStoredNotes(record));
    }

    [Fact]
    public void ConvertRestorationApiVersions_ConvertsOmittedLegacyWorkload()
    {
        const string previous = "apiVersion: extensions/v1beta1\nkind: Deployment\nmetadata:\n  name: app\nspec:\n  template: {}\n";

        var converted = HelmClient.ConvertRestorationApiVersions(previous, string.Empty, "default");

        Assert.Contains("apiVersion: apps/v1", converted);
        Assert.Contains("kind: Deployment", converted);
    }

    [Fact]
    public void ConvertRestorationApiVersions_PreservesPreviousPodDisruptionBudgetVersionWithoutAttemptedTarget()
    {
        const string previous = """
            apiVersion: policy/v1beta1
            kind: PodDisruptionBudget
            metadata:
              name: app
            spec:
              selector:
                matchLabels:
                  app: app
              minAvailable: 1
            """;

        var restored = HelmClient.ConvertRestorationApiVersions(previous, string.Empty, "default");

        Assert.Contains("apiVersion: policy/v1beta1", restored, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertRestorationApiVersions_PreservesPreviousCronJobVersionWithoutAttemptedTarget()
    {
        const string previous = """
            apiVersion: batch/v1beta1
            kind: CronJob
            metadata:
              name: backup
            spec:
              schedule: "0 2 * * *"
            """;

        var restored = HelmClient.ConvertRestorationApiVersions(previous, string.Empty, "default");

        Assert.Contains("apiVersion: batch/v1beta1", restored, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveCleanedDocuments_DropsOnlySuccessfullyDeletedResources()
    {
        const string manifest = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: removed\n---\napiVersion: v1\nkind: Secret\nmetadata:\n  name: retained\n";

        var remaining = HelmClient.RemoveCleanedDocuments(
            manifest,
            ["ConfigMap/release-ns/removed"],
            "release-ns");

        Assert.DoesNotContain("name: removed", remaining);
        Assert.Contains("name: retained", remaining);
    }
}
