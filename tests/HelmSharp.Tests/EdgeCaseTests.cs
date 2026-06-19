using HelmSharp.Chart;
using HelmSharp.Engine;
using Xunit.Abstractions;

namespace HelmSharp.Tests;

public class EdgeCaseTests
{
    private readonly ITestOutputHelper _output;

    public EdgeCaseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void EmptyTemplate_ProducesEmptyOutput()
    {
        var chart = new HelmChart { Name = "empty", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = "";
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        Assert.True(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void NoTemplates_ProducesEmptyOutput()
    {
        var chart = new HelmChart { Name = "empty", Version = "1.0.0", ValuesYaml = "" };
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        Assert.True(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void OnlyHelpersTpl_ProducesEmptyOutput()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/_helpers.tpl"] = "{{- define \"test.name\" -}}test{{- end -}}";
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        Assert.True(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void NotesTxt_NotIncludedInRender()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/NOTES.txt"] = "Thank you!";
        chart.Templates["templates/cm.yaml"] = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: test\n";
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        Assert.DoesNotContain("Thank you", result);
        Assert.Contains("ConfigMap", result);
    }

    [Fact]
    public void NestedIfBlocks()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- if .Values.a }}
            a is true
            {{- if .Values.b }}
            b is also true
            {{- end }}
            {{- end }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["a"] = true, ["b"] = true });
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("a is true", result);
        Assert.Contains("b is also true", result);
    }

    [Fact]
    public void NestedIfBlocks_OuterFalse()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- if .Values.a }}
            a is true
            {{- if .Values.b }}
            b is also true
            {{- end }}
            {{- end }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["a"] = false, ["b"] = true });
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.DoesNotContain("a is true", result);
        Assert.DoesNotContain("b is also true", result);
    }

    [Fact]
    public void RangeOverEmptyList()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- range .Values.items }}
            - {{ . }}
            {{- end }}
            done
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["items"] = new List<object?>() });
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("done", result);
        Assert.DoesNotContain("- ", result);
    }

    [Fact]
    public void RangeOverNil()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- range .Values.items }}
            - {{ . }}
            {{- end }}
            done
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("done", result);
    }

    [Fact]
    public void RangeOverMap()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- range $key, $val := .Values.data }}
            {{ $key }}={{ $val }}
            {{- end }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>
            {
                ["data"] = new Dictionary<string, object?>
                {
                    ["host"] = "localhost",
                    ["port"] = 5432L
                }
            });
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("host=localhost", result);
        Assert.Contains("port=5432", result);
    }

    [Fact]
    public void WithNil_SkipsBlock()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- with .Values.data }}
            value: {{ . }}
            {{- end }}
            done
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("done", result);
        Assert.DoesNotContain("value:", result);
    }

    [Fact]
    public void DefaultFunction_Fallback()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            val: {{ default "fallback" .Values.missing | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("fallback", result);
    }

    [Fact]
    public void ChartMetadata()
    {
        var chart = new HelmChart
        {
            Name = "my-chart",
            Version = "2.0.0",
            AppVersion = "1.5.0",
            Description = "Test chart",
            Type = "application"
        };
        chart.Templates["templates/test.yaml"] = """
            name: {{ .Chart.Name | quote }}
            version: {{ .Chart.Version | quote }}
            appVersion: {{ .Chart.AppVersion | quote }}
            type: {{ .Chart.Type | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("my-chart", result);
        Assert.Contains("2.0.0", result);
        Assert.Contains("1.5.0", result);
        Assert.Contains("application", result);
    }

    [Fact]
    public void CapabilitiesObject()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            kubeVersion: {{ .Capabilities.KubeVersion.Version | quote }}
            helmVersion: {{ .Capabilities.HelmVersion.Version | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("v1.29.0", result);
        Assert.Contains("chemical-ai-helm", result);
    }

    [Fact]
    public void BuiltInObjects_UseRequestedCapabilitiesAndReleaseState()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            kubeVersion: {{ .Capabilities.KubeVersion.Version | quote }}
            kubeMajor: {{ .Capabilities.KubeVersion.Major | quote }}
            kubeMinor: {{ .Capabilities.KubeVersion.Minor | quote }}
            hasDefaultApi: {{ .Capabilities.APIVersions.Has "v1" | quote }}
            hasCustomApi: {{ .Capabilities.APIVersions.Has "example.test/v1" | quote }}
            isInstall: {{ .Release.IsInstall | quote }}
            isUpgrade: {{ .Release.IsUpgrade | quote }}
            revision: {{ .Release.Revision | quote }}
            """;
        var renderer = new HelmTemplateRenderer(
            chart,
            "rel",
            "default",
            new Dictionary<string, object?>(),
            "v1.31.4",
            ["example.test/v1"],
            true,
            7);

        var result = renderer.Render();

        Assert.Contains("kubeVersion: \"v1.31.4\"", result);
        Assert.Contains("kubeMajor: \"1\"", result);
        Assert.Contains("kubeMinor: \"31\"", result);
        Assert.Contains("hasDefaultApi: \"true\"", result);
        Assert.Contains("hasCustomApi: \"true\"", result);
        Assert.Contains("isInstall: \"false\"", result);
        Assert.Contains("isUpgrade: \"true\"", result);
        Assert.Contains("revision: \"7\"", result);
    }

    [Theory]
    [InlineData("1.31", "v1.31.0", "1", "31")]
    [InlineData("v1.31.4+vendor.2", "v1.31.4+vendor.2", "1", "31")]
    [InlineData("V2.0.1-rc.1", "v2.0.1-rc.1", "2", "0")]
    public void Capabilities_NormalizesRequestedKubeVersion(
        string requestedVersion,
        string expectedVersion,
        string expectedMajor,
        string expectedMinor)
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            version: {{ .Capabilities.KubeVersion.Version | quote }}
            gitVersion: {{ .Capabilities.KubeVersion.GitVersion | quote }}
            major: {{ .Capabilities.KubeVersion.Major | quote }}
            minor: {{ .Capabilities.KubeVersion.Minor | quote }}
            """;
        var renderer = new HelmTemplateRenderer(
            chart,
            "rel",
            "default",
            new Dictionary<string, object?>(),
            requestedVersion,
            null,
            false);

        var result = renderer.Render();

        Assert.Contains($"version: \"{expectedVersion}\"", result);
        Assert.Contains($"gitVersion: \"{expectedVersion}\"", result);
        Assert.Contains($"major: \"{expectedMajor}\"", result);
        Assert.Contains($"minor: \"{expectedMinor}\"", result);
    }

    [Fact]
    public void ChartDependencies_ExposeCompleteDependencyShape()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Dependencies.Add(new HelmChartDependency
        {
            Name = "redis",
            Version = "17.x.x",
            Repository = "https://charts.example.test",
            Condition = "redis.enabled",
            Tags = ["cache"],
            Enabled = true,
            ImportValues =
            [
                "data",
                new Dictionary<string, object?>
                {
                    ["child"] = "service",
                    ["parent"] = "imported"
                }
            ],
            Alias = "cache"
        });
        chart.Templates["templates/test.yaml"] = """
            {{- $dependency := index .Chart.Dependencies 0 }}
            {{- $mapping := index $dependency.ImportValues 1 }}
            name: {{ $dependency.Name | quote }}
            version: {{ $dependency.Version | quote }}
            repository: {{ $dependency.Repository | quote }}
            condition: {{ $dependency.Condition | quote }}
            tag: {{ index $dependency.Tags 0 | quote }}
            enabled: {{ $dependency.Enabled | quote }}
            importString: {{ index $dependency.ImportValues 0 | quote }}
            importChild: {{ $mapping.child | quote }}
            importParent: {{ $mapping.parent | quote }}
            alias: {{ $dependency.Alias | quote }}
            """;
        var renderer = new HelmTemplateRenderer(
            chart,
            "rel",
            "default",
            new Dictionary<string, object?>());

        var result = renderer.Render();

        Assert.Contains("name: \"cache\"", result);
        Assert.Contains("version: \"17.x.x\"", result);
        Assert.Contains("repository: \"https://charts.example.test\"", result);
        Assert.Contains("condition: \"redis.enabled\"", result);
        Assert.Contains("tag: \"cache\"", result);
        Assert.Contains("enabled: \"true\"", result);
        Assert.Contains("importString: \"data\"", result);
        Assert.Contains("importChild: \"service\"", result);
        Assert.Contains("importParent: \"imported\"", result);
        Assert.Contains("alias: \"cache\"", result);
    }

    [Fact]
    public void SubchartTemplate_UsesParentQualifiedTemplatePaths()
    {
        var chart = new HelmChart { Name = "parent", Version = "1.0.0", ValuesYaml = "" };
        var subchart = new HelmChart { Name = "child", Version = "1.0.0", ValuesYaml = "" };
        subchart.Templates["templates/configmap.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: child
            data:
              templateName: {{ .Template.Name | quote }}
              templateBasePath: {{ .Template.BasePath | quote }}
            """;
        chart.Subcharts["child"] = subchart;

        var renderer = new HelmTemplateRenderer(
            chart,
            "rel",
            "default",
            new Dictionary<string, object?>());

        var result = renderer.Render();

        Assert.Contains(
            "templateName: \"parent/charts/child/templates/configmap.yaml\"",
            result);
        Assert.Contains(
            "templateBasePath: \"parent/charts/child/templates\"",
            result);
    }

    [Fact]
    public void AliasedSubchart_UsesAliasIdentityAndValues()
    {
        var chart = new HelmChart { Name = "parent", Version = "1.0.0", ValuesYaml = "" };
        chart.Dependencies.Add(new HelmChartDependency
        {
            Name = "child",
            Alias = "cache",
            Version = "1.0.0",
            Condition = "cache.enabled"
        });
        chart.Templates["templates/parent.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: parent
            data:
              dependencyName: {{ (index .Chart.Dependencies 0).Name | quote }}
              dependencyAlias: {{ (index .Chart.Dependencies 0).Alias | quote }}
            """;
        var subchart = new HelmChart
        {
            Name = "child",
            Version = "1.0.0",
            ValuesYaml = "marker: default\n"
        };
        subchart.Templates["templates/child.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: child
            data:
              chartName: {{ .Chart.Name | quote }}
              marker: {{ .Values.marker | quote }}
              templateName: {{ .Template.Name | quote }}
              templateBasePath: {{ .Template.BasePath | quote }}
            """;
        chart.Subcharts["child"] = subchart;
        var values = new Dictionary<string, object?>
        {
            ["child"] = new Dictionary<string, object?>
            {
                ["marker"] = "original"
            },
            ["cache"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["marker"] = "alias"
            }
        };

        var renderer = new HelmTemplateRenderer(chart, "rel", "default", values);
        var result = renderer.Render();

        Assert.Contains("dependencyName: \"cache\"", result);
        Assert.Contains("dependencyAlias: \"cache\"", result);
        Assert.Contains("chartName: \"cache\"", result);
        Assert.Contains("marker: \"alias\"", result);
        Assert.Contains(
            "templateName: \"parent/charts/cache/templates/child.yaml\"",
            result);
        Assert.Contains(
            "templateBasePath: \"parent/charts/cache/templates\"",
            result);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    public void Dependencies_ApplyConditionAfterTags(
        bool? conditionValue,
        bool tagValue,
        bool expectedEnabled)
    {
        var chart = new HelmChart { Name = "parent", Version = "1.0.0", ValuesYaml = "" };
        chart.Dependencies.Add(new HelmChartDependency
        {
            Name = "child",
            Version = "1.0.0",
            Condition = "child.enabled",
            Tags = ["optional"]
        });
        chart.Templates["templates/parent.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: parent
            data:
              dependencyCount: {{ len .Chart.Dependencies | quote }}
            """;
        var subchart = new HelmChart { Name = "child", Version = "1.0.0", ValuesYaml = "" };
        subchart.Templates["templates/child.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: child
            """;
        chart.Subcharts["child"] = subchart;

        var childValues = new Dictionary<string, object?>();
        if (conditionValue.HasValue)
            childValues["enabled"] = conditionValue.Value;
        var values = new Dictionary<string, object?>
        {
            ["child"] = childValues,
            ["tags"] = new Dictionary<string, object?>
            {
                ["optional"] = tagValue
            }
        };
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", values);

        var result = renderer.Render();

        Assert.Contains(
            $"dependencyCount: \"{(expectedEnabled ? 1 : 0)}\"",
            result);
        Assert.Equal(
            expectedEnabled,
            result.Contains("name: child", StringComparison.Ordinal));
        Assert.True(chart.Dependencies[0].Enabled);
    }

    [Theory]
    [InlineData(false, null, null, false)]
    [InlineData(false, true, null, true)]
    [InlineData(false, null, true, true)]
    [InlineData(true, false, null, false)]
    public void Dependencies_UseExplicitEnabledAsInitialState(
        bool declaredEnabled,
        bool? tagValue,
        bool? conditionValue,
        bool expectedEnabled)
    {
        var chart = new HelmChart { Name = "parent", Version = "1.0.0", ValuesYaml = "" };
        chart.Dependencies.Add(new HelmChartDependency
        {
            Name = "child",
            Version = "1.0.0",
            Enabled = declaredEnabled,
            Condition = "child.enabled",
            Tags = ["optional"]
        });
        chart.Templates["templates/parent.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: parent
            data:
              dependencyCount: {{ len .Chart.Dependencies | quote }}
            """;
        var subchart = new HelmChart { Name = "child", Version = "1.0.0", ValuesYaml = "" };
        subchart.Templates["templates/child.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: child
            """;
        chart.Subcharts["child"] = subchart;

        var values = new Dictionary<string, object?>();
        if (tagValue.HasValue)
        {
            values["tags"] = new Dictionary<string, object?>
            {
                ["optional"] = tagValue.Value
            };
        }
        if (conditionValue.HasValue)
        {
            values["child"] = new Dictionary<string, object?>
            {
                ["enabled"] = conditionValue.Value
            };
        }

        var renderer = new HelmTemplateRenderer(chart, "rel", "default", values);
        var result = renderer.Render();

        Assert.Contains(
            $"dependencyCount: \"{(expectedEnabled ? 1 : 0)}\"",
            result);
        Assert.Equal(
            expectedEnabled,
            result.Contains("name: child", StringComparison.Ordinal));
        Assert.Equal(declaredEnabled, chart.Dependencies[0].Enabled);
    }

    [Fact]
    public void RangeWithVariables_PreservesContextAndSetsDot()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- range $index, $item := .Values.items }}
            item-{{ $index }}:
              variable: {{ $item.name | quote }}
              dot: {{ .name | quote }}
              isUpgrade: {{ .Release.IsUpgrade | quote }}
              revision: {{ .Release.Revision | quote }}
              kubeVersion: {{ .Capabilities.KubeVersion.Version | quote }}
              hasCustomApi: {{ .Capabilities.APIVersions.Has "example.test/v1" | quote }}
              templateName: {{ .Template.Name | quote }}
            {{- end }}
            """;
        var values = new Dictionary<string, object?>
        {
            ["items"] = new List<object?>
            {
                new Dictionary<string, object?> { ["name"] = "first" },
                new Dictionary<string, object?> { ["name"] = "second" }
            }
        };
        var renderer = new HelmTemplateRenderer(
            chart,
            "rel",
            "default",
            values,
            "v1.31.4",
            ["example.test/v1"],
            true,
            7);

        var result = renderer.Render();

        Assert.Contains("item-0:", result);
        Assert.Contains("item-1:", result);
        Assert.Contains("variable: \"first\"", result);
        Assert.Contains("dot: \"second\"", result);
        Assert.Contains("isUpgrade: \"true\"", result);
        Assert.Contains("revision: \"7\"", result);
        Assert.Contains("kubeVersion: \"v1.31.4\"", result);
        Assert.Contains("hasCustomApi: \"true\"", result);
        Assert.Contains("templateName: \"test/templates/test.yaml\"", result);
    }

    [Fact]
    public void ReleaseObject()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            name: {{ .Release.Name | quote }}
            namespace: {{ .Release.Namespace | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "my-release", "production",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("my-release", result);
        Assert.Contains("production", result);
    }

    [Fact]
    public void VariableAssignment()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- $name := "my-var" }}
            {{- $count := 42 }}
            name: {{ $name | quote }}
            count: {{ $count }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("my-var", result);
        Assert.Contains("42", result);
    }

    [Fact]
    public void VariableInDict()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- $labels := dict "app" .Release.Name "version" .Chart.Version }}
            labels:
              app: {{ index $labels "app" | quote }}
              version: {{ index $labels "version" | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "my-app", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("my-app", result);
    }

    [Fact]
    public void IncludeFunction()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/_helpers.tpl"] = """
            {{- define "test.fullname" -}}
            {{ .Release.Name }}-{{ .Chart.Name }}
            {{- end }}
            {{- define "test.labels" -}}
            app: {{ include "test.fullname" . }}
            version: {{ .Chart.Version }}
            {{- end }}
            """;
        chart.Templates["templates/cm.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: {{ include "test.fullname" . }}
              labels:
                {{- include "test.labels" . | nindent 4 }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "my-release", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("my-release-test", result);
        Assert.Contains("1.0.0", result);
    }

    [Fact]
    public void DeepNestedBlocks()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- if .Values.level1 }}
            level1
            {{- if .Values.level2 }}
            level2
            {{- if .Values.level3 }}
            level3
            {{- end }}
            end2
            {{- end }}
            end1
            {{- end }}
            done
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["level1"] = true, ["level2"] = true, ["level3"] = true });
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("level1", result);
        Assert.Contains("level2", result);
        Assert.Contains("level3", result);
        Assert.Contains("done", result);
    }

    [Fact]
    public void ElseIf_Pattern()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- if eq .Values.env "prod" }}
            production
            {{- else if eq .Values.env "staging" }}
            staging
            {{- else }}
            development
            {{- end }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["env"] = "staging" });
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("staging", result);
        Assert.DoesNotContain("production", result);
        Assert.DoesNotContain("development", result);
    }

    [Fact]
    public void CommentIgnored()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{/* This is a comment */}}
            data: value
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("data: value", result);
        Assert.DoesNotContain("comment", result);
    }

    [Fact]
    public void MultilineYamlOutput()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: test
            data:
              config.yaml: |
                server:
                  port: 8080
                  host: 0.0.0.0
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("server:", result);
        Assert.Contains("port: 8080", result);
    }

    [Fact]
    public void LargeTemplate_Rendering()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 50; i++)
            sb.AppendLine($"  key{i}: value{i}");
        chart.Templates["templates/test.yaml"] = $"apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: test\ndata:\n{sb}";

        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        Assert.Contains("key0: value0", result);
        Assert.Contains("key49: value49", result);
    }
}
