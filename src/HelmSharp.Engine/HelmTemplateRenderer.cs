using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelmSharp.Chart;

namespace HelmSharp.Engine;

public sealed class HelmTemplateRenderer
{
    private static readonly Regex DefineRegex = new(
        "{{-?\\s*define\\s+\"(?<name>[^\"]+)\"\\s*-?}}(?<body>.*?){{-?\\s*end\\s*-?}}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TokenRegex = new(
        "{{-?\\s*(?<expr>.*?)\\s*-?}}",
        RegexOptions.Compiled | RegexOptions.Singleline);

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
    /// parser limitations) are accumulated per-template rather than failing immediately.
    /// This allows the engine to render as many templates as possible before throwing.
    /// Other exceptions (e.g. <c>fail</c> calls, arity errors) propagate immediately.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when one or more templates fail with <see cref="NotSupportedException"/>.
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

        var output = new StringBuilder();
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

                output.AppendLine("---");
                output.AppendLine(rendered.Trim());
            }
            catch (NotSupportedException ex)
            {
                // Engine-level gaps (missing function, parser limitation) — collect and continue
                errors.Add((path, ex));
            }
            // Other exceptions (fail, arity errors, etc.) propagate immediately
        }

        // Render subchart templates
        foreach (var (name, subchart) in _chart.Subcharts)
        {
            var declaredDependency = _chart.Dependencies.FirstOrDefault(
                dependency => string.Equals(dependency.Name, name, StringComparison.OrdinalIgnoreCase));
            var subchartIdentity = declaredDependency?.Alias ?? name;
            var matchingDependency = _root.Dependencies.FirstOrDefault(
                dependency => string.Equals(
                    dependency.Name,
                    subchartIdentity,
                    StringComparison.OrdinalIgnoreCase));
            if (_chart.Dependencies.Count > 0 && matchingDependency is null)
                continue;

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

                    output.AppendLine("---");
                    output.AppendLine(rendered.Trim());
                }
                catch (NotSupportedException ex)
                {
                    // Engine-level gaps (missing function, parser limitation) — collect and continue
                    errors.Add((path, ex));
                }
                // Other exceptions (fail, arity errors, etc.) propagate immediately
            }
        }

        if (errors.Count > 0)
        {
            var errorSummary = string.Join("; ",
                errors.Select(e => $"{Path.GetFileName(e.Path)}: {e.Exception.GetType().Name}"));
            throw new InvalidOperationException(
                $"HelmSharp template rendering failed for {errors.Count} template(s): {errorSummary}",
                errors[0].Exception);
        }

        return output.ToString();
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

    private string RenderSection(string template, TemplateContext context)
    {
        var output = new StringBuilder();
        var pos = 0;

        while (pos < template.Length)
        {
            var tokenMatch = TokenRegex.Match(template, pos);
            if (!tokenMatch.Success)
            {
                output.Append(template[pos..]);
                break;
            }

            // Check whitespace trimming markers
            var leftTrim = tokenMatch.Value.StartsWith("{{-", StringComparison.Ordinal);
            var rightTrim = tokenMatch.Value.EndsWith("-}}", StringComparison.Ordinal);

            // Append text before this token (with left trim if needed)
            if (tokenMatch.Index > pos)
            {
                var before = template[pos..tokenMatch.Index];
                if (leftTrim)
                    before = TrimTrailingWhitespace(before);
                output.Append(before);
            }

            var expr = tokenMatch.Groups["expr"].Value.Trim();

            if (expr == "else" || expr == "end")
            {
                // For else/end with left trim, also trim trailing whitespace from output
                if (leftTrim)
                {
                    var current = output.ToString().TrimEnd();
                    output.Clear();
                    output.Append(current);
                }
                output.Append(tokenMatch.Value);
                pos = tokenMatch.Index + tokenMatch.Length;
                continue;
            }

            var isBlockStart = expr.StartsWith("if ", StringComparison.Ordinal) ||
                               expr == "if" ||
                               expr.StartsWith("with ", StringComparison.Ordinal) ||
                               expr.StartsWith("range ", StringComparison.Ordinal);

            if (isBlockStart)
            {
                var keyword = expr.Split(' ', 2)[0];
                var openMatch = tokenMatch;
                var bodyStart = openMatch.Index + openMatch.Length;
                var tokenMatches = TokenRegex.Matches(template, bodyStart);
                var depth = 1;
                var elseStartIdx = -1;
                var elseEndIdx = -1;
                var endTokenStart = -1;
                var endTokenEnd = -1;

                foreach (Match token in tokenMatches)
                {
                    var tokenExpr = token.Groups["expr"].Value.Trim();
                    if (tokenExpr == "end")
                    {
                        depth--;
                        if (depth == 0)
                        {
                            endTokenStart = token.Index;
                            endTokenEnd = token.Index + token.Length;
                            break;
                        }
                    }
                    else if (tokenExpr.StartsWith("if ", StringComparison.Ordinal) || tokenExpr == "if" ||
                             tokenExpr.StartsWith("with ", StringComparison.Ordinal) ||
                             tokenExpr.StartsWith("range ", StringComparison.Ordinal) ||
                             tokenExpr.StartsWith("define ", StringComparison.Ordinal))
                    {
                        depth++;
                    }
                    else if (tokenExpr.StartsWith("else if ", StringComparison.Ordinal) && depth == 1 && keyword == "if" && elseStartIdx < 0)
                    {
                        elseStartIdx = token.Index;
                        elseEndIdx = token.Index + token.Length;
                    }
                    else if (tokenExpr == "else" && depth == 1 && elseStartIdx < 0)
                    {
                        elseStartIdx = token.Index;
                        elseEndIdx = token.Index + token.Length;
                    }
                }

                if (endTokenStart < 0)
                    throw new NotSupportedException(
                        $"Template block '{keyword}' with expression '{expr}' is missing an end marker. " +
                        $"Template length: {template.Length}. Excerpt from offset: '{template.Substring(0, Math.Min(200, template.Length))}'");

                var blockExpr = expr.Length > keyword.Length ? expr[keyword.Length..].Trim() : string.Empty;
                var trueBody = elseStartIdx >= 0
                    ? template[bodyStart..elseStartIdx]
                    : template[bodyStart..endTokenStart];

                // Check if the else marker is from an "else if" — if so, reconstruct
                // the false body as {{- if condition }}...{{- end }} so RenderSection can process it
                var falseBody = string.Empty;
                if (elseStartIdx >= 0)
                {
                    var elseTokenValue = template[elseStartIdx..elseEndIdx];
                    if (elseTokenValue.Trim().StartsWith("{{", StringComparison.Ordinal) &&
                        elseTokenValue.Contains("else if", StringComparison.Ordinal))
                    {
                        // For "else if condition", reconstruct false body as:
                        // {{- if condition }} <remaining content including else/else-if chain> {{- end }}
                        var elseIfExpr = elseTokenValue;
                        var elseIfMatch = System.Text.RegularExpressions.Regex.Match(
                            elseIfExpr, @"else\s+if\s+(?<cond>.*?)\s*-?\}\}", System.Text.RegularExpressions.RegexOptions.Singleline);
                        if (elseIfMatch.Success)
                        {
                            var condition = elseIfMatch.Groups["cond"].Value.Trim();
                            var trimLeft = elseTokenValue.Contains("{{-");
                            var prefix = trimLeft ? "{{- " : "{{ ";
                            // Reconstruct: {{- if condition }}<everything from after else-if to end>{{- end }}
                            // The remaining content already has balanced else/else-if/end blocks from the original chain.
                            var remainingContent = template[elseEndIdx..endTokenStart];
                            falseBody = $"{prefix}if {condition} }}}}{remainingContent}{{{{- end }}}}";
                        }
                        else
                        {
                            falseBody = template[elseEndIdx..endTokenStart];
                        }
                    }
                    else
                    {
                        falseBody = template[elseEndIdx..endTokenStart];
                    }
                }

                var replacement = keyword switch
                {
                    "if" => TypeConverters.IsTruthy(EvaluatePipeline(blockExpr, context))
                        ? RenderSection(trueBody, context)
                        : RenderSection(falseBody, context),
                    "with" =>
                        TypeConverters.IsTruthy(EvaluatePipeline(blockExpr, context))
                            ? RenderSection(trueBody, context with { Dot = EvaluatePipeline(blockExpr, context) })
                            : RenderSection(falseBody, context),
                    "range" => RenderRangeExpression(blockExpr, trueBody, context),
                    _ => string.Empty
                };

                output.Append(replacement);
                pos = endTokenEnd;
            }
            else
            {
                if (expr.StartsWith("/*", StringComparison.Ordinal))
                {
                    pos = tokenMatch.Index + tokenMatch.Length;
                    continue;
                }

                string rendered;
                if (TryAssignVariable(expr, context, out var assigned))
                    rendered = assigned;
                else
                    rendered = TypeConverters.ToTemplateString(EvaluatePipeline(expr, context));

                // Apply left trim to rendered output if needed
                if (leftTrim)
                    rendered = rendered.TrimStart();

                output.Append(rendered);
                pos = tokenMatch.Index + tokenMatch.Length;
            }

            // Apply right trim: remove leading whitespace from next content
            if (rightTrim && pos < template.Length)
            {
                // Find the next non-whitespace position
                var nextPos = pos;
                while (nextPos < template.Length && char.IsWhiteSpace(template[nextPos]) && template[nextPos] != '\n')
                    nextPos++;
                // If we stopped at a newline, consume it too
                if (nextPos < template.Length && template[nextPos] == '\n')
                    nextPos++;
                pos = nextPos;
            }
        }

        return output.ToString();
    }

    private static string TrimTrailingWhitespace(string value)
    {
        var end = value.Length;
        while (end > 0 && char.IsWhiteSpace(value[end - 1]))
            end--;
        return value[..end];
    }

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
            "default" => FnDefault(tokens, context, pipelineValue),
            "required" => Required(tokens, context, pipelineValue),
            "tpl" => Tpl(tokens, context, pipelineValue),
            "fail" => FnFail(tokens, context),

            // String functions
            "quote" => StringHelpers.Quote(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "squote" => StringFunctions.Squote(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "cat" => Cat(tokens, context, pipelineValue),
            "indent" => StringHelpers.Indent(TypeConverters.ToTemplateString(pipelineValue), GetInt(tokens, 1, context), false),
            "nindent" => StringHelpers.Indent(TypeConverters.ToTemplateString(pipelineValue), GetInt(tokens, 1, context), true),
            "replace" => Replace(tokens, context, pipelineValue),
            "plural" => Plural(tokens, context, pipelineValue),
            "snakecase" => StringFunctions.Snakecase(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "camelcase" => StringFunctions.Camelcase(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "kebabcase" => StringFunctions.Kebabcase(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "wrap" => Wrap(tokens, context, pipelineValue),
            "wrapWith" => WrapWith(tokens, context, pipelineValue),
            "initials" => StringFunctions.Initials(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "abbrev" => Abbrev(tokens, context, pipelineValue),
            "trunc" => Trunc(tokens, context, pipelineValue),
            "abbrevinitial" => Abbrevinitial(tokens, context, pipelineValue),
            "untitle" => TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)).ToLowerInvariant(),
            "title" => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "upper" => TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)).ToUpperInvariant(),
            "lower" => TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)).ToLowerInvariant(),
            "trim" => TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)).Trim(),
            "trimAll" => TrimAll(tokens, context, pipelineValue),
            "trimSuffix" => TrimSuffix(tokens, context, pipelineValue),
            "trimPrefix" => TrimPrefix(tokens, context, pipelineValue),
            "contains" => Contains(tokens, context, pipelineValue),
            "hasPrefix" => HasPrefix(tokens, context, pipelineValue),
            "hasSuffix" => HasSuffix(tokens, context, pipelineValue),
            "repeat" => Repeat(tokens, context, pipelineValue),
            "substr" => Substr(tokens, context, pipelineValue),
            "toString" => TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "atoi" => int.TryParse(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)), out var ai) ? ai : 0,
            "printf" => Printf(tokens, context),
            "println" => string.Join(' ', tokens.Skip(1).Select(t => TypeConverters.ToTemplateString(EvaluateToken(t, context)))),

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
            "sha1sum" => EncodingHelpers.Sha1Sum(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "adler32sum" => EncodingHelpers.Adler32Sum(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "bcrypt" => EncodingHelpers.BCryptHash(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "randAlphaNum" => RandString(tokens, context, "alphanum"),
            "randAlpha" => RandString(tokens, context, "alpha"),
            "randNumeric" => RandString(tokens, context, "numeric"),
            "randAscii" => RandString(tokens, context, "ascii"),
            "randInt" => RandInt(tokens, context),
            "genPrivateKey" => GenPrivateKey(tokens, context),

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
            "typeIs" => TypeIs(tokens, context, pipelineValue),
            "typeIsLike" => TypeIsLike(tokens, context, pipelineValue),
            "kindOf" => TypeFunctions.KindOf(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "kindIs" => KindIs(tokens, context, pipelineValue),
            "deepEqual" => TypeFunctions.DeepEquals(
                pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context),
                EvaluateToken(tokens.ElementAtOrDefault(2), context)),

            // List functions
            "list" => tokens.Skip(1).Select(t => EvaluateToken(t, context)).ToList(),
            "first" => CollectionsHelpers.First(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "last" => CollectionsHelpers.Last(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "rest" => CollectionsHelpers.Rest(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "initial" => CollectionsHelpers.Initial(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "prepend" => Prepend(tokens, context, pipelineValue),
            "append" => Append(tokens, context, pipelineValue),
            "mustAppend" => Append(tokens, context, pipelineValue),
            "reverse" => CollectionsHelpers.Reverse(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "sortAlpha" => CollectionsHelpers.SortAlpha(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "compact" => CollectionsHelpers.Compact(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "uniq" => CollectionsHelpers.Uniq(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "without" => Without(tokens, context, pipelineValue),
            "has" => Has(tokens, context, pipelineValue),
            "concat" => Concat(tokens, context, pipelineValue),

            // Dict functions
            "dict" => Dict(tokens, context),
            "get" => Get(tokens, context, pipelineValue),
            "set" => Set(tokens, context, pipelineValue),
            "unset" => Unset(tokens, context, pipelineValue),
            "hasKey" => HasKey(tokens, context, pipelineValue),
            "keys" => CollectionsHelpers.Keys(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "values" => CollectionsHelpers.Values(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "merge" => MergeDicts(tokens, context),
            "mustMerge" => MergeDicts(tokens, context),
            "deepCopy" => CollectionsHelpers.DeepCopy(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "pick" => Pick(tokens, context, pipelineValue),
            "omit" => Omit(tokens, context, pipelineValue),
            "pluck" => Pluck(tokens, context, pipelineValue),
            "dig" => Dig(tokens, context, pipelineValue),

            // Index (list/dict access)
            "index" => Index(tokens, context, pipelineValue),

            // Ternary / coalesce / logic
            "ternary" => Ternary(tokens, context, pipelineValue),
            "coalesce" => tokens.Skip(1).Select(t => EvaluateToken(t, context)).FirstOrDefault(TypeConverters.IsTruthy),
            "empty" => !TypeConverters.IsTruthy(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "not" => !TypeConverters.IsTruthy(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "and" => tokens.Skip(1).All(t => TypeConverters.IsTruthy(EvaluateToken(t, context))),
            "or" => tokens.Skip(1).Any(t => TypeConverters.IsTruthy(EvaluateToken(t, context))),

            // Comparison
            "eq" => Eq(tokens, context, pipelineValue),
            "ne" => !TypeConverters.IsTruthy(Eq(tokens, context, pipelineValue)),
            "lt" => CompareOp(tokens, context, pipelineValue, (a, b) => a < 0),
            "gt" => CompareOp(tokens, context, pipelineValue, (a, b) => a > 0),
            "le" => CompareOp(tokens, context, pipelineValue, (a, b) => a <= 0),
            "ge" => CompareOp(tokens, context, pipelineValue, (a, b) => a >= 0),

            // JSON / YAML / TOML
            "toJson" => SerializationFunctions.ToJson(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "fromJson" => SerializationFunctions.FromJson(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),
            "toPrettyJson" => SerializationFunctions.ToPrettyJson(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),
            "toYaml" => HelmYaml.Serialize(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)).TrimEnd(),
            "fromYaml" => HelmYaml.DeserializeDictionary(TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context))),

            // Lookup
            "lookup" => Lookup(tokens, context),

            // Len
            "len" => GetLength(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context)),

            // Auto
            "auto" => "auto",

            _ => pipelineValue is not null && tokens.Count == 1
                ? ApplySimpleFunction(head, pipelineValue, context)
                : EvaluateToken(expression, context)
        };
    }

    private object? IncludeTemplate(IReadOnlyList<string> tokens, TemplateContext context)
    {
        var name = StringHelpers.Unquote(tokens.ElementAtOrDefault(1) ?? string.Empty);
        if (!_definedTemplates.TryGetValue(name, out var body))
            throw new NotSupportedException($"Included template '{name}' was not found.");

        try
        {
            var rendered = RenderSection(body, context);
            // In Go templates, include returns the trimmed result
            return rendered.Trim();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Error rendering included template '{name}': {ex.Message}", ex);
        }
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
            "sha1sum" => EncodingHelpers.Sha1Sum(TypeConverters.ToTemplateString(value)),
            "not" => !TypeConverters.IsTruthy(value),
            "empty" => !TypeConverters.IsTruthy(value),
            "len" => GetLength(value),
            "keys" => CollectionsHelpers.Keys(value),
            "values" => CollectionsHelpers.Values(value),
            "first" => CollectionsHelpers.First(value),
            "last" => CollectionsHelpers.Last(value),
            "rest" => CollectionsHelpers.Rest(value),
            "initial" => CollectionsHelpers.Initial(value),
            "reverse" => CollectionsHelpers.Reverse(value),
            "sortAlpha" => CollectionsHelpers.SortAlpha(value),
            "compact" => CollectionsHelpers.Compact(value),
            "uniq" => CollectionsHelpers.Uniq(value),
            "deepCopy" => CollectionsHelpers.DeepCopy(value),
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
            _ => throw new NotSupportedException($"Helm template function '{function}' is not supported by the managed renderer.")
        };
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
        if (token.StartsWith('$'))
            return ResolveVariable(token, context);
        if (token.StartsWith('.'))
            return ResolvePath(token, context);

        return token;
    }

    private object? ResolvePath(string token, TemplateContext context)
    {
        var parts = token.TrimStart('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        object? current = parts.FirstOrDefault() switch
        {
            "Values" => context.Values,
            "Chart" => new Dictionary<string, object?>
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
            },
            "Release" => new Dictionary<string, object?>
            {
                ["Name"] = context.ReleaseName,
                ["Namespace"] = context.ReleaseNamespace,
                ["Service"] = "Helm",
                ["IsInstall"] = context.IsInstall,
                ["IsUpgrade"] = context.IsUpgrade,
                ["Revision"] = context.Revision
            },
            "Capabilities" => new Dictionary<string, object?>
            {
                ["KubeVersion"] = ToTemplateKubeVersion(context.KubeVersion),
                ["APIVersions"] = context.ApiVersions ?? GetDefaultApiVersions(context.KubeVersion),
                ["HelmVersion"] = new Dictionary<string, object?>
                {
                    ["Version"] = "chemical-ai-helm managed-0.3.0",
                    ["GitCommit"] = "managed",
                    ["GitTreeState"] = "clean",
                    ["GoVersion"] = "dotnet/9.0"
                }
            },
            "Files" => context.Chart.Files.ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.Ordinal),
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

    private static object? ResolveMembers(object? current, string path)
        => ResolveMembers(current, path.Split('.', StringSplitOptions.RemoveEmptyEntries));

    private static object? ResolveMembers(object? current, IEnumerable<string> parts)
    {
        foreach (var part in parts)
        {
            current = current switch
            {
                ApiVersionSet => null,
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

    // Need a static overload for EvaluateToken
    private static object? EvaluateTokenStatic(string? token, TemplateContext context)
    {
        // Minimal static version for use in helpers
        if (string.IsNullOrWhiteSpace(token)) return null;
        token = token.Trim();
        if (token.StartsWith('"') && token.EndsWith('"'))
            return token.Length >= 2 ? token[1..^1] : token;
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

    // ────────────────────────────────────────────────────────────
    //  DEFAULT
    // ────────────────────────────────────────────────────────────
    private object? FnDefault(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var def = EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var val = pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context);
        return TypeConverters.IsTruthy(val) ? val : def;
    }

    // ────────────────────────────────────────────────────────────
    //  FAIL
    // ────────────────────────────────────────────────────────────
    private static object? FnFail(IReadOnlyList<string> tokens, TemplateContext context)
    {
        var msg = tokens.ElementAtOrDefault(1);
        throw new InvalidOperationException(msg != null ? StringHelpers.Unquote(msg) : "fail called");
    }

    // ────────────────────────────────────────────────────────────
    //  CAT
    // ────────────────────────────────────────────────────────────
    private string Cat(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var parts = new List<string>();
        if (pipelineValue != null) parts.Add(TypeConverters.ToTemplateString(pipelineValue));
        foreach (var t in tokens.Skip(1))
            parts.Add(TypeConverters.ToTemplateString(EvaluateToken(t, context)));
        return string.Join(' ', parts);
    }

    // ────────────────────────────────────────────────────────────
    //  PLURAL
    // ────────────────────────────────────────────────────────────
    private static string Plural(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var word = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateTokenStatic(tokens.ElementAtOrDefault(3), context));
        var plural = TypeConverters.ToTemplateString(EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var singular = TypeConverters.ToTemplateString(EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        var count = (int)TypeConverters.ToLong(EvaluateTokenStatic(tokens.ElementAtOrDefault(4), context));
        return count == 1 ? singular : plural;
    }


    // ────────────────────────────────────────────────────────────
    //  WRAP / WRAPWITH
    // ────────────────────────────────────────────────────────────
    private static string Wrap(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var width = (int)TypeConverters.ToLong(EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        return StringFunctions.WrapText(input, width);
    }

    private static string WrapWith(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var width = (int)TypeConverters.ToLong(EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var indent = TypeConverters.ToTemplateString(EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateTokenStatic(tokens.ElementAtOrDefault(3), context));
        return StringFunctions.WrapText(input, width, indent);
    }

    // ────────────────────────────────────────────────────────────
    //  INITIALS / ABBREV / ABBREVINITIAL
    // ────────────────────────────────────────────────────────────
    private static string Abbrev(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var maxWidth = (int)TypeConverters.ToLong(EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        return input.Length <= maxWidth ? input : input[..maxWidth];
    }

    private static string Abbrevinitial(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var maxWidth = (int)TypeConverters.ToLong(EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        var parts = input.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (sb.Length >= maxWidth) break;
            sb.Append(part.Length > 0 ? part[0] : "");
        }
        return sb.ToString()[..Math.Min(sb.Length, maxWidth)];
    }

    // ────────────────────────────────────────────────────────────
    //  TRIMALL / HASSUFFIX / HASPREFIX
    // ────────────────────────────────────────────────────────────
    private static string TrimAll(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var cutset = TypeConverters.ToTemplateString(EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        foreach (var ch in cutset)
            input = input.Trim(ch);
        return input;
    }

    private static bool HasPrefix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var prefix = TypeConverters.ToTemplateString(EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        return input.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool HasSuffix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var suffix = TypeConverters.ToTemplateString(EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        return input.EndsWith(suffix, StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────
    //  REPEAT / SUBSTR
    // ────────────────────────────────────────────────────────────
    private static string Repeat(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var count = (int)TypeConverters.ToLong(EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        return string.Concat(Enumerable.Repeat(input, count));
    }

    private static string Substr(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var start = (int)TypeConverters.ToLong(EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var length = (int)TypeConverters.ToLong(EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateTokenStatic(tokens.ElementAtOrDefault(3), context));
        if (start < 0) start = 0;
        if (start >= input.Length) return string.Empty;
        if (start + length > input.Length) length = input.Length - start;
        return input.Substring(start, length);
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
    //  CRYPTO / RANDOM

    private static string RandString(IReadOnlyList<string> tokens, TemplateContext context, string charset)
    {
        var length = tokens.Count > 1 ? (int)TypeConverters.ToLong(EvaluateTokenStatic(tokens[1], context)) : 10;
        var chars = charset switch
        {
            "alphanum" => "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            "alpha" => "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ",
            "numeric" => "0123456789",
            "ascii" => "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()",
            _ => "abcdefghijklmnopqrstuvwxyz"
        };
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }

    private static long RandInt(IReadOnlyList<string> tokens, TemplateContext context)
    {
        var min = tokens.Count > 1 ? (int)TypeConverters.ToLong(EvaluateTokenStatic(tokens[1], context)) : 0;
        var max = tokens.Count > 2 ? (int)TypeConverters.ToLong(EvaluateTokenStatic(tokens[2], context)) : int.MaxValue;
        return RandomNumberGenerator.GetInt32(min, max);
    }

    private static string GenPrivateKey(IReadOnlyList<string> tokens, TemplateContext context)
    {
        var algo = tokens.Count > 1 ? TypeConverters.ToTemplateString(EvaluateTokenStatic(tokens[1], context)) : "rsa";
        // Return a placeholder — real key generation requires BouncyCastle or similar
        return $"-----BEGIN {algo.ToUpperInvariant()} PRIVATE KEY-----\n(managed-helm-placeholder)\n-----END {algo.ToUpperInvariant()} PRIVATE KEY-----";
    }



    // ────────────────────────────────────────────────────────────
    //  TYPE / REFLECTION
    // ────────────────────────────────────────────────────────────
    private static bool TypeIs(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var typeName = TypeConverters.ToTemplateString(EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var val = pipelineValue ?? EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context);
        return typeName switch
        {
            "string" => val is string,
            "bool" => val is bool,
            "int" => val is int or long,
            "float64" => val is double or float,
            "[]interface {}" => val is IList<object?>,
            "map[string]interface {}" => val is IDictionary<string, object?>,
            "nil" => val is null,
            _ => false
        };
    }

    private static bool TypeIsLike(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
        => TypeIs(tokens, context, pipelineValue);


    private static bool KindIs(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var kind = TypeConverters.ToTemplateString(EvaluateTokenStatic(tokens.ElementAtOrDefault(1), context));
        var val = pipelineValue ?? EvaluateTokenStatic(tokens.ElementAtOrDefault(2), context);
        return TypeFunctions.KindOf(val) == kind;
    }



    private object? Prepend(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var list = CollectionsHelpers.ToList(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var item = EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var result = new List<object?> { item };
        result.AddRange(list);
        return result;
    }

    private object? Append(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var list = CollectionsHelpers.ToList(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var item = EvaluateToken(tokens.ElementAtOrDefault(2), context);
        var result = new List<object?>(list) { item };
        return result;
    }


    private object? Without(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var list = CollectionsHelpers.ToList(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var exclude = tokens.Skip(2).Select(t => TypeConverters.ToTemplateString(EvaluateToken(t, context))).ToHashSet(StringComparer.Ordinal);
        return list.Where(x => !exclude.Contains(TypeConverters.ToTemplateString(x))).ToList();
    }

    private object? Has(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var list = CollectionsHelpers.ToList(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var needle = TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return list.Any(x => TypeConverters.ToTemplateString(x) == needle);
    }

    private object? Concat(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var result = new List<object?>();
        if (pipelineValue != null) result.AddRange(CollectionsHelpers.ToList(pipelineValue));
        foreach (var t in tokens.Skip(1))
            result.AddRange(CollectionsHelpers.ToList(EvaluateToken(t, context)));
        return result;
    }

    // ────────────────────────────────────────────────────────────
    //  DICT FUNCTIONS
    // ────────────────────────────────────────────────────────────
    private object? Set(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var dict = pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var key = TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var value = EvaluateToken(tokens.ElementAtOrDefault(3), context);
        if (dict is Dictionary<string, object?> d)
        {
            d[key] = value;
            return d;
        }
        return dict;
    }

    private object? Unset(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var dict = pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var key = TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(2), context));
        if (dict is Dictionary<string, object?> d)
        {
            d.Remove(key);
            return d;
        }
        return dict;
    }


    private object? MergeDicts(IReadOnlyList<string> tokens, TemplateContext context)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens.Skip(1))
        {
            var val = EvaluateToken(t, context);
            if (val is IDictionary<string, object?> dict)
                CollectionsHelpers.MergeInto(result, dict);
        }
        return result;
    }


    private object? Pick(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var dict = pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var keys = tokens.Skip(2).Select(t => TypeConverters.ToTemplateString(EvaluateToken(t, context))).ToHashSet(StringComparer.Ordinal);
        if (pipelineValue != null)
            keys = tokens.Skip(1).Select(t => TypeConverters.ToTemplateString(EvaluateToken(t, context))).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (dict is IDictionary<string, object?> d)
        {
            foreach (var kvp in d)
                if (keys.Contains(kvp.Key))
                    result[kvp.Key] = kvp.Value;
        }
        return result;
    }

    private object? Omit(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var dict = pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var keys = tokens.Skip(2).Select(t => TypeConverters.ToTemplateString(EvaluateToken(t, context))).ToHashSet(StringComparer.Ordinal);
        if (pipelineValue != null)
            keys = tokens.Skip(1).Select(t => TypeConverters.ToTemplateString(EvaluateToken(t, context))).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (dict is IDictionary<string, object?> d)
        {
            foreach (var kvp in d)
                if (!keys.Contains(kvp.Key))
                    result[kvp.Key] = kvp.Value;
        }
        return result;
    }

    private object? Pluck(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var key = TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var dicts = tokens.Skip(2).Select(t => EvaluateToken(t, context));
        if (pipelineValue != null)
            dicts = tokens.Skip(1).Select(t => EvaluateToken(t, context));
        var result = new List<object?>();
        foreach (var d in dicts)
        {
            if (d is IDictionary<string, object?> dict && dict.TryGetValue(key, out var val))
                result.Add(val);
        }
        return result;
    }

    private object? Dig(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var args = tokens.Skip(1).Select(t => EvaluateToken(t, context)).ToList();
        if (pipelineValue != null) args.Insert(0, pipelineValue);
        if (args.Count < 2) return null;

        var current = args[0];
        var defaultVal = args[^1];
        for (var i = 1; i < args.Count - 1; i++)
        {
            var key = TypeConverters.ToTemplateString(args[i]);
            current = current switch
            {
                Dictionary<string, object?> dict when dict.TryGetValue(key, out var next) => next,
                IDictionary<string, object?> dict when dict.TryGetValue(key, out var next) => next,
                _ => null
            };
            if (current is null) return defaultVal;
        }
        return current;
    }

    // ────────────────────────────────────────────────────────────
    //  COMPARE OPS (lt, gt, le, ge)
    // ────────────────────────────────────────────────────────────
    private object? Eq(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var a = pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var b = EvaluateToken(tokens.ElementAtOrDefault(pipelineValue != null ? 1 : 2), context);
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.GetType() == b.GetType())
        {
            if (a is long la && b is long lb) return la == lb;
            if (a is double da && b is double db) return Math.Abs(da - db) < 1e-10;
            if (a is bool ba && b is bool bb) return ba == bb;
        }
        return string.Equals(TypeConverters.ToTemplateString(a), TypeConverters.ToTemplateString(b), StringComparison.Ordinal);
    }

    private object? CompareOp(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue, Func<int, int, bool> cmp)
    {
        var a = pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var b = EvaluateToken(tokens.ElementAtOrDefault(pipelineValue != null ? 1 : 2), context);
        var result = CompareValues(a, b);
        return cmp(result, 0);
    }

    private static int CompareValues(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        if (a is long la && b is long lb) return la.CompareTo(lb);
        if (a is double da && b is double db) return da.CompareTo(db);
        if (a is int ia && b is int ib) return ia.CompareTo(ib);
        return string.Compare(TypeConverters.ToTemplateString(a), TypeConverters.ToTemplateString(b), StringComparison.Ordinal);
    }


    // ────────────────────────────────────────────────────────────
    //  LOOKUP
    // ────────────────────────────────────────────────────────────
    private object? Lookup(IReadOnlyList<string> tokens, TemplateContext context)
    {
        // lookup "apiVersion" "kind" "namespace" "name"
        // In managed mode, return empty dict — no cluster access during template rendering
        return new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    // ────────────────────────────────────────────────────────────
    //  LEN
    // ────────────────────────────────────────────────────────────
    private static int GetLength(object? value)
        => value switch
        {
            string s => s.Length,
            ICollection c => c.Count,
            IList<object?> l => l.Count,
            IDictionary<string, object?> d => d.Count,
            IEnumerable<object?> e => e.Count(),
            _ => 0
        };

    // ────────────────────────────────────────────────────────────
    //  STRING HELPERS
    // ────────────────────────────────────────────────────────────
    private string Replace(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var oldValue = TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var newValue = TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(2), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(3), context));
        return input.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private string Trunc(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var length = (int)TypeConverters.ToLong(EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.Length <= length ? input : input[..Math.Max(0, length)];
    }

    private string TrimSuffix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var suffix = TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.EndsWith(suffix, StringComparison.Ordinal) ? input[..^suffix.Length] : input;
    }

    private string TrimPrefix(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var prefix = TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.StartsWith(prefix, StringComparison.Ordinal) ? input[prefix.Length..] : input;
    }

    private bool Contains(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var needle = TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var input = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context));
        return input.Contains(needle, StringComparison.Ordinal);
    }

    private string Printf(IReadOnlyList<string> tokens, TemplateContext context)
    {
        var format = TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var args = tokens.Skip(2).Select(t => TypeConverters.ToTemplateString(EvaluateToken(t, context))).ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            format = StringHelpers.ReplaceFirst(format, "%s", "{" + i + "}");
            format = StringHelpers.ReplaceFirst(format, "%v", "{" + i + "}");
            format = StringHelpers.ReplaceFirst(format, "%d", "{" + i + "}");
            format = StringHelpers.ReplaceFirst(format, "%f", "{" + i + "}");
            format = StringHelpers.ReplaceFirst(format, "%q", "{" + i + "}");
        }

        return string.Format(format, args);
    }

    private object? Required(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var message = TypeConverters.ToTemplateString(EvaluateToken(tokens.ElementAtOrDefault(1), context));
        var value = pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(2), context);
        if (!TypeConverters.IsTruthy(value))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "required value is missing" : message);

        return value;
    }

    private string Tpl(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var template = TypeConverters.ToTemplateString(pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context));
        return RenderSection(template, context);
    }

    private object Dict(IReadOnlyList<string> tokens, TemplateContext context)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var args = tokens.Skip(1).ToList();
        for (var i = 0; i < args.Count; i += 2)
        {
            var key = TypeConverters.ToTemplateString(EvaluateToken(args[i], context));
            var value = i + 1 < args.Count ? EvaluateToken(args[i + 1], context) : null;
            dict[key] = value;
        }

        return dict;
    }

    private object? Index(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var value = pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var keyTokens = pipelineValue is null ? tokens.Skip(2) : tokens.Skip(1);
        foreach (var keyToken in keyTokens)
        {
            value = IndexOne(value, EvaluateToken(keyToken, context));
        }

        return value;
    }

    private object? Get(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var dict = pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var key = pipelineValue is null
            ? EvaluateToken(tokens.ElementAtOrDefault(2), context)
            : EvaluateToken(tokens.ElementAtOrDefault(1), context);
        return IndexOne(dict, key);
    }

    private bool HasKey(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var dict = pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var key = TypeConverters.ToTemplateString(pipelineValue is null
            ? EvaluateToken(tokens.ElementAtOrDefault(2), context)
            : EvaluateToken(tokens.ElementAtOrDefault(1), context));
        return dict switch
        {
            Dictionary<string, object?> d => d.ContainsKey(key),
            IDictionary<string, object?> d => d.ContainsKey(key),
            _ => false
        };
    }

    private object? Ternary(IReadOnlyList<string> tokens, TemplateContext context, object? pipelineValue)
    {
        var trueValue = EvaluateToken(tokens.ElementAtOrDefault(1), context);
        var falseValue = EvaluateToken(tokens.ElementAtOrDefault(2), context);
        var test = pipelineValue ?? EvaluateToken(tokens.ElementAtOrDefault(3), context);
        return TypeConverters.IsTruthy(test) ? trueValue : falseValue;
    }

    private static object? IndexOne(object? value, object? key)
    {
        var keyString = TypeConverters.ToTemplateString(key);
        return value switch
        {
            Dictionary<string, object?> dict when dict.TryGetValue(keyString, out var next) => next,
            IDictionary<string, object?> dict when dict.TryGetValue(keyString, out var next) => next,
            IReadOnlyList<object?> list when int.TryParse(keyString, out var index) && index >= 0 && index < list.Count => list[index],
            IList<object?> list when int.TryParse(keyString, out var index) && index >= 0 && index < list.Count => list[index],
            _ => null
        };
    }

}
