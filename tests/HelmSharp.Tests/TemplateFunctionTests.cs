using HelmSharp.Chart;
using HelmSharp.Engine;
using Xunit.Abstractions;

namespace HelmSharp.Tests;

public class TemplateFunctionTests
{
    private readonly ITestOutputHelper _output;

    public TemplateFunctionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void IncludeResult_IsTrimmed()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/_helpers.tpl"] = """
            {{- define "test.name" -}}
            {{ .Release.Name }}-test
            {{- end }}
            """;
        chart.Templates["templates/cm.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: {{ include "test.name" . }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "my-app", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);

        Assert.Contains("name: my-app-test", result);
    }

    [Fact]
    public void DefineWithBothTrims()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/_helpers.tpl"] = """
            {{- define "test.fullname" -}}
            {{ .Release.Name }}-{{ .Chart.Name }}
            {{- end -}}
            """;
        chart.Templates["templates/cm.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: {{ include "test.fullname" . }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "release", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);

        Assert.Contains("name: release-test", result);
    }

    [Fact]
    public void IfBlock_WithLeftTrim_NoExtraNewline()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: test
            {{- if .Values.enabled }}
              annotations:
                enabled: "true"
            {{- end }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["enabled"] = true });
        var result = renderer.Render();
        _output.WriteLine(result);

        Assert.DoesNotContain("name: test\n\n", result);
    }

    [Fact]
    public void RangeWithTrim_ProducesCleanOutput()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: test
            data:
            {{- range .Values.items }}
              {{ .name }}: {{ .value | quote }}
            {{- end }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>
            {
                ["items"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["name"] = "key1", ["value"] = "val1" },
                    new Dictionary<string, object?> { ["name"] = "key2", ["value"] = "val2" }
                }
            });
        var result = renderer.Render();
        _output.WriteLine(result);

        Assert.Contains("key1: \"val1\"", result);
        Assert.Contains("key2: \"val2\"", result);
    }

    [Fact]
    public void FilesMethods_RenderTextEmptyGlobSecretAndBinaryContent()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Files["config/app.txt"] = "alpha\nbeta\n"u8.ToArray();
        chart.Files["config/count.txt"] = "1"u8.ToArray();
        chart.Files["config/date.txt"] = "2024-01-01"u8.ToArray();
        chart.Files["config/empty.txt"] = [];
        chart.Files["secrets/password.txt"] = "s3cr3t"u8.ToArray();
        chart.Files["binary/payload.bin"] = [0x00, 0x01, 0xFE, 0xFF];
        chart.Templates["templates/files.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: files
            data:
              get: {{ .Files.Get "config/app.txt" | quote }}
              missing: {{ .Files.Get "missing.txt" | quote }}
              lineCount: {{ len (.Files.Lines "config/app.txt") | quote }}
              binary: {{ .Files.GetBytes "binary/payload.bin" | b64enc | quote }}
              config: |-
            {{ (.Files.Glob "config/*").AsConfig | nindent 4 }}
              secrets: |-
            {{ (.Files.Glob "secrets/*").AsSecrets | nindent 4 }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);

        Assert.Contains("get: \"alpha\\nbeta\\n\"", result);
        Assert.Contains("missing: \"\"", result);
        Assert.Contains("lineCount: \"2\"", result);
        Assert.Contains("binary: \"AAH+/w==\"", result);
        Assert.Contains("app.txt: |\n      alpha\n      beta", result);
        Assert.Contains("count.txt: \"1\"", result);
        Assert.Contains("date.txt: \"2024-01-01\"", result);
        Assert.Contains("empty.txt: \"\"", result);
        Assert.Contains("password.txt: czNjcjN0", result);
    }

    [Fact]
    public void NestedDefines_WithTrim()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/_helpers.tpl"] = """
            {{- define "test.name" -}}
            {{ .Release.Name }}-test
            {{- end -}}
            {{- define "test.labels" -}}
            app: {{ include "test.name" . }}
            version: {{ .Chart.Version }}
            {{- end -}}
            """;
        chart.Templates["templates/cm.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: {{ include "test.name" . }}
              labels:
                {{- include "test.labels" . | nindent 4 }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "my-release", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);

        Assert.Contains("name: my-release-test", result);
        Assert.Contains("app: my-release-test", result);
    }

    [Fact]
    public void ElseBlock_WithTrim()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: test
            {{- if .Values.production }}
              annotations:
                env: production
            {{- else }}
              annotations:
                env: staging
            {{- end }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["production"] = false });
        var result = renderer.Render();
        _output.WriteLine(result);

        Assert.Contains("env: staging", result);
        Assert.DoesNotContain("production", result);
    }

    [Fact]
    public void ElseIf_WithTrim()
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
    public void Comment_WithLeftTrim_RemovesPreviousLineWhitespace()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            spec:
              replicas: 1
              {{- /* comment */}}
              selector:
                app: test
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        var normalized = result.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("replicas: 1\n  selector:", normalized);
        Assert.DoesNotContain("replicas: 1\n\n  selector:", normalized);
        Assert.DoesNotContain("comment", normalized);
    }

    [Fact]
    public void BlockWithRightTrim_DoesNotDuplicateActionLineIndent()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            data:
              args:
              {{ if .Values.enabled -}}
              - --flag
              {{- end }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["enabled"] = true });
        var result = renderer.Render();
        _output.WriteLine(result);

        Assert.Contains("  - --flag", result);
        Assert.DoesNotContain("    - --flag", result);
    }

    [Fact]
    public void Render_OrdersManifestDocumentsLikeHelm()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/z-priorityclass.yaml"] = """
            apiVersion: scheduling.k8s.io/v1
            kind: PriorityClass
            metadata:
              name: app
            """;
        chart.Templates["templates/deployment.yaml"] = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: app
            """;
        chart.Templates["templates/service.yaml"] = """
            apiVersion: v1
            kind: Service
            metadata:
              name: app
            """;
        chart.Templates["templates/configmap.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: app
            data:
              helm.sh/hook: post-install
              helm.sh/hook-weight: "99"
            """;
        chart.Templates["templates/kindless.yaml"] = """
            # rendered comment-only manifest
            """;
        chart.Templates["templates/webhook.yaml"] = """
            apiVersion: admissionregistration.k8s.io/v1
            kind: ValidatingWebhookConfiguration
            metadata:
              name: app
            """;
        chart.Templates["templates/hook.yaml"] = """
            apiVersion: batch/v1
            kind: Job
            metadata:
              name: app-hook
              annotations:
                "helm.sh/hook": post-install
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);

        var priorityClassIndex = result.IndexOf("kind: PriorityClass", StringComparison.Ordinal);
        var configMapIndex = result.IndexOf("kind: ConfigMap", StringComparison.Ordinal);
        var serviceIndex = result.IndexOf("kind: Service", StringComparison.Ordinal);
        var deploymentIndex = result.IndexOf("kind: Deployment", StringComparison.Ordinal);
        var webhookIndex = result.IndexOf("kind: ValidatingWebhookConfiguration", StringComparison.Ordinal);
        var kindlessIndex = result.IndexOf("# rendered comment-only manifest", StringComparison.Ordinal);
        var hookIndex = result.IndexOf("name: app-hook", StringComparison.Ordinal);

        Assert.True(priorityClassIndex < configMapIndex);
        Assert.True(configMapIndex < serviceIndex);
        Assert.True(serviceIndex < deploymentIndex);
        Assert.True(deploymentIndex < webhookIndex);
        Assert.True(webhookIndex < kindlessIndex);
        Assert.True(kindlessIndex < hookIndex);
    }

    [Fact]
    public void Render_CapabilitiesHelmVersion_UsesAssemblyVersion()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/configmap.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: app
            data:
              version: {{ .Capabilities.HelmVersion.Version | quote }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);

        var expectedVersion = typeof(HelmTemplateRenderer).Assembly.GetName().Version?.ToString(3);
        Assert.Contains($"version: \"HelmSharp {expectedVersion}\"", result);
    }

    [Fact]
    public void Render_SplitsManifestDocumentsWithWhitespaceAndComments()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/multi.yaml"] = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: app
            --- # separator comment
            apiVersion: v1
            kind: Service
            metadata:
              name: app
            """ + "\n---   \n" + """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: app
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        var normalized = result.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("---\napiVersion: v1\nkind: ConfigMap", normalized);
        Assert.Contains("---\napiVersion: v1\nkind: Service", normalized);
        Assert.Contains("---\napiVersion: apps/v1\nkind: Deployment", normalized);
    }

    [Fact]
    public void MultipleIncludes_WithTrim()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/_helpers.tpl"] = """
            {{- define "test.name" -}}
            {{ .Release.Name }}-test
            {{- end -}}
            {{- define "test.fullname" -}}
            {{ include "test.name" . }}-v{{ .Chart.Version }}
            {{- end -}}
            """;
        chart.Templates["templates/cm.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: {{ include "test.fullname" . }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "my-app", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);

        Assert.Contains("name: my-app-test-v1.0.0", result);
    }

    [Fact]
    public void DefineWithBodyContent_NoExtraWhitespace()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/_helpers.tpl"] = """
            {{- define "test.config" -}}
            database:
              host: {{ .Values.host | default "localhost" }}
              port: {{ .Values.port | default "5432" }}
            {{- end -}}
            """;
        chart.Templates["templates/cm.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: test
            data:
              config.yaml: |
                {{ include "test.config" . | indent 4 }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["host"] = "db.example.com", ["port"] = "5432" });
        var result = renderer.Render();
        _output.WriteLine(result);

        Assert.Contains("host: db.example.com", result);
        Assert.Contains("port: 5432", result);
    }

    [Fact]
    public void ToYaml_WithIndent()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: test
              annotations:
                {{- toYaml .Values.annotations | nindent 4 }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>
            {
                ["annotations"] = new Dictionary<string, object?>
                {
                    ["key1"] = "value1",
                    ["key2"] = "value2"
                }
            });
        var result = renderer.Render();
        _output.WriteLine(result);

        Assert.Contains("key1: value1", result);
        Assert.Contains("key2: value2", result);
    }

    [Fact]
    public void ToYaml_SortsKeysAlphabetically()
    {
        // Helm/Go YAML marshaling sorts map keys alphabetically.
        // Verify that toYaml output matches this behavior.
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- toYaml .Values | nindent 0 }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>
            {
                ["zzz"] = "last-alpha",
                ["aaa"] = "first-alpha",
                ["nested"] = new Dictionary<string, object?>
                {
                    ["charlie"] = "c",
                    ["bravo"] = "b",
                    ["alpha"] = "a"
                },
                ["mmm"] = "middle"
            });
        var result = renderer.Render().TrimEnd();
        _output.WriteLine(result);

        // Keys must appear in alphabetical order: aaa, mmm, nested, zzz
        var idxAaa = result.IndexOf("aaa:", StringComparison.Ordinal);
        var idxMmm = result.IndexOf("mmm:", StringComparison.Ordinal);
        var idxNested = result.IndexOf("nested:", StringComparison.Ordinal);
        var idxZzz = result.IndexOf("zzz:", StringComparison.Ordinal);

        Assert.True(idxAaa >= 0, "aaa key missing");
        Assert.True(idxMmm >= 0, "mmm key missing");
        Assert.True(idxNested >= 0, "nested key missing");
        Assert.True(idxZzz >= 0, "zzz key missing");
        Assert.True(idxAaa < idxMmm, "aaa should come before mmm");
        Assert.True(idxMmm < idxNested, "mmm should come before nested");
        Assert.True(idxNested < idxZzz, "nested should come before zzz");

        // Nested keys must also be sorted: alpha, bravo, charlie
        var idxAlpha = result.IndexOf("alpha:", StringComparison.Ordinal);
        var idxBravo = result.IndexOf("bravo:", StringComparison.Ordinal);
        var idxCharlie = result.IndexOf("charlie:", StringComparison.Ordinal);
        Assert.True(idxAlpha < idxBravo, "nested: alpha should come before bravo");
        Assert.True(idxBravo < idxCharlie, "nested: bravo should come before charlie");
    }

    [Fact]
    public void ToYaml_RendersNestedNullsLikeHelm()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- toYaml .Values | nindent 0 }}
            """;

        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>
            {
                ["limits"] = null,
                ["requests"] = new Dictionary<string, object?>
                {
                    ["cpu"] = "1m"
                },
                ["plain"] = "value"
            });
        var result = renderer.Render();
        _output.WriteLine(result);

        Assert.Contains("limits: null", result);
        Assert.Contains("requests:", result);
        Assert.Contains("cpu: 1m", result);
        Assert.Contains("plain: value", result);
    }

    [Fact]
    public void StringFunctions_UpperLowerTitleTrimReplaceTrunc()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            upper: {{ "hello" | upper | quote }}
            lower: {{ "HELLO" | lower | quote }}
            title: {{ "hello world" | title | quote }}
            trim: {{ "  hello  " | trim | quote }}
            replace: {{ "hello world" | replace "world" "there" | quote }}
            trunc: {{ "hello world" | trunc 5 | quote }}
            truncTail: {{ "hello world" | trunc -5 | quote }}
            substr: {{ substr 1 5 "hello world" | quote }}
            substrAllWithNegativeEnd: {{ substr 0 -1 "hello" | quote }}
            substrTailWithNegativeEnd: {{ substr 2 -1 "hello" | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("HELLO", result);
        Assert.Contains("hello", result);
        Assert.Contains("Hello World", result);
        Assert.Contains("hello there", result);
        Assert.Contains("trunc: \"hello\"", result);
        Assert.Contains("truncTail: \"world\"", result);
        Assert.Contains("substr: \"ello\"", result);
        Assert.Contains("substrAllWithNegativeEnd: \"hello\"", result);
        Assert.Contains("substrTailWithNegativeEnd: \"llo\"", result);
    }

    [Fact]
    public void MathFunctions_AddSubMulDiv()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            add: {{ add 1 2 3 }}
            sub: {{ sub 10 3 }}
            mul: {{ mul 4 5 }}
            div: {{ div 20 4 }}
            mod: {{ mod 10 3 }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("add: 6", result);
        Assert.Contains("sub: 7", result);
        Assert.Contains("mul: 20", result);
        Assert.Contains("div: 5", result);
        Assert.Contains("mod: 1", result);
    }

    [Fact]
    public void ToDecimal_ParsesInputsAsOctal()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            modeWithoutLeadingZero: {{ "644" | toDecimal }}
            modeWithLeadingZero: {{ "0644" | toDecimal }}
            invalidOctal: {{ "08" | toDecimal }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("modeWithoutLeadingZero: 420", result);
        Assert.Contains("modeWithLeadingZero: 420", result);
        Assert.Contains("invalidOctal: 0", result);
    }

    [Fact]
    public void ListFunctions_FirstLastLenSort()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- $items := list "a" "b" "c" }}
            first: {{ first $items | quote }}
            last: {{ last $items | quote }}
            len: {{ len $items }}
            {{- $sorted := sortAlpha $items }}
            sorted: {{ toJson $sorted }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("first: \"a\"", result);
        Assert.Contains("last: \"c\"", result);
        Assert.Contains("len: 3", result);
    }

    [Fact]
    public void DictFunctions_GetHasKeyKeys()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- $d := dict "name" "test" "value" 42 }}
            name: {{ get $d "name" | quote }}
            hasKey: {{ hasKey $d "name" | quote }}
            keys: {{ keys $d | toJson }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("name: \"test\"", result);
        Assert.Contains("hasKey: \"true\"", result);
    }

    [Fact]
    public void EncodingFunctions_Base64()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            encoded: {{ "hello" | b64enc | quote }}
            decoded: {{ "aGVsbG8=" | b64dec | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("aGVsbG8=", result);
        Assert.Contains("hello", result);
    }

    [Fact]
    public void Sha256SumFunction()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            hash: {{ "hello" | sha256sum | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", result);
    }

    [Fact]
    public void SemverCompare()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            gte: {{ semverCompare ">=1.2.0" "1.5.0" | quote }}
            tilde: {{ semverCompare "~1.2.0" "1.2.5" | quote }}
            caret: {{ semverCompare "^1.2.0" "1.9.0" | quote }}
            fail: {{ semverCompare ">=2.0.0" "1.5.0" | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("gte: \"true\"", result);
        Assert.Contains("tilde: \"true\"", result);
        Assert.Contains("caret: \"true\"", result);
        Assert.Contains("fail: \"false\"", result);
    }

    [Fact]
    public void TypeFunctions_TypeOfKindOf()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            typeOf: {{ typeOf .Values.name | quote }}
            kindOf: {{ kindOf .Values.name | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["name"] = "hello" });
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("typeOf:", result);
        Assert.Contains("kindOf:", result);
    }

    [Fact]
    public void JsonFunctions_ToJsonFromJson()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            json: {{ toJson .Values.data }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>
            {
                ["data"] = new Dictionary<string, object?>
                {
                    ["key"] = "value",
                    ["num"] = 42L
                }
            });
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("key", result);
        Assert.Contains("value", result);
    }

    [Fact]
    public void RangeOverMap_IteratesKeyValuePairs()
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
    public void CoalesceFunction()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            val: {{ coalesce "" "" "found" "" | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("found", result);
    }

    [Fact]
    public void TernaryFunction()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            val: {{ ternary "yes" "no" .Values.flag | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["flag"] = true });
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("yes", result);
    }

    [Fact]
    public void PrintfFunction()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            val: {{ printf "%s-%s" .Release.Name "svc" | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "my-app", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("my-app-svc", result);
    }

    [Fact]
    public void QuoteFunction()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            val: {{ .Values.name | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["name"] = "hello" });
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("\"hello\"", result);
    }

    [Fact]
    public void IndentAndNindent()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            data:
              config: |
                {{ "line1\nline2" | indent 4 }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("line1", result);
        Assert.Contains("line2", result);
        Assert.DoesNotContain("\\n", result);
    }

    [Fact]
    public void EqComparison()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- if eq .Values.env "prod" }}
            is-prod
            {{- end }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["env"] = "prod" });
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("is-prod", result);
    }

    [Fact]
    public void NotFunction()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- if not .Values.disabled }}
            enabled
            {{- end }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?> { ["disabled"] = false });
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("enabled", result);
    }

    [Fact]
    public void EmptyFunction()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- if empty .Values.items }}
            no-items
            {{- end }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("no-items", result);
    }

    [Fact]
    public void LenFunction()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            count: {{ len .Values.items }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>
            {
                ["items"] = new List<object?> { "a", "b", "c" }
            });
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("count: 3", result);
    }

    [Fact]
    public void ContainsFunction()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- if contains "world" "hello world" }}
            found
            {{- end }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("found", result);
    }

    [Fact]
    public void HasPrefixHasSuffix()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            prefix: {{ hasPrefix "he" "hello" | quote }}
            suffix: {{ hasSuffix "lo" "hello" | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("prefix: \"true\"", result);
        Assert.Contains("suffix: \"true\"", result);
    }

    [Fact]
    public void TrimPrefixTrimSuffix()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            trimmed: {{ trimPrefix "hello " "hello world" | quote }}
            trimmed2: {{ trimSuffix " world" "hello world" | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("world", result);
        Assert.Contains("hello", result);
    }

    [Fact]
    public void RepeatFunction()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            val: {{ repeat 3 "ab" | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("ababab", result);
    }

    [Fact]
    public void SnakecaseCamelcaseKebabcase()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            snake: {{ "helloWorld" | snakecase | quote }}
            camel: {{ "hello_world" | camelcase | quote }}
            kebab: {{ "helloWorld" | kebabcase | quote }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("hello_world", result);
        Assert.Contains("hello-world", result);
    }

    [Fact]
    public void PrintFunction_RendersArgs()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            value: {{ print "hello" "world" }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("value: helloworld", result);
    }

    [Fact]
    public void PrintFunction_PipelineForm()
    {
        // Regression: pipeline value should be included in output, not silently dropped.
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            value: {{ "hello" | print }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("value: hello", result);
    }

    [Fact]
    public void PrintFunction_PipelineWithArgs()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            value: {{ "hello" | print "world" }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("value: worldhello", result);
    }

    [Fact]
    public void TupleFunction_CreatesList()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- $t := tuple "a" "b" "c" }}
            first: {{ index $t 0 | quote }}
            second: {{ index $t 1 | quote }}
            third: {{ index $t 2 | quote }}
            len: {{ len $t }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default",
            new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("first: \"a\"", result);
        Assert.Contains("second: \"b\"", result);
        Assert.Contains("third: \"c\"", result);
        Assert.Contains("len: 3", result);
    }

    [Fact]
    public void Join_ConcatenatesListWithSeparator()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- $l := list "a" "b" "c" }}
            {{- join ", " $l }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.EndsWith("a, b, c", result.TrimEnd());
    }

    [Fact]
    public void Split_SplitsStringIntoDict()
    {
        // Sprig split returns dict {_0, _1, …}; use ._N field access
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- $parts := split "," "a,b,c" }}
            {{ $parts._0 }},{{ $parts._1 }},{{ $parts._2 }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("a,b,c", result);
    }

    [Fact]
    public void Slice_ExtractsSublist()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- $l := list "a" "b" "c" "d" "e" }}
            {{- $s := slice $l 1 4 }}
            {{ join "," $s }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("b,c,d", result);
    }

    [Fact]
    public void Sha512Sum_ComputesHash()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- sha512sum "hello" }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("9b71d224", result);
    }

    [Fact]
    public void UuidV4_GeneratesValidUuid()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- uuidv4 }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render().TrimEnd();
        _output.WriteLine(result);
        Assert.Matches(@"---\s*[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", result);
    }

    [Fact]
    public void Nospace_RemovesWhitespace()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- nospace "a b  c d" }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("abcd", result);
    }

    [Fact]
    public void MergeOverwrite_MergesWithOverwriteSemantics()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- $a := dict "x" 1 "y" 2 }}
            {{- $b := dict "y" 3 "z" 4 }}
            {{- $m := mergeOverwrite $a $b }}
            x: {{ $m.x }}
            y: {{ $m.y }}
            z: {{ $m.z }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("x: 1", result);
        Assert.Contains("y: 3", result);
        Assert.Contains("z: 4", result);
    }

    [Fact]
    public void MergeOverwrite_AppendsPipelineValueAsLastArgument()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- $dst := dict "x" 1 "nested" (dict "a" "dst" "b" "dst") }}
            {{- $src := dict "y" 2 "nested" (dict "b" "src" "c" "src") }}
            {{- $m := $src | mergeOverwrite $dst }}
            x: {{ $m.x }}
            y: {{ $m.y }}
            nestedA: {{ $m.nested.a }}
            nestedB: {{ $m.nested.b }}
            nestedC: {{ $m.nested.c }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("x: 1", result);
        Assert.Contains("y: 2", result);
        Assert.Contains("nestedA: dst", result);
        Assert.Contains("nestedB: src", result);
        Assert.Contains("nestedC: src", result);
    }

    [Fact]
    public void RegexFunctions_MatchFindReplaceSplit()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            match: {{ regexMatch "[0-9]+" "abc123def" }}
            find: {{ regexFind "[0-9]+" "abc123def" }}
            replace: {{ regexReplaceAll "[0-9]+" "abc123def456" "NUM" }}
            split: {{ join "," (regexSplit "[,;]" "a,b;c,d") }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("match: true", result);
        Assert.Contains("find: 123", result);
        Assert.Contains("replace: abcNUMdefNUM", result);
        Assert.Contains("split: a,b,c,d", result);
    }

    [Fact]
    public void RegexFunctions_MustRegexReplaceAllLiteral()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            direct: {{ mustRegexReplaceAllLiteral "[0-9]+" "abc123def" "$1" }}
            piped: {{ "abc123def" | mustRegexReplaceAllLiteral "[0-9]+" "$1" }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());

        var result = renderer.Render();

        Assert.Contains("direct: abc$1def", result);
        Assert.Contains("piped: abc$1def", result);
    }

    [Fact]
    public void MustVariants_WorkCorrectly()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            {{- $l := list 3 1 4 1 5 9 }}
            rev: {{ index (mustReverse $l) 0 }}
            sorted: {{ index (mustSortAlpha $l) 0 }}
            compact: {{ len (mustCompact (list 0 1 2)) }}
            uniq: {{ len (mustUniq $l) }}
            has: {{ $l | mustHas 9 }}
            """;
        var renderer = new HelmTemplateRenderer(chart, "rel", "default", new Dictionary<string, object?>());
        var result = renderer.Render();
        _output.WriteLine(result);
        Assert.Contains("rev: 9", result);
        Assert.Contains("sorted: 1", result);
        Assert.Contains("compact: 2", result);
        Assert.Contains("uniq: 5", result);
        Assert.Contains("has: true", result);
    }
}
