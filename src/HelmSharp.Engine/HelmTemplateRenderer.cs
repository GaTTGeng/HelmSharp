using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelmSharp.Chart;

namespace HelmSharp.Engine;

public sealed class HelmTemplateRenderer : IEvaluationContext
{
    private static readonly Regex TokenRegex = new(
        "{{-?\\s*(?<expr>.*?)\\s*-?}}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly IReadOnlyDictionary<string, int> InstallOrder = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Namespace"] = 0,
        ["NetworkPolicy"] = 1,
        ["ResourceQuota"] = 2,
        ["LimitRange"] = 3,
        ["PodSecurityPolicy"] = 4,
        ["PodDisruptionBudget"] = 5,
        ["ServiceAccount"] = 6,
        ["Secret"] = 7,
        ["SecretList"] = 8,
        ["ConfigMap"] = 9,
        ["StorageClass"] = 10,
        ["PersistentVolume"] = 11,
        ["PersistentVolumeClaim"] = 12,
        ["CustomResourceDefinition"] = 13,
        ["ClusterRole"] = 14,
        ["ClusterRoleList"] = 15,
        ["ClusterRoleBinding"] = 16,
        ["ClusterRoleBindingList"] = 17,
        ["Role"] = 18,
        ["RoleList"] = 19,
        ["RoleBinding"] = 20,
        ["RoleBindingList"] = 21,
        ["Service"] = 22,
        ["DaemonSet"] = 23,
        ["Pod"] = 24,
        ["ReplicationController"] = 25,
        ["ReplicaSet"] = 26,
        ["Deployment"] = 27,
        ["HorizontalPodAutoscaler"] = 28,
        ["StatefulSet"] = 29,
        ["Job"] = 30,
        ["CronJob"] = 31,
        ["IngressClass"] = 32,
        ["Ingress"] = 33,
        ["APIService"] = 34,
        ["MutatingWebhookConfiguration"] = 36,
        ["ValidatingWebhookConfiguration"] = 37,
    };

    private readonly HelmChart _chart;
    private readonly TemplateContext _root;
    private readonly Dictionary<string, string> _definedTemplates = new(StringComparer.Ordinal);

    /// <summary>
    /// Exposes the defined templates for diagnostic purposes (test infrastructure only).
    /// </summary>
    internal IReadOnlyDictionary<string, string> DefinedTemplates => _definedTemplates;

    public HelmTemplateRenderer(
        HelmChart chart,
        string releaseName,
        string releaseNamespace,
        Dictionary<string, object?> values)
        : this(chart, releaseName, releaseNamespace, values, null, null, false, 1)
    {
    }

    public HelmTemplateRenderer(
        HelmChart chart,
        string releaseName,
        string releaseNamespace,
        Dictionary<string, object?> values,
        string? kubeVersion,
        IEnumerable<string>? apiVersions,
        bool isUpgrade,
        int revision = 1)
    {
        _chart = chart;
        _root = new TemplateContext(
            chart,
            releaseName,
            releaseNamespace,
            values,
            values,
            new Dictionary<string, object?>(StringComparer.Ordinal))
        {
            IsInstall = !isUpgrade,
            IsUpgrade = isUpgrade,
            Revision = revision,
            KubeVersion = kubeVersion,
            ApiVersions = BuildApiVersions(apiVersions, kubeVersion),
            Dependencies = BuildEffectiveDependencies(chart.Dependencies, values)
        };
    }

    /// <summary>
    /// Renders all non-helper templates in the chart.
    /// </summary>
    /// <remarks>
    /// <para>Exceptions of type <see cref="NotSupportedException"/> (missing template function,
    /// parser limitations) and <see cref="TemplateParseException"/> (malformed template syntax)
    /// are accumulated per-template rather than failing immediately.
    /// This allows the engine to render as many templates as possible before throwing.
    /// Other exceptions (e.g. <c>fail</c> calls, arity errors) propagate immediately.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when one or more templates fail with <see cref="NotSupportedException"/>
    /// or <see cref="TemplateParseException"/>.
    /// The message lists each failing template and its exception type.
    /// </exception>
    public string Render()
    {
        // Extract defines from main chart templates
        foreach (var content in _chart.Templates.Values)
        {
            ExtractDefines(content);
        }

        // Extract defines from subchart templates (shared globally)
        foreach (var (_, subchart) in _chart.Subcharts)
        {
            foreach (var content in subchart.Templates.Values)
            {
                ExtractDefines(content);
            }
        }

        var manifests = new List<RenderedManifest>();
        var errors = new List<(string Path, Exception Exception)>();

        // Render main chart templates
        foreach (var (path, content) in _chart.Templates)
        {
            var fileName = Path.GetFileName(path);
            if (fileName.StartsWith('_') || fileName.Equals("NOTES.txt", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var withoutDefines = StripDefines(content);
                var templateContext = _root with
                {
                    CurrentTemplatePath = path,
                    Variables = new Dictionary<string, object?>(_root.Variables, StringComparer.Ordinal)
                };
                var rendered = RenderSection(withoutDefines, templateContext);
                if (string.IsNullOrWhiteSpace(rendered))
                    continue;

                AddManifestDocuments(manifests, $"{_chart.Name}/{path}", rendered);
            }
            catch (NotSupportedException ex)
            {
                // Engine-level gaps (missing function, parser limitation) — collect and continue
                errors.Add((path, new NotSupportedException($"{Path.GetFileName(path)}: {ex.Message}")));
            }
            catch (TemplateParseException ex)
            {
                // Malformed template syntax — collect and continue so the remaining
                // templates in the chart can still render
                errors.Add((path, new TemplateParseException($"{Path.GetFileName(path)}: {ex.Message}", ex.Line, ex.Column, ex.Offset)));
            }
            // Other exceptions (fail, arity errors, etc.) propagate immediately
        }

        // Render subchart templates
        foreach (var (subchartIdentity, subchart) in GetSubchartRenderInstances())
        {
            var subchartValues = HelmValues.BuildSubchartValues(
                subchart,
                _root.Values,
                subchartIdentity);
            var subchartContext = new TemplateContext(
                subchart, _root.ReleaseName, _root.ReleaseNamespace,
                subchartValues, subchartValues,
                new Dictionary<string, object?>(_root.Variables, StringComparer.Ordinal))
            {
                IsInstall = _root.IsInstall,
                IsUpgrade = _root.IsUpgrade,
                Revision = _root.Revision,
                KubeVersion = _root.KubeVersion,
                ApiVersions = _root.ApiVersions,
                TemplateChartName = subchartIdentity,
                TemplateChartPath = $"{_chart.Name}/charts/{subchartIdentity}",
                Dependencies = BuildEffectiveDependencies(subchart.Dependencies, subchartValues)
            };

            foreach (var (path, content) in subchart.Templates)
            {
                var fileName = Path.GetFileName(path);
                if (fileName.StartsWith('_') || fileName.Equals("NOTES.txt", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var withoutDefines = StripDefines(content);
                    var rendered = RenderSection(
                        withoutDefines,
                        subchartContext with
                        {
                            CurrentTemplatePath = path,
                            Variables = new Dictionary<string, object?>(subchartContext.Variables, StringComparer.Ordinal)
                        });
                    if (string.IsNullOrWhiteSpace(rendered))
                        continue;

                    AddManifestDocuments(
                        manifests,
                        $"{_chart.Name}/charts/{subchartIdentity}/{path}",
                        rendered);
                }
                catch (NotSupportedException ex)
                {
                    // Engine-level gaps (missing function, parser limitation) — collect and continue
                    errors.Add((path, new NotSupportedException($"{Path.GetFileName(path)} (subchart: {subchartIdentity}): {ex.Message}")));
                }
                catch (TemplateParseException ex)
                {
                    // Malformed template syntax — collect and continue so the remaining
                    // templates in the chart can still render
                    errors.Add((path, new TemplateParseException($"{Path.GetFileName(path)} (subchart: {subchartIdentity}): {ex.Message}", ex.Line, ex.Column, ex.Offset)));
                }
                // Other exceptions (fail, arity errors, etc.) propagate immediately
            }
        }

        if (errors.Count > 0)
        {
            var errorSummary = string.Join("; ",
                errors.Select(e => $"{Path.GetFileName(e.Path)}: {e.Exception.GetType().Name}: {e.Exception.Message}"));
            throw new InvalidOperationException(
                $"HelmSharp template rendering failed for {errors.Count} template(s): {errorSummary}",
                errors[0].Exception);
        }

        var output = new StringBuilder();
        foreach (var manifest in SortManifests(manifests))
        {
            output.Append("---\n");
            output.Append(manifest.Content);
            output.Append('\n');
        }

        return output.ToString();
    }

    private static void AddManifestDocuments(List<RenderedManifest> manifests, string sourcePath, string rendered)
    {
        foreach (var document in SplitManifestDocuments(NormalizeManifestContent(rendered)))
        {
            if (string.IsNullOrWhiteSpace(document))
                continue;

            var order = manifests.Count;
            manifests.Add(new RenderedManifest(
                sourcePath,
                document,
                ExtractManifestKind(document),
                IsHookManifest(document),
                ExtractHookWeight(document),
                order));
        }
    }

    private static IEnumerable<RenderedManifest> SortManifests(IEnumerable<RenderedManifest> manifests)
        => manifests
            .OrderBy(manifest => manifest.IsHook)
            .ThenBy(manifest => manifest.IsHook ? manifest.HookWeight : 0)
            .ThenBy(manifest => GetManifestKindOrder(manifest.Kind))
            .ThenBy(manifest => manifest.SourcePath, StringComparer.Ordinal)
            .ThenBy(manifest => manifest.OriginalOrder);

    private static int GetManifestKindOrder(string kind)
    {
        if (string.IsNullOrEmpty(kind))
            return 35;

        return InstallOrder.TryGetValue(kind, out var order) ? order : int.MaxValue;
    }

    private static List<string> SplitManifestDocuments(string manifest)
    {
        var documents = new List<string>();
        var current = new StringBuilder();
        foreach (var line in manifest.Split('\n'))
        {
            if (line == "---")
            {
                AddCurrentDocument(documents, current);
                continue;
            }

            current.Append(line);
            current.Append('\n');
        }

        AddCurrentDocument(documents, current);
        return documents;
    }

    private static void AddCurrentDocument(List<string> documents, StringBuilder current)
    {
        var document = current.ToString().Trim();
        current.Clear();
        if (document.Length > 0)
            documents.Add(document);
    }

    private static string ExtractManifestKind(string manifest)
    {
        var match = Regex.Match(manifest, @"(?m)^kind:\s*(.+)$");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static bool IsHookManifest(string manifest)
        => Regex.IsMatch(manifest, @"(?m)^\s*[""']?helm\.sh/hook[""']?\s*:");

    private static int ExtractHookWeight(string manifest)
    {
        var match = Regex.Match(manifest, @"(?m)^\s*[""']?helm\.sh/hook-weight[""']?\s*:\s*[""']?(?<weight>-?\d+)");
        return match.Success && int.TryParse(match.Groups["weight"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight)
            ? weight
            : 0;
    }

    private static string NormalizeManifestContent(string rendered)
    {
        var normalized = rendered
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
        return Regex.Replace(normalized, @"(?m)^---\n[ \t]*\n", "---\n");
    }

    private sealed record RenderedManifest(
        string SourcePath,
        string Content,
        string Kind,
        bool IsHook,
        int HookWeight,
        int OriginalOrder);

    private IEnumerable<(string Identity, HelmChart Chart)> GetSubchartRenderInstances()
    {
        if (_chart.Dependencies.Count == 0)
        {
            foreach (var (name, subchart) in _chart.Subcharts)
                yield return (name, subchart);
            yield break;
        }

        foreach (var dependency in _chart.Dependencies)
        {
            var identity = dependency.Alias ?? dependency.Name;
            var enabled = _root.Dependencies.Any(
                effective => string.Equals(effective.Name, identity, StringComparison.OrdinalIgnoreCase));
            if (!enabled)
                continue;

            if (TryGetSubchartForDependency(dependency, identity, out var subchart))
                yield return (identity, subchart);
        }
    }

    private bool TryGetSubchartForDependency(
        HelmChartDependency dependency,
        string identity,
        out HelmChart subchart)
    {
        if (_chart.Subcharts.TryGetValue(dependency.Name, out subchart!))
            return true;

        if (_chart.Subcharts.TryGetValue(identity, out subchart!))
            return true;

        foreach (var candidate in _chart.Subcharts.Values)
        {
            if (string.Equals(candidate.Name, dependency.Name, StringComparison.OrdinalIgnoreCase))
            {
                subchart = candidate;
                return true;
            }
        }

        subchart = null!;
        return false;
    }

    private void ExtractDefines(string content)
    {
        var defineOpenRegex = new Regex(
            "{{-?\\s*define\\s+\"(?<name>[^\"]+)\"\\s*-?}}",
            RegexOptions.Singleline);

        var matches = defineOpenRegex.Matches(content);
        foreach (Match openMatch in matches)
        {
            var name = openMatch.Groups["name"].Value;
            var bodyStart = openMatch.Index + openMatch.Length;
            var tokenMatches = TokenRegex.Matches(content, bodyStart);
            var depth = 1;

            foreach (Match token in tokenMatches)
            {
                var tokenExpr = token.Groups["expr"].Value.Trim();
                if (tokenExpr.StartsWith("define ", StringComparison.Ordinal) ||
                    tokenExpr.StartsWith("if ", StringComparison.Ordinal) ||
                    tokenExpr == "if" ||
                    tokenExpr.StartsWith("with ", StringComparison.Ordinal) ||
                    tokenExpr.StartsWith("range ", StringComparison.Ordinal))
                    depth++;
                else if (tokenExpr == "end")
                {
                    depth--;
                    if (depth == 0)
                    {
                        var body = content[bodyStart..token.Index];

                        // Apply whitespace trimming from define tag markers
                        // {{- define "name" }} trims leading whitespace from body
                        // {{ define "name" -}} trims trailing whitespace from body
                        var defineTag = openMatch.Value;
                        if (defineTag.Contains("{{-", StringComparison.Ordinal))
                            body = body.TrimStart();
                        // Check the end tag for right-trim
                        var endTag = token.Value;
                        if (endTag.EndsWith("-}}", StringComparison.Ordinal))
                            body = body.TrimEnd();

                        _definedTemplates[name] = body;
                        break;
                    }
                }
            }
        }
    }

    private static string StripDefines(string content)
    {
        var defineOpenRegex = new Regex(
            "{{-?\\s*define\\s+\"[^\"]+\"\\s*-?}}",
            RegexOptions.Singleline);

        var result = content;
        var matches = defineOpenRegex.Matches(result);

        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var openMatch = matches[i];
            var bodyStart = openMatch.Index + openMatch.Length;
            var tokenMatches = TokenRegex.Matches(result, bodyStart);
            var depth = 1;
            var endPos = -1;

            foreach (Match token in tokenMatches)
            {
                var tokenExpr = token.Groups["expr"].Value.Trim();
                if (tokenExpr.StartsWith("define ", StringComparison.Ordinal) ||
                    tokenExpr.StartsWith("if ", StringComparison.Ordinal) ||
                    tokenExpr == "if" ||
                    tokenExpr.StartsWith("with ", StringComparison.Ordinal) ||
                    tokenExpr.StartsWith("range ", StringComparison.Ordinal))
                    depth++;
                else if (tokenExpr == "end")
                {
                    depth--;
                    if (depth == 0)
                    {
                        endPos = token.Index + token.Length;
                        break;
                    }
                }
            }

            if (endPos > 0)
                result = result[..openMatch.Index] + result[endPos..];
        }

        return result;
    }

    public string RenderNotes()
    {
        foreach (var (path, content) in _chart.Templates)
        {
            var fileName = Path.GetFileName(path);
            if (!fileName.Equals("NOTES.txt", StringComparison.OrdinalIgnoreCase))
                continue;

            var withoutDefines = StripDefines(content);
            var notesContext = _root with
            {
                CurrentTemplatePath = path,
                Variables = new Dictionary<string, object?>(_root.Variables, StringComparer.Ordinal)
            };
            var rendered = RenderSection(withoutDefines, notesContext);
            return rendered.Trim();
        }
        return string.Empty;
    }

    /// <summary>
    /// Parses template text into an AST document.
    /// </summary>
    private static TemplateDocumentNode ParseTemplate(string content)
    {
        var tokenizer = new TemplateTokenizer(content);
        var parser = new TemplateParser(tokenizer.TokenizeFlat());
        return parser.Parse();
    }

    /// <summary>
    /// Renders an AST document by walking its children and evaluating actions.
    /// </summary>
    private string RenderDocument(TemplateDocumentNode doc, TemplateContext context)
    {
        var output = new StringBuilder();

        for (var i = 0; i < doc.Children.Count; i++)
        {
            var node = doc.Children[i];

            switch (node)
            {
                case TextNode text:
                {
                    var content = text.Content;

                    // Apply right trim from previous sibling (trim leading whitespace)
                    if (i > 0 && HasRightTrim(doc.Children[i - 1]))
                        content = TrimLeadingForRightTrim(content);

                    // Apply left trim from next sibling (trim trailing whitespace)
                    if (i + 1 < doc.Children.Count && HasLeftTrim(doc.Children[i + 1]))
                        content = TrimTrailingWhitespace(content);

                    output.Append(content);
                    break;
                }

                case ActionNode action:
                {
                    output.Append(RenderActionNode(action, context));
                    break;
                }

                case BlockNode block:
                {
                    if (block.RightTrim)
                        TrimCurrentLineIndent(output);
                    output.Append(RenderBlockNode(block, context));
                    break;
                }

                case CommentNode:
                case DefineNode:
                    // Comments produce no output; defines are extracted before rendering
                    break;
            }
        }

        return output.ToString();
    }

    private static void TrimCurrentLineIndent(StringBuilder output)
    {
        var index = output.Length - 1;
        while (index >= 0 && output[index] is ' ' or '\t')
            index--;

        if (index >= 0 && output[index] != '\n')
            return;

        output.Length = index + 1;
    }

    /// <summary>
    /// Renders a single action node (expression inside delimiters).
    /// </summary>
    private string RenderActionNode(ActionNode action, TemplateContext context)
    {
        var expr = action.Expression;

        // Handle comment actions
        if (expr.StartsWith("/*", StringComparison.Ordinal))
            return string.Empty;

        // Handle else/end pass-through (should not appear in properly parsed AST)
        if (expr is "else" or "else if" or "end")
            return $"{{{{ {expr} }}}}";

        string rendered;

        if (TryAssignVariable(expr, context, out var assigned))
        {
            rendered = assigned;
        }
        else
        {
            rendered = TypeConverters.ToTemplateString(EvaluatePipeline(expr, context));
        }

        return rendered;
    }

    /// <summary>
    /// Renders a block node (if/with/range) by evaluating the condition and
    /// walking the appropriate body.
    /// </summary>
    private string RenderBlockNode(BlockNode block, TemplateContext context)
    {
        return block.Keyword switch
        {
            "if" => RenderIfBlock(block, context),
            "with" => RenderWithBlock(block, context),
            "range" => RenderRangeBlock(block, context),
            _ => string.Empty,
        };
    }

    private string RenderIfBlock(BlockNode block, TemplateContext context)
    {
        if (TypeConverters.IsTruthy(EvaluatePipeline(block.Expression, context)))
        {
            return block.TrueBody is TemplateDocumentNode trueDoc
                ? RenderDocument(trueDoc, context)
                : string.Empty;
        }

        // Try else-if chain
        foreach (var elseIf in block.ElseIfChain)
        {
            if (TypeConverters.IsTruthy(EvaluatePipeline(elseIf.Condition, context)))
            {
                return elseIf.Body is TemplateDocumentNode eiDoc
                    ? RenderDocument(eiDoc, context)
                    : string.Empty;
            }
        }

        // Else body
        return block.FalseBody is TemplateDocumentNode falseDoc
            ? RenderDocument(falseDoc, context)
            : string.Empty;
    }

    private string RenderWithBlock(BlockNode block, TemplateContext context)
    {
        var value = EvaluatePipeline(block.Expression, context);

        if (TypeConverters.IsTruthy(value))
        {
            return block.TrueBody is TemplateDocumentNode trueDoc
                ? RenderDocument(trueDoc, context with { Dot = value })
                : string.Empty;
        }

        return block.FalseBody is TemplateDocumentNode falseDoc
            ? RenderDocument(falseDoc, context)
            : string.Empty;
    }

    private string RenderRangeBlock(BlockNode block, TemplateContext context)
    {
        // Serialize the true body back to text for RenderRangeExpression
        var bodyText = block.TrueBody is TemplateDocumentNode trueDoc
            ? trueDoc.SerializeToText()
            : string.Empty;

        var rendered = RenderRangeExpression(block.Expression, bodyText, context);

        // range … else: render else body when the range value has no elements.
        // The check is whether any output was produced; a non-empty collection whose
        // every element renders to an empty string would hit the else branch — which
        // matches Helm CLI's semantics (the range iterated zero times).
        if (rendered.Length == 0 && block.FalseBody is TemplateDocumentNode elseDoc)
            return RenderDocument(elseDoc, context);

        return rendered;
    }

    private string RenderSection(string template, TemplateContext context)
    {
        // AST-based rendering: parse template into AST, then walk and evaluate.
        // Block boundaries are determined by parser structure rather than regex depth counting.
        var doc = ParseTemplate(template);
        return RenderDocument(doc, context);
    }


    private static string TrimTrailingWhitespace(string value)
    {
        var end = value.Length;
        while (end > 0 && char.IsWhiteSpace(value[end - 1]))
            end--;
        return value[..end];
    }

    /// <summary>
    /// Applies right-trim to leading text content: removes leading horizontal whitespace
    /// and consumes the first newline if present (matching Helm CLI behavior).
    /// </summary>
    private static string TrimLeadingForRightTrim(string content)
    {
        if (content.Length == 0)
            return content;

        var newStart = 0;
        while (newStart < content.Length && char.IsWhiteSpace(content[newStart]) && content[newStart] != '\n')
            newStart++;
        if (newStart < content.Length && content[newStart] == '\n')
            newStart++;
        return content[newStart..];
    }

    /// <summary>Returns true if the node has a left-trim marker on its opening delimiter.</summary>
    private static bool HasLeftTrim(TemplateNode node)
        => node switch
        {
            ActionNode a => a.LeftTrim,
            BlockNode b => b.LeftTrim,
            CommentNode c => c.LeftTrim,
            _ => false,
        };

    /// <summary>Returns true if the node has a right-trim marker on its closing delimiter.</summary>
    private static bool HasRightTrim(TemplateNode node)
        => node switch
        {
            ActionNode a => a.RightTrim,
            BlockNode b => b.EndRightTrim,
            _ => false,
        };

    private string RenderRangeExpression(string expression, string body, TemplateContext context)
    {
        // Handle: range $k, $v := expr
        var assignIndex = expression.IndexOf(":=", StringComparison.Ordinal);
        if (assignIndex > 0)
        {
            var varPart = expression[..assignIndex].Trim();
            var exprPart = expression[(assignIndex + 2)..].Trim();
            var vars = varPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (vars.Length == 2)
            {
                var value = EvaluatePipeline(exprPart, context);
                if (value is IDictionary<string, object?> dict)
                {
                    var builder = new StringBuilder();
                    foreach (var kvp in dict)
                    {
                        var iterCtx = CreateRangeContext(context, kvp.Value);
                        iterCtx.Variables[vars[0]] = kvp.Key;
                        iterCtx.Variables[vars[1]] = kvp.Value;
                        builder.Append(RenderSection(body, iterCtx));
                    }
                    return builder.ToString();
                }

                if (value is IEnumerable<object?> items)
                {
                    var builder = new StringBuilder();
                    var index = 0;
                    foreach (var item in items)
                    {
                        var iterCtx = CreateRangeContext(context, item);
                        iterCtx.Variables[vars[0]] = index;
                        iterCtx.Variables[vars[1]] = item;
                        builder.Append(RenderSection(body, iterCtx));
                        index++;
                    }
                    return builder.ToString();
                }
            }
            else if (vars.Length == 1)
            {
                var value = EvaluatePipeline(exprPart, context);
                return RenderRangeWithVar(vars[0], value, body, context);
            }
        }

        // Simple range: range expr
        var conditionValue = EvaluatePipeline(expression, context);
        return RenderRange(conditionValue, body, context);
    }

    private string RenderRangeWithVar(string varName, object? value, string body, TemplateContext context)
    {
        if (value is IDictionary<string, object?> dict)
        {
            var builder = new StringBuilder();
            foreach (var kvp in dict)
            {
                var iterCtx = CreateRangeContext(context, kvp.Value);
                iterCtx.Variables[varName] = kvp.Value;
                builder.Append(RenderSection(body, iterCtx));
            }
            return builder.ToString();
        }

        if (value is IEnumerable<object?> items)
        {
            var builder = new StringBuilder();
            foreach (var item in items)
            {
                var iterCtx = CreateRangeContext(context, item);
                iterCtx.Variables[varName] = item;
                builder.Append(RenderSection(body, iterCtx));
            }
            return builder.ToString();
        }

        return string.Empty;
    }

    private static TemplateContext CreateRangeContext(TemplateContext context, object? dot)
        => context with
        {
            Dot = dot,
            Variables = new Dictionary<string, object?>(context.Variables, StringComparer.Ordinal)
        };

    private string RenderRange(object? value, string body, TemplateContext context)
    {
        if (value is IDictionary<string, object?> dict)
        {
            var builder = new StringBuilder();
            foreach (var kvp in dict)
            {
                var pair = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Key"] = kvp.Key, ["Value"] = kvp.Value };
                builder.Append(RenderSection(body, context with { Dot = pair }));
            }
            return builder.ToString();
        }

        if (value is IEnumerable<object?> items)
        {
            var builder = new StringBuilder();
            foreach (var item in items)
                builder.Append(RenderSection(body, context with { Dot = item }));
            return builder.ToString();
        }

        return string.Empty;
    }

    private bool TryAssignVariable(string expr, TemplateContext context, out string output)
    {
        output = string.Empty;
        var marker = expr.Contains(":=", StringComparison.Ordinal) ? ":=" : "=";
        var index = expr.IndexOf(marker, StringComparison.Ordinal);
        if (index <= 0)
            return false;

        var name = expr[..index].Trim();
        if (!name.StartsWith('$'))
            return false;

        var valueExpr = expr[(index + marker.Length)..].Trim();
        context.Variables[name] = EvaluatePipeline(valueExpr, context);
        return true;
    }

    private object? EvaluatePipeline(string expression, TemplateContext context)
    {
        var parts = SplitPipeline(expression);
        object? value = null;
        for (var i = 0; i < parts.Count; i++)
        {
            value = i == 0
                ? EvaluateExpression(parts[i], context, null, hasPipelineValue: false)
                : EvaluateExpression(parts[i], context, value, hasPipelineValue: true);
        }

        return value;
    }

    private object? EvaluateExpression(
        string expression,
        TemplateContext context,
        object? pipelineValue,
        bool hasPipelineValue)
    {
        expression = expression.Trim();
        if (expression.Length == 0)
            return pipelineValue;

        var tokens = SplitArguments(expression);
        if (tokens.Count == 0)
            return pipelineValue;

        var head = tokens[0];
        if (head.StartsWith(".Files.", StringComparison.Ordinal))
            return EvaluateFilesMethod(head, tokens, context);
        if (TryDispatchApiVersionsHas(head, tokens, context, pipelineValue, hasPipelineValue, out var methodResult))
            return methodResult;

        return head switch
        {
            // Template inclusion
            "include" => IncludeTemplate(tokens, context),
            "template" => IncludeTemplate(tokens, context),

            // Default / required / tpl
            "default" => CoreFunctions.Default(tokens, context, pipelineValue, this),
            "required" => CoreFunctions.Required(tokens, context, pipelineValue, this),
            "tpl" => CoreFunctions.Tpl(tokens, context, pipelineValue, this),
            "fail" => CoreFunctions.FnFail(tokens, context, pipelineValue, this),

            // String functions
            "quote" => StringHelpers.Quote(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "squote" => StringFunctions.Squote(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "cat" => CoreFunctions.Cat(tokens, context, pipelineValue, this),
            "indent" => StringHelpers.Indent(TypeConverters.ToTemplateString(pipelineValue), GetInt(tokens, 1, context), false),
            "nindent" => StringHelpers.Indent(TypeConverters.ToTemplateString(pipelineValue), GetInt(tokens, 1, context), true),
            "replace" => CoreFunctions.Replace(tokens, context, pipelineValue, this),
            "plural" => TextFunctions.Plural(tokens, context, pipelineValue, this),
            "snakecase" => StringFunctions.Snakecase(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "camelcase" => StringFunctions.Camelcase(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "kebabcase" => StringFunctions.Kebabcase(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "wrap" => TextFunctions.Wrap(tokens, context, pipelineValue, this),
            "wrapWith" => TextFunctions.WrapWith(tokens, context, pipelineValue, this),
            "initials" => StringFunctions.Initials(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "abbrev" => TextFunctions.Abbrev(tokens, context, pipelineValue, this),
            "trunc" => CoreFunctions.Trunc(tokens, context, pipelineValue, this),
            "abbrevinitial" => TextFunctions.Abbrevinitial(tokens, context, pipelineValue, this),
            "untitle" => TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)).ToLowerInvariant(),
            "title" => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "upper" => TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)).ToUpperInvariant(),
            "lower" => TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)).ToLowerInvariant(),
            "trim" => TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)).Trim(),
            "trimAll" => TextFunctions.TrimAll(tokens, context, pipelineValue, this),
            "trimSuffix" => CoreFunctions.TrimSuffix(tokens, context, pipelineValue, this),
            "trimPrefix" => CoreFunctions.TrimPrefix(tokens, context, pipelineValue, this),
            "contains" => CoreFunctions.Contains(tokens, context, pipelineValue, this),
            "hasPrefix" => TextFunctions.HasPrefix(tokens, context, pipelineValue, this),
            "hasSuffix" => TextFunctions.HasSuffix(tokens, context, pipelineValue, this),
            "repeat" => TextFunctions.Repeat(tokens, context, pipelineValue, this),
            "substr" => TextFunctions.Substr(tokens, context, pipelineValue, this),
            "toString" => TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "atoi" => int.TryParse(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)), out var ai) ? ai : 0,
            "nospace" => StringFunctions.Nospace(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "swapcase" => StringFunctions.Swapcase(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "shuffle" => StringFunctions.Shuffle(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "regexFind" => StringFunctions.RegexFind(
                TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context)),
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "regexFindAll" => StringFunctions.RegexFindAll(
                TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context)),
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "regexMatch" => StringFunctions.RegexMatch(
                TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context)),
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            // Sprig: regexReplaceAll PATTERN INPUT REPLACEMENT
            "regexReplaceAll" => StringFunctions.RegexReplaceAll(
                TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context)),
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context)),
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(pipelineValue != null ? 2 : 3), context))),
            "regexReplaceAllLiteral" => StringFunctions.RegexReplaceAllLiteral(
                TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context)),
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context)),
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(pipelineValue != null ? 2 : 3), context))),
            "regexSplit" => StringFunctions.RegexSplit(
                TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context)),
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "printf" => CoreFunctions.Printf(tokens, context, this),
            "println" => PrintArgs(tokens, context, pipelineValue, newline: true),
            "print" => PrintArgs(tokens, context, pipelineValue),

            // Math functions
            "add" => FoldMathArgs(tokens, context, pipelineValue, "+"),
            "sub" => FoldMathArgs(tokens, context, pipelineValue, "-"),
            "mul" => FoldMathArgs(tokens, context, pipelineValue, "*"),
            "div" => FoldMathArgs(tokens, context, pipelineValue, "/"),
            "mod" => FoldMathArgs(tokens, context, pipelineValue, "%"),
            "max" => MathFunctions.MathMax(ResolveDoubleList(tokens, context, pipelineValue)),
            "min" => MathFunctions.MathMin(ResolveDoubleList(tokens, context, pipelineValue)),
            "ceil" => MathFunctions.Ceil(ResolveDoubleArg(tokens, context, pipelineValue, 1)),
            "floor" => MathFunctions.Floor(ResolveDoubleArg(tokens, context, pipelineValue, 1)),
            "round" => MathFunctions.Round(ResolveDoubleArg(tokens, context, pipelineValue, 1),
                tokens.Count > 2 ? (int)TypeConverters.ToLong(EvaluateToken(tokens.ElementAtOrDefault(2), context)) : 0),
            "int64" => TypeConverters.ToLong(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "int" => (int)TypeConverters.ToLong(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "float64" => TypeConverters.ToDouble(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),

            // Date/time functions
            "now" => DateTimeOffset.UtcNow,
            "date" => DateTimeFunctions.Format(
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context)),
                pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context)),
            "dateInZone" => DateTimeFunctions.FormatInZone(
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context)),
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(2), context)),
                pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(3), context)),
            "duration" => DateTimeFunctions.Duration(
                (long)TypeConverters.ToLong(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "durationRound" => DateTimeFunctions.DurationRound(
                (long)TypeConverters.ToLong(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "unixEpoch" => TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),

            // Crypto / random functions
            "sha256sum" => StringHelpers.Sha256Sum(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "sha512sum" => EncodingHelpers.Sha512Sum(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "sha1sum" => EncodingHelpers.Sha1Sum(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "adler32sum" => EncodingHelpers.Adler32Sum(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "bcrypt" => EncodingHelpers.BCryptHash(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "uuidv4" => EncodingHelpers.UuidV4(),
            "randAlphaNum" => TextFunctions.RandString(tokens, context, "alphanum", this),
            "randAlpha" => TextFunctions.RandString(tokens, context, "alpha", this),
            "randNumeric" => TextFunctions.RandString(tokens, context, "numeric", this),
            "randAscii" => TextFunctions.RandString(tokens, context, "ascii", this),
            "randInt" => TextFunctions.RandInt(tokens, context, this),
            "genPrivateKey" => TextFunctions.GenPrivateKey(tokens, context, this),

            // Encoding functions
            "b64enc" => EncodingHelpers.Base64Encode(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "b64dec" => Encoding.UTF8.GetString(Convert.FromBase64String(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)))),
            "b32enc" => EncodingHelpers.Base32Encode(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "b32dec" => EncodingHelpers.Base32Decode(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),

            // Environment
            "env" => Environment.GetEnvironmentVariable(TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context))) ?? string.Empty,
            "expandenv" => EncodingHelpers.ExpandEnv(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),

            // Path functions
            "dir" => Path.GetDirectoryName(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))) ?? string.Empty,
            "base" => Path.GetFileName(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "ext" => Path.GetExtension(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "clean" => Path.GetFullPath(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "isAbs" => Path.IsPathRooted(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),

            // Semantic versioning
            "semver" => TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "semverCompare" => SemverFunctions.Satisfies(
                TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context)),
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context))),

            // Type / reflection
            "typeOf" => (pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))?.GetType().FullName ?? "nil",
            "typeIs" => TypeFunctions.TypeIs(tokens, context, pipelineValue),
            "typeIsLike" => TypeFunctions.TypeIsLike(tokens, context, pipelineValue),
            "kindOf" => TypeFunctions.KindOf(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "kindIs" => TypeFunctions.KindIs(tokens, context, pipelineValue),
            "deepEqual" => TypeFunctions.DeepEquals(
                pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context),
                EvaluateToken(tokens.ElementAtOrDefault(2), context)),

            // List functions
            "list" => tokens.Skip(1).Select(t => EvaluateToken(t, context)).ToList(),
            "tuple" => tokens.Skip(1).Select(t => EvaluateToken(t, context)).ToList(),
            "first" => CollectionsHelpers.First(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "last" => CollectionsHelpers.Last(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "rest" => CollectionsHelpers.Rest(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "initial" => CollectionsHelpers.Initial(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "prepend" => CoreFunctions.Prepend(tokens, context, pipelineValue, this),
            "append" => CoreFunctions.Append(tokens, context, pipelineValue, this),
            "mustAppend" => CoreFunctions.Append(tokens, context, pipelineValue, this),
            "join" => CollectionsHelpers.Join(
                pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context),
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            // Sprig: split returns dict {_0, _1, …}; splitList returns list
            "split" => CollectionsHelpers.Split(
                TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context)),
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "splitList" => CollectionsHelpers.SplitList(
                TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context)),
                TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "slice" => CollectionsHelpers.Slice(
                pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context),
                (int)TypeConverters.ToLong(EvaluateToken(tokens.ElementAtOrDefault(pipelineValue != null ? 1 : 2), context)),
                tokens.Count > (pipelineValue != null ? 2 : 3)
                    ? (int)TypeConverters.ToLong(EvaluateToken(tokens.ElementAtOrDefault(pipelineValue != null ? 2 : 3), context))
                    : null),
            "until" => TextFunctions.Until(
                (int)TypeConverters.ToLong(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "untilStep" => ResolveUntilStep(tokens, context, pipelineValue),
            "reverse" => CollectionsHelpers.Reverse(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "mustReverse" => CollectionsHelpers.MustReverse(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "sortAlpha" => CollectionsHelpers.SortAlpha(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "mustSortAlpha" => CollectionsHelpers.MustSortAlpha(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "compact" => CollectionsHelpers.Compact(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "mustCompact" => CollectionsHelpers.MustCompact(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "uniq" => CollectionsHelpers.Uniq(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "mustUniq" => CollectionsHelpers.MustUniq(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "without" => CoreFunctions.Without(tokens, context, pipelineValue, this),
            "mustWithout" => CoreFunctions.Without(tokens, context, pipelineValue, this),
            "has" => CoreFunctions.Has(tokens, context, pipelineValue, this),
            "mustHas" => CoreFunctions.Has(tokens, context, pipelineValue, this),
            "concat" => CoreFunctions.Concat(tokens, context, pipelineValue, this),
            "mustPrepend" => CoreFunctions.Prepend(tokens, context, pipelineValue, this),

            // Dict functions
            "dict" => DictFunctions.Dict(tokens, context, this),
            "get" => DictFunctions.Get(tokens, context, pipelineValue, this),
            "set" => DictFunctions.Set(tokens, context, pipelineValue, this),
            "unset" => DictFunctions.Unset(tokens, context, pipelineValue, this),
            "hasKey" => DictFunctions.HasKey(tokens, context, pipelineValue, this),
            "keys" => CollectionsHelpers.Keys(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "values" => CollectionsHelpers.Values(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "merge" => DictFunctions.MergeDicts(tokens, context, this),
            "mustMerge" => DictFunctions.MergeDicts(tokens, context, this),
            "mergeOverwrite" => CollectionsHelpers.MergeOverwrite(
                tokens.Skip(1).Select(t => EvaluateToken(t, context)).ToList()),
            "mustMergeOverwrite" => CollectionsHelpers.MergeOverwrite(
                tokens.Skip(1).Select(t => EvaluateToken(t, context)).ToList()),
            "deepCopy" => CollectionsHelpers.DeepCopy(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "mustDeepCopy" => CollectionsHelpers.DeepCopy(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "toDecimal" => SerializationFunctions.ToDecimal(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "toRawJson" => SerializationFunctions.ToRawJson(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "pick" => DictFunctions.Pick(tokens, context, pipelineValue, this),
            "omit" => DictFunctions.Omit(tokens, context, pipelineValue, this),
            "pluck" => DictFunctions.Pluck(tokens, context, pipelineValue, this),
            "dig" => DictFunctions.Dig(tokens, context, pipelineValue, this),

            // Index (list/dict access)
            "index" => DictFunctions.Index(tokens, context, pipelineValue, this),

            // Ternary / coalesce / logic
            "ternary" => CoreFunctions.Ternary(tokens, context, pipelineValue, this),
            "coalesce" => tokens.Skip(1).Select(t => EvaluateToken(t, context)).FirstOrDefault(TypeConverters.IsTruthy),
            "empty" => !TypeConverters.IsTruthy(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "not" => !TypeConverters.IsTruthy(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "and" => tokens.Skip(1).All(t => TypeConverters.IsTruthy(EvaluateToken(t, context))),
            "or" => tokens.Skip(1).Any(t => TypeConverters.IsTruthy(EvaluateToken(t, context))),

            // Comparison
            "eq" => CoreFunctions.Eq(tokens, context, pipelineValue, this),
            "ne" => !TypeConverters.IsTruthy(CoreFunctions.Eq(tokens, context, pipelineValue, this)),
            "lt" => CoreFunctions.CompareOp(tokens, context, pipelineValue, (a, b) => a < 0, this),
            "gt" => CoreFunctions.CompareOp(tokens, context, pipelineValue, (a, b) => a > 0, this),
            "le" => CoreFunctions.CompareOp(tokens, context, pipelineValue, (a, b) => a <= 0, this),
            "ge" => CoreFunctions.CompareOp(tokens, context, pipelineValue, (a, b) => a >= 0, this),

            // JSON / YAML / TOML
            "toJson" => SerializationFunctions.ToJson(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "fromJson" => SerializationFunctions.FromJson(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "toPrettyJson" => SerializationFunctions.ToPrettyJson(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "toYaml" => HelmYaml.Serialize(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)).TrimEnd(),
            "fromYaml" => HelmYaml.DeserializeDictionary(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),

            // Lookup
            "lookup" => DictFunctions.Lookup(tokens, context, this),

            // Len
            "len" => TypeFunctions.GetLength(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),

            // Auto
            "auto" => "auto",

            _ => pipelineValue is not null && tokens.Count == 1
                ? ApplySimpleFunction(head, pipelineValue, context)
                : tokens.Count > 1
                    ? throw new NotSupportedException($"Helm template function '{head}' is not supported by the managed renderer.")
                    : IsResolvableTokenExpression(expression)
                        ? EvaluateToken(expression, context)
                        : throw new NotSupportedException($"Helm template function '{head}' is not supported by the managed renderer.")
        };
    }

    private static bool IsResolvableTokenExpression(string expression)
    {
        var token = expression.Trim();
        if (token.Length == 0)
            return true;
        if (token is "true" or "false" or "nil")
            return true;
        if (token.StartsWith('"') || token.StartsWith('\'') || token.StartsWith('.') || token.StartsWith('$') || token.StartsWith('('))
            return true;
        return long.TryParse(token, out _) ||
               double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private object? IncludeTemplate(IReadOnlyList<string> tokens, TemplateContext context)
    {
        var name = TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context));
        if (!_definedTemplates.TryGetValue(name, out var body))
            throw new NotSupportedException($"Included template '{name}' was not found.");

        try
        {
            var rendered = RenderSection(body, CreateTemplateInvocationContext(tokens, context));
            // In Go templates, include returns the trimmed result
            return rendered.Trim();
        }
        catch (TemplateParseException)
        {
            // Preserve the exception type so the per-template error collection
            // in Render() can catch and collect it, rather than aborting the
            // entire chart render. See #63.
            throw;
        }
        catch (NotSupportedException)
        {
            // Likewise: NotSupportedException is also collected per-template
            // in Render() for engine-level gaps.
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Error rendering included template '{name}': {ex.Message}", ex);
        }
    }

    private TemplateContext CreateTemplateInvocationContext(IReadOnlyList<string> tokens, TemplateContext context)
    {
        if (tokens.Count <= 2)
            return context;

        return context with
        {
            Dot = EvaluateToken(tokens[2], context),
            Variables = new Dictionary<string, object?>(context.Variables, StringComparer.Ordinal)
        };
    }

    private object? ApplySimpleFunction(string function, object? value, TemplateContext context)
    {
        return function switch
        {
            "quote" => StringHelpers.Quote(value),
            "squote" => StringFunctions.Squote(value),
            "toYaml" => HelmYaml.Serialize(value).TrimEnd(),
            "toJson" => SerializationFunctions.ToJson(value),
            "lower" => TypeConverters.ToTemplateString(value).ToLowerInvariant(),
            "upper" => TypeConverters.ToTemplateString(value).ToUpperInvariant(),
            "b64enc" => EncodingHelpers.Base64Encode(value),
            "b64dec" => Encoding.UTF8.GetString(Convert.FromBase64String(TypeConverters.ToTemplateString(value))),
            "trim" => TypeConverters.ToTemplateString(value).Trim(),
            "sha256sum" => StringHelpers.Sha256Sum(TypeConverters.ToTemplateString(value)),
            "sha512sum" => EncodingHelpers.Sha512Sum(TypeConverters.ToTemplateString(value)),
            "sha1sum" => EncodingHelpers.Sha1Sum(TypeConverters.ToTemplateString(value)),
            "not" => !TypeConverters.IsTruthy(value),
            "empty" => !TypeConverters.IsTruthy(value),
            "len" => TypeFunctions.GetLength(value),
            "keys" => CollectionsHelpers.Keys(value),
            "values" => CollectionsHelpers.Values(value),
            "first" => CollectionsHelpers.First(value),
            "last" => CollectionsHelpers.Last(value),
            "rest" => CollectionsHelpers.Rest(value),
            "initial" => CollectionsHelpers.Initial(value),
            "reverse" => CollectionsHelpers.Reverse(value),
            "mustReverse" => CollectionsHelpers.MustReverse(value),
            "sortAlpha" => CollectionsHelpers.SortAlpha(value),
            "mustSortAlpha" => CollectionsHelpers.MustSortAlpha(value),
            "compact" => CollectionsHelpers.Compact(value),
            "mustCompact" => CollectionsHelpers.MustCompact(value),
            "uniq" => CollectionsHelpers.Uniq(value),
            "mustUniq" => CollectionsHelpers.MustUniq(value),
            "deepCopy" => CollectionsHelpers.DeepCopy(value),
            "mustDeepCopy" => CollectionsHelpers.DeepCopy(value),
            "nospace" => StringFunctions.Nospace(TypeConverters.ToTemplateString(value)),
            "swapcase" => StringFunctions.Swapcase(TypeConverters.ToTemplateString(value)),
            "shuffle" => StringFunctions.Shuffle(TypeConverters.ToTemplateString(value)),
            "uuidv4" => EncodingHelpers.UuidV4(),
            "toDecimal" => SerializationFunctions.ToDecimal(value),
            "toRawJson" => SerializationFunctions.ToRawJson(value),
            "typeOf" => value?.GetType().FullName ?? "nil",
            "kindOf" => TypeFunctions.KindOf(value),
            "toString" => TypeConverters.ToTemplateString(value),
            "int64" => TypeConverters.ToLong(value),
            "int" => (int)TypeConverters.ToLong(value),
            "float64" => TypeConverters.ToDouble(value),
            "title" => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(TypeConverters.ToTemplateString(value)),
            "untitle" => TypeConverters.ToTemplateString(value).ToLowerInvariant(),
            "snakecase" => StringFunctions.Snakecase(TypeConverters.ToTemplateString(value)),
            "camelcase" => StringFunctions.Camelcase(TypeConverters.ToTemplateString(value)),
            "kebabcase" => StringFunctions.Kebabcase(TypeConverters.ToTemplateString(value)),
            "print" => TypeConverters.ToTemplateString(value),
            "tuple" => value,
            _ => throw new NotSupportedException($"Helm template function '{function}' is not supported by the managed renderer.")
        };
    }

    // Go fmt.Sprint spacing: adjacent strings concatenate without spaces;
    // non-string operands get spaces between them. Pipeline value is LAST.
    // e.g. {{ print .Release.Name "-svc" }} → "rel-svc" (string+string)
    // e.g. {{ print 1 2 }} → "1 2" (non-string+non-string)
    private string PrintArgs(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, bool newline = false)
    {
        var values = new List<object?>();
        foreach (var t in tokens.Skip(1))
            values.Add(EvaluateToken(t, context));
        if (pipelineValue != null)
            values.Add(pipelineValue);

        if (values.Count == 0)
            return newline ? "\n" : string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.Append(TypeConverters.ToTemplateString(values[0]));
        for (var i = 1; i < values.Count; i++)
        {
            var prevIsString = values[i - 1] is string;
            var currIsString = values[i] is string;
            if (!prevIsString && !currIsString)
                sb.Append(' ');
            sb.Append(TypeConverters.ToTemplateString(values[i]));
        }
        if (newline) sb.Append('\n');
        return sb.ToString();
    }

    private object? EvaluateToken(string? token, TemplateContext context)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        token = token.Trim();
        if (token.StartsWith('"') && token.EndsWith('"'))
            return StringHelpers.Unquote(token);
        if (token.StartsWith('\'') && token.EndsWith('\''))
            return token[1..^1];
        if (token.StartsWith('('))
        {
            var memberSeparator = token.LastIndexOf(").", StringComparison.Ordinal);
            if (memberSeparator > 0)
            {
                var value = EvaluatePipeline(token[1..memberSeparator].Trim(), context);
                return ResolveMembers(value, token[(memberSeparator + 2)..]);
            }
            if (token.EndsWith(')'))
                return EvaluatePipeline(token[1..^1].Trim(), context);
        }
        if (token == ".")
            return context.Dot;
        if (token == "true") return true;
        if (token == "false") return false;
        if (token == "nil") return null;
        if (long.TryParse(token, out var l)) return l;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        if (token == "$")
            return CreateRootDot(context);
        if (token.StartsWith("$.", StringComparison.Ordinal))
            return ResolvePath("." + token[2..], context);
        if (token.StartsWith('$'))
            return ResolveVariable(token, context);
        if (token.StartsWith('.'))
            return ResolvePath(token, context);

        return token;
    }

    /// <summary>
    /// Builds the full root dictionary for <c>$</c> lookups, matching Helm's
    /// top-level template object ($.Values, $.Release, $.Chart, $.Capabilities,
    /// $.Files, $.Template).
    /// </summary>
    private static Dictionary<string, object?> CreateRootDot(TemplateContext context)
        => new(StringComparer.Ordinal)
        {
            ["Values"] = context.Values,
            ["Release"] = BuildReleaseDict(context),
            ["Chart"] = BuildChartDict(context),
            ["Capabilities"] = new Dictionary<string, object?>
            {
                ["KubeVersion"] = ToTemplateKubeVersion(context.KubeVersion),
                ["APIVersions"] = context.ApiVersions ?? GetDefaultApiVersions(context.KubeVersion),
                ["HelmVersion"] = new Dictionary<string, object?>
                {
                    ["Version"] = "HelmSharp 0.3.0",
                    ["GitCommit"] = "managed",
                    ["GitTreeState"] = "clean",
                    ["GoVersion"] = "dotnet/9.0"
                }
            },
            ["Files"] = new TemplateFiles(context.Chart.Files),
            ["Template"] = new Dictionary<string, object?>
            {
                ["Name"] = $"{context.TemplateChartPath ?? context.Chart.Name}/{context.CurrentTemplatePath}",
                ["BasePath"] = $"{context.TemplateChartPath ?? context.Chart.Name}/templates"
            }
        };

    private object? ResolvePath(string token, TemplateContext context)
    {
        var parts = token.TrimStart('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        object? current = parts.FirstOrDefault() switch
        {
            "Values" => context.Values,
            "Chart" => BuildChartDict(context),
            "Release" => BuildReleaseDict(context),
            "Capabilities" => new Dictionary<string, object?>
            {
                ["KubeVersion"] = ToTemplateKubeVersion(context.KubeVersion),
                ["APIVersions"] = context.ApiVersions ?? GetDefaultApiVersions(context.KubeVersion),
                ["HelmVersion"] = new Dictionary<string, object?>
                {
                    ["Version"] = "HelmSharp 0.3.0",
                    ["GitCommit"] = "managed",
                    ["GitTreeState"] = "clean",
                    ["GoVersion"] = "dotnet/9.0"
                }
            },
            "Files" => new TemplateFiles(context.Chart.Files),
            "Template" => new Dictionary<string, object?>
            {
                ["Name"] = $"{context.TemplateChartPath ?? context.Chart.Name}/{context.CurrentTemplatePath}",
                ["BasePath"] = $"{context.TemplateChartPath ?? context.Chart.Name}/templates"
            },
            _ => context.Dot
        };

        var skip = parts.FirstOrDefault() is "Values" or "Chart" or "Release" or "Capabilities" or "Files" or "Template" ? 1 : 0;
        return ResolveMembers(current, parts.Skip(skip));
    }

    private static Dictionary<string, object?> BuildChartDict(TemplateContext context)
        => new(StringComparer.Ordinal)
        {
            ["APIVersion"] = context.Chart.ApiVersion,
            ["Name"] = context.TemplateChartName ?? context.Chart.Name,
            ["Version"] = context.Chart.Version,
            ["AppVersion"] = context.Chart.AppVersion ?? string.Empty,
            ["Description"] = context.Chart.Description ?? string.Empty,
            ["Home"] = context.Chart.Home ?? string.Empty,
            ["Sources"] = context.Chart.Sources ?? new List<object?>(),
            ["Keywords"] = context.Chart.Keywords ?? new List<object?>(),
            ["Maintainers"] = context.Chart.Maintainers?
                .Select(ToTemplateMaintainer)
                .Cast<object?>()
                .ToList() ?? new List<object?>(),
            ["Type"] = context.Chart.Type ?? "application",
            ["Deprecated"] = context.Chart.Deprecated,
            ["KubeVersion"] = context.Chart.KubeVersion ?? string.Empty,
            ["Annotations"] = context.Chart.Annotations ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            ["Dependencies"] = context.Dependencies.Select(ToTemplateDependency).ToList()
        };

    private static Dictionary<string, object?> BuildReleaseDict(TemplateContext context)
        => new(StringComparer.Ordinal)
        {
            ["Name"] = context.ReleaseName,
            ["Namespace"] = context.ReleaseNamespace,
            ["Service"] = "Helm",
            ["IsInstall"] = context.IsInstall,
            ["IsUpgrade"] = context.IsUpgrade,
            ["Revision"] = context.Revision
        };

    private static object? ResolveMembers(object? current, string path)
        => ResolveMembers(current, path.Split('.', StringSplitOptions.RemoveEmptyEntries));

    private static object? ResolveMembers(object? current, IEnumerable<string> parts)
    {
        foreach (var part in parts)
        {
            current = current switch
            {
                ApiVersionSet => null,
                TemplateFiles files when part.Equals("AsConfig", StringComparison.Ordinal) => files.AsConfig(),
                TemplateFiles files when part.Equals("AsSecrets", StringComparison.Ordinal) => files.AsSecrets(),
                Dictionary<string, object?> dict when dict.TryGetValue(part, out var next) => next,
                IDictionary<string, object?> dict when dict.TryGetValue(part, out var next) => next,
                _ => null
            };
        }

        return current;
    }

    private object? EvaluateFilesMethod(
        string head,
        IReadOnlyList<string> tokens,
        TemplateContext context)
    {
        var path = TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context));
        return head switch
        {
            ".Files.Get" => context.Chart.Files.TryGetValue(path, out var content)
                ? Encoding.UTF8.GetString(content)
                : string.Empty,
            ".Files.GetBytes" => context.Chart.Files.TryGetValue(path, out var content)
                ? content
                : Array.Empty<byte>(),
            ".Files.Lines" => context.Chart.Files.TryGetValue(path, out var content)
                ? HelmCliRunnerCompatibleLines(Encoding.UTF8.GetString(content))
                : new List<object?>(),
            ".Files.Glob" => new TemplateFiles(context.Chart.Files).Glob(path),
            ".Files.AsConfig" => new TemplateFiles(context.Chart.Files).AsConfig(),
            ".Files.AsSecrets" => new TemplateFiles(context.Chart.Files).AsSecrets(),
            _ => throw new NotSupportedException($"Helm files method '{head}' is not supported by the managed renderer.")
        };
    }

    private static List<object?> HelmCliRunnerCompatibleLines(string content)
    {
        var normalized = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        return normalized.Split('\n').Cast<object?>().ToList();
    }

    private sealed class TemplateFiles
    {
        private static readonly Regex NumericScalarRegex = new(
            @"^[+-]?(?:[0-9][0-9_]*)(?:\.[0-9_]+)?(?:[eE][+-]?[0-9]+)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex DateLikeScalarRegex = new(
            @"^[0-9]{4}-[0-9]{2}-[0-9]{2}(?:[Tt ].*)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly Dictionary<string, byte[]> _files;

        public TemplateFiles(IDictionary<string, byte[]> files)
        {
            _files = files.ToDictionary(
                pair => NormalizeFilePath(pair.Key),
                pair => pair.Value,
                StringComparer.Ordinal);
        }

        private TemplateFiles(Dictionary<string, byte[]> files)
        {
            _files = files;
        }

        public TemplateFiles Glob(string pattern)
        {
            var regex = GlobToRegex(NormalizeFilePath(pattern));
            var matches = _files
                .Where(pair => regex.IsMatch(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return new TemplateFiles(matches);
        }

        public string AsConfig()
            => RenderYamlMap(encodeValues: false);

        public string AsSecrets()
            => RenderYamlMap(encodeValues: true);

        private string RenderYamlMap(bool encodeValues)
        {
            var builder = new StringBuilder();
            foreach (var (path, content) in _files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var key = Path.GetFileName(path);
                var value = encodeValues
                    ? Convert.ToBase64String(content)
                    : Encoding.UTF8.GetString(content);
                AppendYamlEntry(builder, key, value);
            }

            return builder.ToString().TrimEnd();
        }

        private static void AppendYamlEntry(StringBuilder builder, string key, string value)
        {
            if (value.Length == 0)
            {
                builder.Append(key).AppendLine(": \"\"");
                return;
            }

            // Files with CR (\r\n or \r) → quoted scalar with escaped control chars (Helm parity)
            if (value.Contains('\r', StringComparison.Ordinal))
            {
                builder.Append(key).Append(": ").AppendLine(EscapeYamlScalar(value));
                return;
            }

            // Files with LF only → block scalar (Helm parity)
            if (value.Contains('\n', StringComparison.Ordinal))
            {
                builder.Append(key).AppendLine(": |");
                var lines = value.Split('\n');
                if (lines.Length > 0 && lines[^1].Length == 0)
                    lines = lines[..^1];
                foreach (var line in lines)
                    builder.Append("  ").AppendLine(line);
                return;
            }

            builder.Append(key).Append(": ").AppendLine(EscapeYamlScalar(value));
        }

        private static string EscapeYamlScalar(string value)
            => NeedsQuotedScalar(value)
                ? "\"" + value
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)
                    .Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal)
                    .Replace("\t", "\\t", StringComparison.Ordinal) + "\""
                : value;

        private static bool NeedsQuotedScalar(string value)
            => value.Length == 0 ||
               char.IsWhiteSpace(value[0]) ||
               char.IsWhiteSpace(value[^1]) ||
               value.Contains('\r', StringComparison.Ordinal) ||
               value.Contains('\n', StringComparison.Ordinal) ||
               value.Contains('\t', StringComparison.Ordinal) ||
               value.Contains(':', StringComparison.Ordinal) ||
               value.Contains('#', StringComparison.Ordinal) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("~", StringComparison.Ordinal) ||
               value.Equals(".inf", StringComparison.OrdinalIgnoreCase) ||
               value.Equals(".nan", StringComparison.OrdinalIgnoreCase) ||
               NumericScalarRegex.IsMatch(value) ||
               DateLikeScalarRegex.IsMatch(value);

        private static Regex GlobToRegex(string pattern)
        {
            var builder = new StringBuilder("^");
            for (var i = 0; i < pattern.Length; i++)
            {
                var ch = pattern[i];
                if (ch == '*')
                {
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        builder.Append(".*");
                        i++;
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }
                    continue;
                }

                if (ch == '?')
                {
                    builder.Append("[^/]");
                    continue;
                }

                builder.Append(Regex.Escape(ch.ToString()));
            }

            builder.Append('$');
            return new Regex(builder.ToString(), RegexOptions.CultureInvariant);
        }

        private static string NormalizeFilePath(string path)
            => path.Replace('\\', '/').TrimStart('/');
    }


    private bool TryDispatchApiVersionsHas(
        string head,
        IReadOnlyList<string> tokens,
        TemplateContext context,
        object? pipelineValue,
        bool hasPipelineValue,
        out object? result)
    {
        result = null;
        var lastDot = head.LastIndexOf('.');
        if (lastDot <= 0)
            return false;

        var method = head[(lastDot + 1)..];
        if (!method.Equals("Has", StringComparison.Ordinal))
            return false;

        var receiverToken = head[..lastDot];
        var receiver = EvaluateToken(receiverToken, context);
        if (receiver is not ApiVersionSet versionSet)
            return false;

        var explicitArgCount = tokens.Count - 1;
        var totalArgCount = explicitArgCount + (hasPipelineValue ? 1 : 0);
        if (totalArgCount != 1)
            throw new InvalidOperationException(
                $"wrong number of args for Has: want 1 got {totalArgCount}");

        var argToken = explicitArgCount == 1 ? tokens[1] : null;
        var argValue = argToken is null ? pipelineValue : EvaluateToken(argToken, context);
        if (argValue is null)
            throw new InvalidOperationException("invalid value; expected string");
        result = versionSet.Has(TypeConverters.ToTemplateString(argValue));
        return true;
    }

    private static ApiVersionSet BuildApiVersions(IEnumerable<string>? apiVersions, string? kubeVersion)
    {
        var defaultVersions = GetDefaultApiVersions(kubeVersion);
        var customVersions = apiVersions ?? [];
        var versions = defaultVersions
            .Concat<object?>(customVersions)
            .DistinctBy(static v => v?.ToString(), StringComparer.Ordinal)
            .ToList();
        return new ApiVersionSet(versions);
    }

    private static Dictionary<string, object?> ToTemplateKubeVersion(string? version)
    {
        var normalized = NormalizeKubeVersion(version);
        var coreVersion = normalized.TrimStart('v');
        var suffixIndex = coreVersion.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
            coreVersion = coreVersion[..suffixIndex];
        var parts = coreVersion.Split('.');

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Version"] = normalized,
            ["Major"] = parts[0],
            ["Minor"] = parts[1],
            ["GitVersion"] = normalized
        };
    }

    private static string NormalizeKubeVersion(string? version)
    {
        const string defaultVersion = "v1.29.0";
        if (string.IsNullOrWhiteSpace(version))
            return defaultVersion;

        var match = Regex.Match(
            version.Trim(),
            @"^[vV]?(?<major>0|[1-9]\d*)(?:\.(?<minor>0|[1-9]\d*))?(?:\.(?<patch>0|[1-9]\d*))?(?<prerelease>-[0-9A-Za-z.-]+)?(?<build>\+[0-9A-Za-z.-]+)?$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new ArgumentException(
                $"Kubernetes version '{version}' is not a valid semantic version.",
                nameof(version));

        var major = match.Groups["major"].Value;
        var minor = match.Groups["minor"].Success ? match.Groups["minor"].Value : "0";
        var patch = match.Groups["patch"].Success ? match.Groups["patch"].Value : "0";
        return $"v{major}.{minor}.{patch}"
               + match.Groups["prerelease"].Value
               + match.Groups["build"].Value;
    }

    private static Dictionary<string, object?> ToTemplateDependency(HelmChartDependency dependency)
        => new(StringComparer.Ordinal)
        {
            ["Name"] = dependency.Name,
            ["Version"] = dependency.Version ?? string.Empty,
            ["Repository"] = dependency.Repository ?? string.Empty,
            ["Condition"] = dependency.Condition ?? string.Empty,
            ["Tags"] = dependency.Tags ?? new List<string>(),
            ["Enabled"] = dependency.Enabled,
            ["ImportValues"] = dependency.ImportValues ?? new List<object?>(),
            ["Alias"] = dependency.Alias ?? string.Empty
        };

    private static List<HelmChartDependency> BuildEffectiveDependencies(
        IEnumerable<HelmChartDependency> dependencies,
        IDictionary<string, object?> values)
    {
        var tags = values.TryGetValue("tags", out var tagsValue)
            ? tagsValue as IDictionary<string, object?>
            : null;
        var result = new List<HelmChartDependency>();

        foreach (var dependency in dependencies)
        {
            var enabled = dependency.Enabled;
            var tagOverride = EvaluateDependencyTags(dependency.Tags, tags);
            if (tagOverride.HasValue)
                enabled = tagOverride.Value;

            foreach (var condition in (dependency.Condition ?? string.Empty)
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryGetBooleanPath(values, condition, out var conditionValue))
                {
                    enabled = conditionValue;
                    break;
                }
            }

            if (!enabled)
                continue;

            result.Add(new HelmChartDependency
            {
                Name = dependency.Alias ?? dependency.Name,
                Version = dependency.Version,
                Repository = dependency.Repository,
                Condition = dependency.Condition,
                Tags = dependency.Tags?.ToList(),
                Enabled = true,
                ImportValues = dependency.ImportValues?.ToList(),
                Alias = dependency.Alias
            });
        }

        return result;
    }

    private static bool? EvaluateDependencyTags(
        IEnumerable<string>? dependencyTags,
        IDictionary<string, object?>? valuesTags)
    {
        var hasTrue = false;
        var hasFalse = false;

        foreach (var tag in dependencyTags ?? [])
        {
            if (valuesTags?.TryGetValue(tag, out var value) != true || value is not bool enabled)
                continue;

            if (enabled)
                hasTrue = true;
            else
                hasFalse = true;
        }

        if (hasTrue)
            return true;
        if (hasFalse)
            return false;
        return null;
    }

    private static bool TryGetBooleanPath(
        IDictionary<string, object?> values,
        string path,
        out bool result)
    {
        object? current = values;
        foreach (var part in path.Split(
                     '.',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current is not IDictionary<string, object?> dictionary ||
                !dictionary.TryGetValue(part, out current))
            {
                result = false;
                return false;
            }
        }

        if (current is bool boolean)
        {
            result = boolean;
            return true;
        }

        result = false;
        return false;
    }

    private static Dictionary<string, object?> ToTemplateMaintainer(object? maintainer)
    {
        if (maintainer is not IDictionary<string, object?> fields)
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Name"] = fields.TryGetValue("name", out var name) ? name : string.Empty,
            ["Email"] = fields.TryGetValue("email", out var email) ? email : string.Empty,
            ["URL"] = fields.TryGetValue("url", out var url) ? url : string.Empty
        };
    }

    /// <summary>
    /// Catalog of Kubernetes API versions with removal metadata.
    /// Each entry maps an API version string to the Kubernetes minor version
    /// where it was removed (or null if still available in the latest release).
    /// This is used to derive the default API version list for a given kubeVersion.
    /// </summary>
    private static readonly (string Version, int? RemovedMinor)[] ApiVersionCatalog =
    [
        ("v1", null),
        ("admissionregistration.k8s.io/v1", null),
        ("admissionregistration.k8s.io/v1alpha1", null),
        ("admissionregistration.k8s.io/v1beta1", 22),
        ("internal.apiserver.k8s.io/v1alpha1", null),
        ("apps/v1", null),
        ("apps/v1beta1", 16),
        ("apps/v1beta2", 16),
        ("authentication.k8s.io/v1", null),
        ("authentication.k8s.io/v1alpha1", null),
        ("authentication.k8s.io/v1beta1", 22),
        ("authorization.k8s.io/v1", null),
        ("authorization.k8s.io/v1beta1", 22),
        ("autoscaling/v1", null),
        ("autoscaling/v2", null),
        ("autoscaling/v2beta1", 26),
        ("autoscaling/v2beta2", 26),
        ("batch/v1", null),
        ("batch/v1beta1", 25),
        ("certificates.k8s.io/v1", null),
        ("certificates.k8s.io/v1beta1", 22),
        ("certificates.k8s.io/v1alpha1", null),
        ("coordination.k8s.io/v1beta1", 22),
        ("coordination.k8s.io/v1", null),
        ("discovery.k8s.io/v1", null),
        ("discovery.k8s.io/v1beta1", 25),
        ("events.k8s.io/v1", null),
        ("events.k8s.io/v1beta1", 25),
        ("extensions/v1beta1", 22),
        ("flowcontrol.apiserver.k8s.io/v1", null),
        ("flowcontrol.apiserver.k8s.io/v1alpha1", null),
        ("flowcontrol.apiserver.k8s.io/v1beta1", 26),
        ("flowcontrol.apiserver.k8s.io/v1beta2", 29),
        ("flowcontrol.apiserver.k8s.io/v1beta3", 32),
        ("networking.k8s.io/v1", null),
        ("networking.k8s.io/v1alpha1", 27),
        ("networking.k8s.io/v1beta1", 22),
        ("node.k8s.io/v1", null),
        ("node.k8s.io/v1alpha1", null),
        ("node.k8s.io/v1beta1", 25),
        ("policy/v1", null),
        ("policy/v1beta1", 25),
        ("rbac.authorization.k8s.io/v1", null),
        ("rbac.authorization.k8s.io/v1beta1", 22),
        ("rbac.authorization.k8s.io/v1alpha1", 25),
        ("scheduling.k8s.io/v1alpha1", 25),
        ("scheduling.k8s.io/v1beta1", 22),
        ("scheduling.k8s.io/v1", null),
        ("storage.k8s.io/v1beta1", 22),
        ("storage.k8s.io/v1", null),
        ("storage.k8s.io/v1alpha1", 27),
        ("apiextensions.k8s.io/v1beta1", 22),
        ("apiextensions.k8s.io/v1", null),
    ];

    /// <summary>
    /// Cached unfiltered set of all known API versions.
    /// Used when kubeVersion is null or empty (backward-compatible path).
    /// </summary>
    private static readonly ApiVersionSet AllApiVersions =
        new(ApiVersionCatalog.Select(x => (object?)x.Version));

    /// <summary>
    /// Returns the default API version set filtered by the configured Kubernetes
    /// version. When <paramref name="kubeVersion"/> is null or empty, all known
    /// API versions are included (backward-compatible behavior). When a version
    /// is specified, API versions that were removed at or before that version
    /// are excluded.
    /// </summary>
    private static ApiVersionSet GetDefaultApiVersions(string? kubeVersion)
    {
        if (string.IsNullOrWhiteSpace(kubeVersion))
            return AllApiVersions;

        var minor = ParseKubeMinorVersion(kubeVersion);
        var versions = ApiVersionCatalog
            .Where(x => x.RemovedMinor == null || minor < x.RemovedMinor.Value)
            .Select(x => (object?)x.Version)
            .ToList();
        return new ApiVersionSet(versions);
    }

    /// <summary>
    /// Extracts the minor version number from a kubeVersion string like
    /// "v1.29.0" or "1.30.5", returning 29 or 30 respectively.
    /// </summary>
    private static int ParseKubeMinorVersion(string kubeVersion)
    {
        var normalized = NormalizeKubeVersion(kubeVersion);
        var core = normalized.TrimStart('v');
        var dotIndex = core.IndexOf('.');
        var secondDotIndex = core.IndexOf('.', dotIndex + 1);
        var minorSlice = secondDotIndex > dotIndex
            ? core[(dotIndex + 1)..secondDotIndex]
            : core[(dotIndex + 1)..];
        if (int.TryParse(minorSlice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor))
            return minor;
        throw new ArgumentException(
            $"Cannot parse minor version from kubeVersion '{kubeVersion}'.",
            nameof(kubeVersion));
    }

    private static object? ResolveVariable(string token, TemplateContext context)
    {
        var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !context.Variables.TryGetValue(parts[0], out var current))
            return null;

        foreach (var part in parts.Skip(1))
        {
            current = current switch
            {
                Dictionary<string, object?> dict when dict.TryGetValue(part, out var next) => next,
                IDictionary<string, object?> dict when dict.TryGetValue(part, out var next) => next,
                _ => null
            };
        }

        return current;
    }

    private static List<string> SplitPipeline(string expression)
        => SplitByTopLevel(expression, '|');

    private static List<string> SplitArguments(string expression)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var quote = '\0';
        var parenDepth = 0;
        foreach (var ch in expression)
        {
            if ((ch == '"' || ch == '\'') && (!inQuote || quote == ch) && parenDepth == 0)
            {
                inQuote = !inQuote;
                quote = inQuote ? ch : '\0';
                current.Append(ch);
                continue;
            }

            if (ch == '(' && !inQuote)
            {
                parenDepth++;
                current.Append(ch);
                continue;
            }
            if (ch == ')' && !inQuote)
            {
                parenDepth--;
                current.Append(ch);
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuote && parenDepth == 0)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static List<string> SplitByTopLevel(string expression, char separator)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var quote = '\0';
        var parenDepth = 0;
        foreach (var ch in expression)
        {
            if ((ch == '"' || ch == '\'') && (!inQuote || quote == ch) && parenDepth == 0)
            {
                inQuote = !inQuote;
                quote = inQuote ? ch : '\0';
            }

            if (!inQuote)
            {
                if (ch == '(')
                    parenDepth++;
                else if (ch == ')')
                    parenDepth--;
            }

            if (ch == separator && !inQuote && parenDepth == 0)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    // ────────────────────────────────────────────────────────────
    //  HELPER: parse int from token list
    // ────────────────────────────────────────────────────────────
    private int GetInt(IReadOnlyList<string> tokens, int index, TemplateContext context)
        => (int)TypeConverters.ToLong(EvaluateToken(tokens.ElementAtOrDefault(index), context));

    // ────────────────────────────────────────────────────────────
    //  HELPER: get pipeline value or first arg
    // ────────────────────────────────────────────────────────────
    private static object? Pv(IReadOnlyList<string> tokens, int argIndex, TemplateContext context, object? pipelineValue)
        => pipelineValue ?? EvaluateTokenStatic(tokens.ElementAtOrDefault(argIndex), context);

    /// <summary>
    /// Static token evaluator accessible to extracted function helpers in the same assembly.
    /// </summary>
    internal static object? EvaluateTokenStatic(string? token, TemplateContext context)
    {
        // Minimal static version for use in helpers
        if (string.IsNullOrWhiteSpace(token)) return null;
        token = token.Trim();
        if (token.StartsWith('"') && token.EndsWith('"'))
            return StringHelpers.Unquote(token);
        if (token.StartsWith('\'') && token.EndsWith('\''))
            return token[1..^1];
        if (token == ".") return context.Dot;
        if (token == "true") return true;
        if (token == "false") return false;
        if (token == "nil") return null;
        if (long.TryParse(token, out var l)) return l;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        if (token.StartsWith('$'))
        {
            var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && context.Variables.TryGetValue(parts[0], out var current))
            {
                foreach (var part in parts.Skip(1))
                {
                    current = current switch
                    {
                        Dictionary<string, object?> dict when dict.TryGetValue(part, out var next) => next,
                        IDictionary<string, object?> dict when dict.TryGetValue(part, out var next) => next,
                        _ => null
                    };
                }
                return current;
            }
            return null;
        }
        return token;
    }




    // Sprig: untilStep START STOP STEP (defaults: start=0, step=1)
    private object? ResolveUntilStep(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        long start, stop, step;
        if (pipelineValue != null)
        {
            start = TypeConverters.ToLong(pipelineValue);
            stop = TypeConverters.ToLong(EvaluateToken(tokens.ElementAtOrDefault(1), context));
            step = tokens.Count > 2 ? TypeConverters.ToLong(EvaluateToken(tokens[2], context)) : 1L;
        }
        else
        {
            start = tokens.Count > 1 ? TypeConverters.ToLong(EvaluateToken(tokens[1], context)) : 0L;
            stop = TypeConverters.ToLong(EvaluateToken(tokens.ElementAtOrDefault(2), context));
            step = tokens.Count > 3 ? TypeConverters.ToLong(EvaluateToken(tokens[3], context)) : 1L;
        }
        if (step == 0) step = 1;
        return TextFunctions.UntilStep((int)start, (int)stop, (int)step);
    }

    // ────────────────────────────────────────────────────────────
    //  MATH FUNCTIONS
    // ────────────────────────────────────────────────────────────
    //  MATH HELPERS — resolve args here, compute in MathFunctions
    // ────────────────────────────────────────────────────────────
    private object FoldMathArgs(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, string op)
    {
        var args = tokens.Skip(1).Select(t => EvaluateToken(t, context)).ToList();
        if (pipelineValue != null) args.Insert(0, pipelineValue);
        if (args.Count == 0) return 0L;

        var result = TypeConverters.ToDouble(args[0]);
        for (var i = 1; i < args.Count; i++)
            result = (double)MathFunctions.MathOp(result, TypeConverters.ToDouble(args[i]), op);

        return result == Math.Floor(result) ? (long)result : result;
    }

    private List<double> ResolveDoubleList(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var args = tokens.Skip(1).Select(t => TypeConverters.ToDouble(EvaluateToken(t, context))).ToList();
        if (pipelineValue != null) args.Insert(0, TypeConverters.ToDouble(pipelineValue));
        return args;
    }

    private double ResolveDoubleArg(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, int index)
        => TypeConverters.ToDouble(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(index), context));





    // ────────────────────────────────────────────────────────────
    //  TYPE / REFLECTION




    // ────────────────────────────────────────────────────────────
    //  COMPARE OPS (lt, gt, le, ge)
    // ────────────────────────────────────────────────────────────

    // ────────────────────────────────────────────────────────────
    // ────────────────────────────────────────────────────────────


    // ── IEvaluationContext explicit implementation ──────────────
    object? IEvaluationContext.EvaluateToken(string? token, TemplateContext context)
        => EvaluateToken(token, context);

    string IEvaluationContext.RenderSection(string template, TemplateContext context)
        => RenderSection(template, context);
}
