using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HelmSharp.Release;
using k8s.Models;

namespace HelmSharp.Tests;

public class HelmV3ReleaseStorageTests
{
    [Fact]
    public void Decode_ReadsFixedHelmV3ReleaseShape()
    {
        var json = File.ReadAllText(FixturePath());

        var record = HelmV3ReleaseCodec.Decode(EncodeLikeHelm(json, compress: true));

        Assert.Equal("fixture-release", record.Name);
        Assert.Equal("fixture-ns", record.Namespace);
        Assert.Equal(3, record.Revision);
        Assert.Equal("deployed", record.Status);
        Assert.Equal("fixture-chart", record.ChartName);
        Assert.Equal("1.2.3", record.ChartVersion);
        Assert.Equal("4.5.6", record.AppVersion);
        Assert.Equal("v2", record.ChartApiVersion);
        Assert.Equal("Fixture chart", record.ChartDescription);
        Assert.Equal("application", record.ChartType);
        Assert.Equal(">=1.25.0", record.ChartKubeVersion);
        Assert.Equal(DateTimeOffset.Parse("2026-07-17T01:02:03Z"), record.FirstDeployedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-07-17T01:03:04Z"), record.UpdatedAt);
        Assert.Null(record.DeletedAt);
        Assert.Equal("Install complete", record.Description);
        Assert.Equal("Fixture notes\n", record.Notes);
        Assert.Contains("message: hello", record.ValuesYaml);
        Assert.Contains("replicaCount: 2", record.ValuesYaml);
        Assert.Contains("message: default", record.ChartValuesYaml);
        Assert.Contains("replicaCount: 1", record.ChartValuesYaml);
        Assert.DoesNotContain("fixture-hook", record.Manifest);

        var hook = Assert.Single(record.Hooks);
        Assert.Equal("fixture-hook", hook.Name);
        Assert.Equal("Job", hook.Kind);
        Assert.Equal(["pre-install"], hook.Events);
        Assert.Equal("Succeeded", hook.LastRunPhase);
        Assert.Equal(-1, hook.Weight);
        Assert.Equal(["before-hook-creation"], hook.DeletePolicies);
        Assert.Equal(["hook-succeeded"], hook.OutputLogPolicies);
    }

    [Fact]
    public void Decode_AcceptsHelmPreCompressionPayload()
    {
        var json = File.ReadAllText(FixturePath());

        var record = HelmV3ReleaseCodec.Decode(EncodeLikeHelm(json, compress: false));

        Assert.Equal("fixture-release", record.Name);
        Assert.Equal(3, record.Revision);
    }

    [Fact]
    public void Encode_ProducesHelmV3EnvelopeWithDistinctConfigAndChartValues()
    {
        var record = CreateRecord();

        using var document = DecodeLikeHelm(HelmV3ReleaseCodec.Encode(record));
        var root = document.RootElement;

        Assert.Equal("fixture-release", root.GetProperty("name").GetString());
        Assert.False(root.TryGetProperty("labels", out _));
        Assert.Equal("", root.GetProperty("info").GetProperty("deleted").GetString());
        Assert.Equal("override", root.GetProperty("config").GetProperty("message").GetString());
        Assert.Equal("computed", root.GetProperty("helmsharp_computed_values").GetProperty("message").GetString());
        Assert.Equal("default", root.GetProperty("chart").GetProperty("values").GetProperty("message").GetString());
        Assert.Equal("v2", root.GetProperty("chart").GetProperty("metadata").GetProperty("apiVersion").GetString());
        Assert.Equal("Succeeded", root.GetProperty("hooks")[0].GetProperty("last_run").GetProperty("phase").GetString());
        Assert.DoesNotContain("fixture-hook", root.GetProperty("manifest").GetString());
    }

    [Fact]
    public void EncodeThenDecode_PreservesDeletionTimestamp()
    {
        var deletedAt = DateTimeOffset.Parse("2026-07-21T06:00:00Z");
        var record = CreateRecord() with { Status = "uninstalled", DeletedAt = deletedAt };

        var decoded = HelmV3ReleaseCodec.Decode(HelmV3ReleaseCodec.Encode(record));

        Assert.Equal(deletedAt, decoded.DeletedAt);
    }


    [Fact]
    public void DecodeThenEncode_PreservesOpaqueHelmChartPayload()
    {
        var json = File.ReadAllText(FixturePath());
        var record = HelmV3ReleaseCodec.Decode(EncodeLikeHelm(json, compress: true));

        using var document = DecodeLikeHelm(HelmV3ReleaseCodec.Encode(record with { Status = "superseded" }));
        var chart = document.RootElement.GetProperty("chart");
        var metadata = chart.GetProperty("metadata");

        Assert.Equal(
            "YXBpVmVyc2lvbjogdjEKa2luZDogQ29uZmlnTWFwCg==",
            chart.GetProperty("templates")[0].GetProperty("data").GetString());
        Assert.Equal("eyJ0eXBlIjoib2JqZWN0In0=", chart.GetProperty("schema").GetString());
        Assert.Equal(
            "IyBGaXh0dXJlIENoYXJ0Cg==",
            chart.GetProperty("files")[0].GetProperty("data").GetString());
        Assert.Equal("sha256:fixture", chart.GetProperty("lock").GetProperty("digest").GetString());
        Assert.Equal(
            "true",
            metadata.GetProperty("annotations").GetProperty("example.test/preserved").GetString());
        Assert.Equal("https://example.test/fixture-chart", metadata.GetProperty("home").GetString());
    }

    [Fact]
    public void Decode_ReportsCorruptGzipPayload()
    {
        var encoded = Convert.ToBase64String([0x1f, 0x8b, 0x08, 0xff]);

        var exception = Assert.Throws<InvalidDataException>(
            () => HelmV3ReleaseCodec.Decode(encoded));

        Assert.Contains("gzip", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_ReportsInvalidJsonInsideGzip()
    {
        var encoded = EncodeLikeHelm("{not-json", compress: true);

        var exception = Assert.Throws<InvalidDataException>(
            () => HelmV3ReleaseCodec.Decode(encoded));

        Assert.Contains("release JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void ReadRecord_FallsBackToLegacyReleaseJson()
    {
        var record = CreateRecord();
        var legacyJson = JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = "sh.helm.release.v1.fixture-release.v3",
                NamespaceProperty = "fixture-ns",
                Labels = new Dictionary<string, string>
                {
                    ["owner"] = "helm",
                    ["team"] = "sdk"
                }
            },
            Data = new Dictionary<string, byte[]>
            {
                ["release.json"] = Encoding.UTF8.GetBytes(legacyJson)
            }
        };

        var restored = HelmReleaseStore.ReadRecord(secret);

        Assert.Equal(record.Name, restored.Name);
        Assert.Equal(record.Revision, restored.Revision);
        Assert.Equal("sdk", restored.Labels!["team"]);
        Assert.False(restored.Labels.ContainsKey("owner"));
    }

    [Fact]
    public void ReadRecord_ReportsMalformedPayloadWithSecretIdentity()
    {
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = "sh.helm.release.v1.broken.v1",
                NamespaceProperty = "broken-ns"
            },
            Data = new Dictionary<string, byte[]>
            {
                ["release"] = Encoding.UTF8.GetBytes("not-base64")
            }
        };

        var exception = Assert.Throws<HelmReleaseStoreException>(
            () => HelmReleaseStore.ReadRecord(secret));

        Assert.Equal("sh.helm.release.v1.broken.v1", exception.SecretName);
        Assert.Equal("broken-ns", exception.NamespaceName);
        Assert.Equal("Helm v3 release", exception.Format);
        Assert.Contains("Base64", exception.Message);
    }

    [Fact]
    public void ReadRecord_ReportsMissingPayload()
    {
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = "missing",
                NamespaceProperty = "test"
            }
        };

        var exception = Assert.Throws<HelmReleaseStoreException>(
            () => HelmReleaseStore.ReadRecord(secret));

        Assert.Contains("Neither data.release nor the legacy release.json key was present", exception.Message);
    }

    [Fact]
    public void BuildSecret_CreatesHelmV3SecretWithSystemAndCustomLabels()
    {
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var record = CreateRecord() with
        {
            Labels = new Dictionary<string, string>
            {
                ["team"] = "sdk",
                ["status"] = "forged"
            }
        };

        var secret = HelmReleaseStore.BuildSecret(record, existing: null, timestamp);

        Assert.Equal("sh.helm.release.v1.fixture-release.v3", secret.Metadata.Name);
        Assert.Equal("fixture-ns", secret.Metadata.NamespaceProperty);
        Assert.Equal("helm.sh/release.v1", secret.Type);
        Assert.Equal("helm", secret.Metadata.Labels["owner"]);
        Assert.Equal("deployed", secret.Metadata.Labels["status"]);
        Assert.Equal("3", secret.Metadata.Labels["version"]);
        Assert.Equal("1700000000", secret.Metadata.Labels["createdAt"]);
        Assert.False(secret.Metadata.Labels.ContainsKey("modifiedAt"));
        Assert.Equal("sdk", secret.Metadata.Labels["team"]);
        Assert.Equal(["release"], secret.Data.Keys);
        Assert.Null(secret.StringData);

        using var decoded = DecodeLikeHelm(Encoding.UTF8.GetString(secret.Data["release"]));
        Assert.Equal("fixture-release", decoded.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void BuildSecret_UpdateUsesModifiedAtAndPreservesResourceVersion()
    {
        var existing = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                ResourceVersion = "42",
                Labels = new Dictionary<string, string>
                {
                    ["createdAt"] = "1600000000",
                    ["team"] = "existing"
                }
            }
        };

        var secret = HelmReleaseStore.BuildSecret(
            CreateRecord(),
            existing,
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

        Assert.Equal("42", secret.Metadata.ResourceVersion);
        Assert.Equal("1700000000", secret.Metadata.Labels["modifiedAt"]);
        Assert.False(secret.Metadata.Labels.ContainsKey("createdAt"));
        Assert.Equal("existing", secret.Metadata.Labels["team"]);
    }


    [Fact]
    public void CreateChartSnapshot_MapsCompleteFreshHelmChartShape()
    {
        var chart = new HelmSharp.Chart.HelmChart
        {
            ApiVersion = "v2",
            Name = "fresh-chart",
            Version = "2.3.4",
            AppVersion = "5.6.7",
            Description = "Fresh chart",
            Home = "https://example.test/fresh",
            Icon = "https://example.test/icon.svg",
            Sources = ["https://example.test/source"],
            Keywords = ["sdk", "helm"],
            Maintainers =
            [
                new Dictionary<string, object?>
                {
                    ["name"] = "Maintainer",
                    ["email"] = "maintainer@example.test"
                }
            ],
            Type = "application",
            Deprecated = true,
            KubeVersion = ">=1.28.0",
            Annotations = new Dictionary<string, object?>
            {
                ["example.test/annotation"] = "preserved"
            },
            LockDigest = "sha256:lock",
            LockGenerated = "2026-07-17T00:00:00Z",
            ValuesYaml = "message: default\nnested:\n  enabled: true\n"
        };
        chart.Dependencies.Add(new HelmSharp.Chart.HelmChartDependency
        {
            Name = "child",
            Version = "1.2.0",
            Repository = "https://example.test/charts",
            Condition = "child.enabled",
            Tags = ["backend"],
            Enabled = true,
            ImportValues = ["exports.data"],
            Alias = "child-alias"
        });
        chart.LockEntries.Add(new HelmSharp.Chart.HelmChartLockEntry
        {
            Name = "child",
            Version = "1.2.0",
            Repository = "https://example.test/charts",
            Digest = "sha256:child"
        });
        chart.Templates["templates/configmap.yaml"] = "apiVersion: v1\nkind: ConfigMap\n";
        chart.Files["values.schema.json"] = Encoding.UTF8.GetBytes("{\"type\":\"object\"}");
        chart.Files["README.md"] = Encoding.UTF8.GetBytes("# Fresh chart\n");
        chart.Files["assets/blob.bin"] = [0x00, 0x7f, 0xff];
        chart.Files["Chart.lock"] = Encoding.UTF8.GetBytes("digest: sha256:lock\n");
        var child = new HelmSharp.Chart.HelmChart
        {
            ApiVersion = "v2",
            Name = "child",
            Version = "1.2.0",
            ValuesYaml = "childDefault: true\n"
        };
        child.Templates["templates/child.yaml"] = "apiVersion: v1\nkind: ConfigMap\n";
        child.Files["README.md"] = Encoding.UTF8.GetBytes("# Child chart\n");
        chart.Subcharts["child-alias"] = child;
        chart.Crds.Add(new Dictionary<string, object?>
        {
            ["apiVersion"] = "apiextensions.k8s.io/v1",
            ["kind"] = "CustomResourceDefinition",
            ["metadata"] = new Dictionary<string, object?>
            {
                ["name"] = "widgets.example.test"
            }
        });

        using var document = JsonDocument.Parse(HelmV3ReleaseCodec.CreateChartSnapshot(chart));
        var root = document.RootElement;
        var metadata = root.GetProperty("metadata");

        Assert.Equal("fresh-chart", metadata.GetProperty("name").GetString());
        Assert.Equal("https://example.test/fresh", metadata.GetProperty("home").GetString());
        Assert.Equal("https://example.test/source", metadata.GetProperty("sources")[0].GetString());
        Assert.Equal("sdk", metadata.GetProperty("keywords")[0].GetString());
        Assert.Equal(
            "maintainer@example.test",
            metadata.GetProperty("maintainers")[0].GetProperty("email").GetString());
        Assert.Equal("https://example.test/icon.svg", metadata.GetProperty("icon").GetString());
        Assert.True(metadata.GetProperty("deprecated").GetBoolean());
        Assert.Equal(
            "preserved",
            metadata.GetProperty("annotations").GetProperty("example.test/annotation").GetString());
        var dependency = metadata.GetProperty("dependencies")[0];
        Assert.Equal("child-alias", dependency.GetProperty("alias").GetString());
        Assert.Equal("exports.data", dependency.GetProperty("import-values")[0].GetString());

        var lockElement = root.GetProperty("lock");
        Assert.Equal("sha256:lock", lockElement.GetProperty("digest").GetString());
        Assert.Equal("2026-07-17T00:00:00Z", lockElement.GetProperty("generated").GetString());
        Assert.Equal(
            "sha256:child",
            lockElement.GetProperty("dependencies")[0].GetProperty("digest").GetString());
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(chart.Templates["templates/configmap.yaml"])),
            root.GetProperty("templates")[0].GetProperty("data").GetString());
        Assert.Equal(
            Convert.ToBase64String(chart.Files["values.schema.json"]),
            root.GetProperty("schema").GetString());
        Assert.Equal("default", root.GetProperty("values").GetProperty("message").GetString());
        var childSnapshot = Assert.Single(root.GetProperty("dependencies").EnumerateArray());
        Assert.Equal("child", childSnapshot.GetProperty("metadata").GetProperty("name").GetString());
        Assert.True(childSnapshot.GetProperty("values").GetProperty("childDefault").GetBoolean());
        Assert.Single(childSnapshot.GetProperty("templates").EnumerateArray());
        Assert.Single(childSnapshot.GetProperty("files").EnumerateArray());
        var crd = Assert.Single(root.GetProperty("crds").EnumerateArray());
        Assert.Equal("crds/widgets.example.test.yaml", crd.GetProperty("name").GetString());
        Assert.Contains("CustomResourceDefinition", Encoding.UTF8.GetString(Convert.FromBase64String(crd.GetProperty("data").GetString()!)));

        var files = root.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(files, file => file.GetProperty("name").GetString() == "values.schema.json");
        Assert.DoesNotContain(files, file => file.GetProperty("name").GetString() == "Chart.lock");
        var binary = Assert.Single(files, file => file.GetProperty("name").GetString() == "assets/blob.bin");
        Assert.Equal(Convert.ToBase64String(chart.Files["assets/blob.bin"]), binary.GetProperty("data").GetString());
    }

    [Fact]
    public void ReadRecord_ReadsOfficialHelmV3213RawSecretFixture()
    {
        // Generated with helm.sh/helm/v3 v3.21.3 official release/chart types.
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Releases",
            "helm-v3.21.3-release-secret.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        var root = fixture.RootElement;
        var metadata = root.GetProperty("metadata");
        var outerData = Convert.FromBase64String(root.GetProperty("data").GetProperty("release").GetString()!);
        var innerRelease = Encoding.UTF8.GetString(outerData);
        Assert.StartsWith("H4sI", innerRelease);

        var labels = metadata.GetProperty("labels")
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!);
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = metadata.GetProperty("name").GetString(),
                NamespaceProperty = metadata.GetProperty("namespace").GetString(),
                Labels = labels
            },
            Type = root.GetProperty("type").GetString(),
            Data = new Dictionary<string, byte[]> { ["release"] = outerData }
        };

        var record = HelmReleaseStore.ReadRecord(secret);

        Assert.Equal("fixture-release", record.Name);
        Assert.Equal("fixture-ns", record.Namespace);
        Assert.Equal(3, record.Revision);
        Assert.Equal("deployed", record.Status);
        Assert.Equal("fixture-chart", record.ChartName);
        Assert.Equal("1.2.3", record.ChartVersion);
        Assert.Equal("4.5.6", record.AppVersion);
        Assert.Equal("Complete Helm v3.21.3 fixture chart", record.ChartDescription);
        Assert.Equal("Install complete", record.Description);
        Assert.Equal("Fixture notes from Helm v3.21.3\n", record.Notes);
        Assert.Contains("message: override", record.ValuesYaml);
        Assert.Contains("message: default", record.ChartValuesYaml);
        Assert.DoesNotContain("fixture-hook", record.Manifest);
        Assert.Equal("fixture", record.Labels!["environment"]);
        Assert.Equal("sdk", record.Labels["team"]);
        Assert.False(record.Labels.ContainsKey("owner"));
        var hook = Assert.Single(record.Hooks);
        Assert.Equal(["pre-install", "test"], hook.Events);

        using var chartDocument = JsonDocument.Parse(record.RawChartJson!);
        var chart = chartDocument.RootElement;
        Assert.Equal(2, chart.GetProperty("templates").GetArrayLength());
        Assert.Equal(2, chart.GetProperty("files").GetArrayLength());
        Assert.Equal("sha256:fixture-lock-digest", chart.GetProperty("lock").GetProperty("digest").GetString());
        Assert.Equal(
            "true",
            chart.GetProperty("metadata").GetProperty("annotations").GetProperty("example.test/preserved").GetString());

        using var roundTrip = DecodeLikeHelm(HelmV3ReleaseCodec.Encode(record with { Status = "superseded" }));
        var roundTripChart = roundTrip.RootElement.GetProperty("chart");
        Assert.Equal(2, roundTripChart.GetProperty("templates").GetArrayLength());
        Assert.Equal(2, roundTripChart.GetProperty("files").GetArrayLength());
        Assert.Equal(
            "sha256:fixture-lock-digest",
            roundTripChart.GetProperty("lock").GetProperty("digest").GetString());
    }
    private static HelmReleaseRecord CreateRecord()
        => new()
        {
            Name = "fixture-release",
            Namespace = "fixture-ns",
            Revision = 3,
            Status = "deployed",
            ChartName = "fixture-chart",
            ChartVersion = "1.2.3",
            AppVersion = "4.5.6",
            ChartApiVersion = "v2",
            ChartDescription = "Fixture chart",
            ChartType = "application",
            ChartKubeVersion = ">=1.25.0",
            ChartValuesYaml = "message: default\nreplicaCount: 1\n",
            ValuesYaml = "message: override\nreplicaCount: 2\n",
            ComputedValuesYaml = "message: computed\nreplicaCount: 2\n",
            Manifest = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: fixture-release\n",
            FirstDeployedAt = DateTimeOffset.Parse("2026-07-17T01:02:03Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-07-17T01:03:04Z"),
            Description = "Install complete",
            Notes = "Fixture notes\n",
            Hooks =
            [
                new HelmReleaseHookRecord
                {
                    Name = "fixture-hook",
                    Kind = "Job",
                    Path = "templates/hook.yaml",
                    Manifest = "apiVersion: batch/v1\nkind: Job\nmetadata:\n  name: fixture-hook\n",
                    Events = ["pre-install"],
                    LastRunStartedAt = DateTimeOffset.Parse("2026-07-17T01:02:04Z"),
                    LastRunCompletedAt = DateTimeOffset.Parse("2026-07-17T01:02:05Z"),
                    LastRunPhase = "Succeeded",
                    Weight = -1,
                    DeletePolicies = ["before-hook-creation"],
                    OutputLogPolicies = ["hook-succeeded"]
                }
            ]
        };

    private static string FixturePath()
        => Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Releases",
            "helm-v3.21.3-release.json");

    private static string EncodeLikeHelm(string json, bool compress)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (!compress)
            return Convert.ToBase64String(bytes);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(bytes);
        return Convert.ToBase64String(output.ToArray());
    }

    private static JsonDocument DecodeLikeHelm(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded);
        using var compressed = new MemoryStream(bytes);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}
