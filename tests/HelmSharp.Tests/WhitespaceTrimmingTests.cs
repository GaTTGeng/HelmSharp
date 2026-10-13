using HelmSharp.Chart;
using HelmSharp.Engine;
using Xunit.Abstractions;

namespace HelmSharp.Tests;

/// <summary>
/// Covers Go-template whitespace trim markers (<c>{{-</c> / <c>-}}</c>) around tokens
/// and inside if/define blocks.
/// </summary>
public class WhitespaceTrimmingTests(ITestOutputHelper output)
{

    [Fact]
    public void LeftTrim_RemovesWhitespaceBeforeToken()
    {
        var chart = MakeChart("name: {{- .Values.name }}\ndata: value\n");
        var result = Render(chart, new Dictionary<string, object?> { ["name"] = "test" });
        output.WriteLine(result);
        Assert.Contains("name:test", result);
    }

    [Fact]
    public void RightTrim_RemovesWhitespaceAfterToken()
    {
        var chart = MakeChart("name: {{ .Values.name -}}\ndata: value\n");
        var result = Render(chart, new Dictionary<string, object?> { ["name"] = "test" });
        output.WriteLine(result);
        Assert.Contains("data: value", result);
    }

    [Fact]
    public void BothTrim_RemovesSurroundingWhitespace()
    {
        var chart = MakeChart("start\n  {{- .Values.name -}}\nend\n");
        var result = Render(chart, new Dictionary<string, object?> { ["name"] = "test" });
        output.WriteLine(result);
        Assert.Contains("starttest", result.Replace("\r", "").Replace("\n", ""));
    }

    [Fact]
    public void IfBlock_WithLeftTrim()
    {
        var chart = MakeChart("{{- if .Values.enabled }}\n  active\n{{- else }}\n  inactive\n{{- end }}\n");
        var result = Render(chart, new Dictionary<string, object?> { ["enabled"] = true });
        output.WriteLine(result);
        Assert.Contains("active", result);
    }

    [Fact]
    public void IfBlock_WithRightTrim()
    {
        var chart = MakeChart("{{ if .Values.enabled -}}\nactive\n{{ end }}\n");
        var result = Render(chart, new Dictionary<string, object?> { ["enabled"] = true });
        output.WriteLine(result);
        Assert.Contains("active", result);
    }

    [Fact]
    public void DefineBlock_WithTrim()
    {
        var chart = new HelmChart
        {
            Name = "test", Version = "1.0.0", ValuesYaml = "",
            Templates =
            {
                ["templates/_helpers.tpl"] = "{{- define \"test.name\" -}}\n{{ .Release.Name }}-test\n{{- end -}}\n",
                ["templates/cm.yaml"] = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: {{ include \"test.name\" . }}\ndata:\n  key: value\n"
            }
        };

        var values = new Dictionary<string, object?>();
        var renderer = new HelmTemplateRenderer(chart, "my-release", "default", values);
        var result = renderer.Render();
        output.WriteLine(result);
        Assert.Contains("my-release-test", result);
    }

    [Fact]
    public void Range_WithTrim()
    {
        var chart = MakeChart("items:\n{{- range .Values.items }}\n  - {{ . }}\n{{- end }}\n");
        var result = Render(chart, new Dictionary<string, object?>
        {
            ["items"] = new List<object?> { "a", "b", "c" }
        });
        output.WriteLine(result);
        Assert.Contains("- a", result);
        Assert.Contains("- b", result);
        Assert.Contains("- c", result);
    }

    [Fact]
    public void WithBlock_WithTrim()
    {
        var chart = MakeChart("{{- with .Values.data }}\nvalue: {{ . }}\n{{- end }}\nfinal: true\n");
        var result = Render(chart, new Dictionary<string, object?> { ["data"] = "hello" });
        output.WriteLine(result);
        Assert.Contains("value: hello", result);
        Assert.Contains("final: true", result);
    }

    [Fact]
    public void MultipleTrimOnSameLine()
    {
        var chart = MakeChart("{{- .Values.a -}}|{{- .Values.b -}}\n");
        var result = Render(chart, new Dictionary<string, object?> { ["a"] = "hello", ["b"] = "world" });
        output.WriteLine(result);
        Assert.Contains("hello|world", result);
    }

    [Fact]
    public void IndentedDefine_WithTrim()
    {
        var chart = new HelmChart
        {
            Name = "test", Version = "1.0.0", ValuesYaml = "",
            Templates =
            {
                ["templates/_helpers.tpl"] = "{{- define \"test.labels\" -}}\napp: {{ .Release.Name }}\nversion: {{ .Chart.Version }}\n{{- end }}\n",
                ["templates/cm.yaml"] = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: test\n  labels:\n    {{- include \"test.labels\" . | nindent 4 }}\ndata:\n  key: value\n"
            }
        };

        var values = new Dictionary<string, object?>();
        var renderer = new HelmTemplateRenderer(chart, "my-app", "default", values);
        var result = renderer.Render();
        output.WriteLine(result);
        Assert.Contains("app: my-app", result);
    }

    private static HelmChart MakeChart(string templateContent)
    {
        return new HelmChart
        {
            Name = "test", Version = "1.0.0", ValuesYaml = "",
            Templates = { ["templates/test.yaml"] = templateContent }
        };
    }

    private string Render(HelmChart chart, Dictionary<string, object?> values)
    {
        var renderer = new HelmTemplateRenderer(chart, "test-release", "default", values);
        return renderer.Render();
    }
}
