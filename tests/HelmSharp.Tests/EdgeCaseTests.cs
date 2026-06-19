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
        Assert.Contains("hasCustomApi: \"true\"", result);
        Assert.Contains("isInstall: \"false\"", result);
        Assert.Contains("isUpgrade: \"true\"", result);
        Assert.Contains("revision: \"7\"", result);
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
