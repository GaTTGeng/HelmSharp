namespace HelmSharp.Engine;

/// <summary>
/// Returned by ParseContent when it stops at a boundary keyword.
/// </summary>
internal readonly struct StopResult
{
    /// <summary>The keyword: "end", "else", or "else if". Null if EOF.</summary>
    public string? Keyword { get; init; }
    /// <summary>Raw action expression that caused the stop.</summary>
    public string Expression { get; init; }
    /// <summary>Left trim marker on the stop action.</summary>
    public bool LeftTrim { get; init; }
    /// <summary>Right trim marker on the stop action.</summary>
    public bool RightTrim { get; init; }
    /// <summary>Line number of the stop action (1-based).</summary>
    public int Line { get; init; }
    /// <summary>Column number of the stop action (1-based).</summary>
    public int Column { get; init; }
    /// <summary>Byte offset of the stop action in the template.</summary>
    public int Offset { get; init; }
}

/// <summary>
/// Parses a flat token stream from <see cref="TemplateTokenizer"/> into a nested AST.
/// Uses recursive descent — block bodies are parsed by directly consuming tokens.
/// </summary>
public sealed class TemplateParser
{
    private readonly List<Token> _tokens;
    private int _pos;

    private readonly Dictionary<string, DefineNode> _defines = new(StringComparer.Ordinal);

    private static readonly HashSet<string> EndOnly = new(StringComparer.Ordinal) { "end" };
    private static readonly HashSet<string> EndElseElseIf = new(StringComparer.Ordinal) { "end", "else", "else if" };

    public TemplateParser(IEnumerable<Token> tokens)
    {
        _tokens = tokens.ToList();
        _pos = 0;
    }

    public IReadOnlyDictionary<string, DefineNode> Defines => _defines;

    public TemplateDocumentNode Parse()
    {
        var document = new TemplateDocumentNode();
        ParseContent(document.Children, EndElseElseIf);

        if (document.Children.Count > 0)
        {
            document.StartOffset = document.Children[0].StartOffset;
            document.EndOffset = document.Children[^1].EndOffset;
            document.StartLine = document.Children[0].StartLine;
            document.EndLine = document.Children[^1].EndLine;
        }

        return document;
    }

    /// <summary>Parses content until EOF or a stop keyword. Returns the stop info.</summary>
    private StopResult ParseContent(List<TemplateNode> children, HashSet<string> stopKeywords)
    {
        while (_pos < _tokens.Count)
        {
            var token = _tokens[_pos];

            if (token.Kind == TokenKind.EndOfFile)
                break;

            if (token.Kind == TokenKind.Text)
            {
                children.Add(ParseText());
                continue;
            }

            if (token.Kind == TokenKind.LeftDelim)
            {
                _pos++;
                var leftTrim = token.LeftTrim;
                var startLine = token.Line;
                var startCol = token.Column;
                var startOffset = token.Offset;

                if (_pos >= _tokens.Count) break;
                var ct = _tokens[_pos];
                if (ct.Kind != TokenKind.ActionContent) break;

                var expr = ct.Value;
                _pos++;

                var rightTrim = false;
                if (_pos < _tokens.Count && _tokens[_pos].Kind == TokenKind.RightDelim)
                {
                    rightTrim = _tokens[_pos].RightTrim;
                    _pos++;
                }

                var keyword = GetFirstWord(expr);

                if (stopKeywords.Contains(keyword))
                    return new StopResult { Keyword = keyword, Expression = expr, LeftTrim = leftTrim, RightTrim = rightTrim, Line = startLine, Column = startCol, Offset = startOffset };

                switch (keyword)
                {
                    case "define":
                        ParseDefine(expr, leftTrim, rightTrim, startOffset, startLine, startCol);
                        break;
                    case "if":
                    case "with":
                    case "range":
                        children.Add(ParseBlock(keyword, expr, leftTrim, rightTrim, startOffset, startLine, startCol));
                        break;
                    default:
                        children.Add(MakeNode(expr, leftTrim, rightTrim, startOffset, startLine));
                        break;
                }
                continue;
            }

            _pos++;
        }

        return new StopResult();
    }

    private void ParseDefine(string expr, bool leftTrim, bool rightTrim, int startOffset, int startLine, int startCol)
    {
        var name = ExtractQuotedFirstArg(expr, "define");
        if (name is null)
            throw new TemplateParseException(
                $"The 'define' keyword requires a quoted template name (e.g. define \"name\")",
                startLine, startCol, startOffset);
        if (name.Length == 0)
            throw new TemplateParseException(
                "The 'define' keyword was given an empty template name.",
                startLine, startCol, startOffset);

        var bodyDoc = new TemplateDocumentNode();
        var stop = ParseContent(bodyDoc.Children, EndOnly);
        if (rightTrim)
            TrimLeadingForRightTrim(bodyDoc.Children);

        if (stop.Keyword == null)
            throw new TemplateParseException(
                $"Missing 'end' for define \"{name}\"",
                startLine, startCol, startOffset);

        _defines[name] = new DefineNode
        {
            Name = name,
            Body = bodyDoc,
            LeftTrim = leftTrim,
            RightTrim = rightTrim,
            StartOffset = startOffset,
            StartLine = startLine,
        };
    }

    private BlockNode ParseBlock(string keyword, string expr, bool leftTrim, bool rightTrim, int startOffset, int startLine, int startCol)
    {
        // Skip past keyword in the trimmed expression to extract the condition
        var trimmedExpr = expr.TrimStart();
        var condition = trimmedExpr.StartsWith(keyword, StringComparison.Ordinal) && trimmedExpr.Length > keyword.Length
            ? trimmedExpr[keyword.Length..].Trim()
            : string.Empty;

        var block = new BlockNode
        {
            Keyword = keyword,
            Expression = condition,
            LeftTrim = leftTrim,
            RightTrim = rightTrim,
            StartOffset = startOffset,
            StartLine = startLine,
        };

        // Parse true body
        var trueBody = new TemplateDocumentNode();
        var stop = ParseContent(trueBody.Children, EndElseElseIf);
        if (rightTrim)
            TrimLeadingForRightTrim(trueBody.Children);
        if (stop.LeftTrim)
            TrimTrailingWhitespace(trueBody.Children);
        block.TrueBody = trueBody;

        // Handle else-if chain
        while (stop.Keyword == "else if")
        {
            // Capture trim marker before ParseContent overwrites `stop`
            var elseIfTrim = stop.LeftTrim;

            // Skip past "else if" keyword accounting for leading whitespace
            var elseTrimmed = stop.Expression.TrimStart();
            var elseIfKeyword = "else if";
            var elseIfCondition = elseTrimmed.StartsWith(elseIfKeyword, StringComparison.Ordinal) && elseTrimmed.Length > elseIfKeyword.Length
                ? elseTrimmed[elseIfKeyword.Length..].Trim()
                : string.Empty;

            var branchDoc = new TemplateDocumentNode();
            var branchRightTrim = stop.RightTrim;
            stop = ParseContent(branchDoc.Children, EndElseElseIf);
            if (branchRightTrim)
                TrimLeadingForRightTrim(branchDoc.Children);
            if (stop.LeftTrim)
                TrimTrailingWhitespace(branchDoc.Children);

            block.ElseIfChain.Add(new ElseIfBranch
            {
                Condition = elseIfCondition,
                Body = branchDoc,
                TrimMarker = elseIfTrim,
            });
        }

        // Handle final else
        if (stop.Keyword == "else")
        {
            var elseDoc = new TemplateDocumentNode();
            var elseRightTrim = stop.RightTrim;
            var elseStop = ParseContent(elseDoc.Children, EndOnly);
            if (elseRightTrim)
                TrimLeadingForRightTrim(elseDoc.Children);
            if (elseStop.LeftTrim)
                TrimTrailingWhitespace(elseDoc.Children);
            block.FalseBody = elseDoc;

            if (elseStop.Keyword == null)
                throw new TemplateParseException(
                    $"Missing 'end' for 'else' branch of '{keyword}' block",
                    stop.Line, stop.Column, stop.Offset);

            stop = elseStop;
        }

        // If the last stop keyword was not "end" (EOF), the block is not closed
        if (stop.Keyword == null)
            throw new TemplateParseException(
                $"Missing 'end' for '{keyword}' block",
                startLine, startCol, startOffset);

        block.EndRightTrim = stop.RightTrim;
        return block;
    }

    /// <summary>
    /// Strips <em>all</em> leading whitespace from the first child text node.
    /// Go block right-trim (<c>-}}</c> on <c>if</c>/<c>range</c>/<c>with</c>) consumes
    /// every whitespace character — spaces, tabs, and newlines — between the closing
    /// delimiter and the first non-whitespace character in the body.  This is broader
    /// than the standard action right-trim (spaces/tabs + at most one newline).
    /// </summary>
    private static void TrimLeadingForRightTrim(List<TemplateNode> children)
    {
        if (children.FirstOrDefault() is not TextNode text)
            return;

        var content = text.Content;
        var newStart = 0;
        while (newStart < content.Length && char.IsWhiteSpace(content[newStart]))
            newStart++;

        children[0] = new TextNode
        {
            Content = content[newStart..],
            StartOffset = text.StartOffset,
            EndOffset = text.EndOffset,
            StartLine = text.StartLine,
            EndLine = text.EndLine,
        };
    }

    private static void TrimTrailingWhitespace(List<TemplateNode> children)
    {
        if (children.LastOrDefault() is not TextNode text)
            return;

        var content = text.Content;
        var end = content.Length;
        while (end > 0 && char.IsWhiteSpace(content[end - 1]))
            end--;

        children[^1] = new TextNode
        {
            Content = content[..end],
            StartOffset = text.StartOffset,
            EndOffset = text.EndOffset,
            StartLine = text.StartLine,
            EndLine = text.EndLine,
        };
    }

    private TextNode ParseText()
    {
        var t = _tokens[_pos];
        _pos++;
        return new TextNode
        {
            Content = t.Value,
            StartOffset = t.Offset,
            EndOffset = t.Offset + t.Value.Length,
            StartLine = t.Line,
            EndLine = t.Line,
        };
    }

    private static TemplateNode MakeNode(string expr, bool leftTrim, bool rightTrim, int startOffset, int startLine)
    {
        if (expr.TrimStart().StartsWith("/*", StringComparison.Ordinal))
            return new CommentNode
            {
                Content = expr.Trim(),
                LeftTrim = leftTrim,
                RightTrim = rightTrim,
                StartOffset = startOffset,
                StartLine = startLine
            };

        return new ActionNode
        {
            Expression = expr.Trim(),
            LeftTrim = leftTrim,
            RightTrim = rightTrim,
            StartOffset = startOffset,
            StartLine = startLine,
        };
    }

    /// <summary>Returns the first whitespace-delimited word from an expression. Handles "else if" as one word.</summary>
    internal static string GetFirstWord(string expr)
    {
        expr = expr.TrimStart();
        if (expr.Length == 0) return string.Empty;

        if (expr.StartsWith("else", StringComparison.Ordinal))
        {
            var after = expr[4..].TrimStart();
            if (after.StartsWith("if", StringComparison.Ordinal))
            {
                var c = after.Length > 2 ? after[2] : '\0';
                if (!char.IsLetterOrDigit(c))
                    return "else if";
            }
            return "else";
        }

        var end = 0;
        while (end < expr.Length && !char.IsWhiteSpace(expr[end]))
            end++;
        return expr[..end];
    }

    /// <summary>
    /// Extracts the first quoted argument after a keyword (e.g. the template name in
    /// <c>define "mytpl"</c>). Returns <see langword="null"/> when the argument is not
    /// quoted, since Go templates require quoted names for <c>define</c> and <c>template</c>.
    /// </summary>
    private static string? ExtractQuotedFirstArg(string expr, string keyword)
    {
        // Trim leading whitespace, then skip past the keyword
        var remaining = expr.TrimStart();
        if (remaining.StartsWith(keyword, StringComparison.Ordinal))
            remaining = remaining[keyword.Length..].TrimStart();

        if (remaining.Length >= 2)
        {
            var quote = remaining[0];
            if (quote is '"' or '\'')
            {
                var i = remaining.IndexOf(quote, 1);
                if (i > 0) return remaining[1..i];
            }
        }
        return null;
    }
}
