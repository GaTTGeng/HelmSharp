namespace HelmSharp.Engine;

/// <summary>
/// Parses a flat token stream from <see cref="TemplateTokenizer"/> into a nested AST.
/// </summary>
/// <remarks>
/// <para>The parser handles structural template constructs:
/// <c>define</c>/<c>end</c>, <c>if</c>/<c>else if</c>/<c>else</c>/<c>end</c>,
/// <c>with</c>/<c>else</c>/<c>end</c>, <c>range</c>/<c>else</c>/<c>end</c>.</para>
/// <para>
/// Expression content inside actions is stored as raw strings — the existing
/// evaluation engine handles expression parsing and evaluation.
/// </para>
/// </remarks>
public sealed class TemplateParser
{
    private readonly IEnumerator<Token> _tokens;
    private Token _current;
    private bool _hasCurrent;

    // Define nodes extracted during parsing (collected for the renderer).
    private readonly Dictionary<string, DefineNode> _defines = new(StringComparer.Ordinal);

    public TemplateParser(IEnumerable<Token> tokens)
    {
        _tokens = tokens.GetEnumerator();
        Advance();
    }

    /// <summary>
    /// Returns the defines extracted during parsing, keyed by template name.
    /// </summary>
    public IReadOnlyDictionary<string, DefineNode> Defines => _defines;

    /// <summary>
    /// Parses the full template into a <see cref="TemplateDocumentNode"/>.
    /// </summary>
    public TemplateDocumentNode Parse()
    {
        var document = new TemplateDocumentNode();
        ParseNodes(document.Children, stopAtEnd: false, isTopLevel: true);

        // Update document-level position from children
        if (document.Children.Count > 0)
        {
            document.StartOffset = document.Children[0].StartOffset;
            document.EndOffset = document.Children[^1].EndOffset;
            document.StartLine = document.Children[0].StartLine;
            document.EndLine = document.Children[^1].EndLine;
        }

        return document;
    }

    /// <summary>
    /// Parse template content into children of a parent node. Returns true if
    /// parsing stopped because of an <c>end</c> or <c>else</c>/<c>else if</c> keyword.
    /// </summary>
    private bool ParseNodes(List<TemplateNode> children, bool stopAtEnd, bool isTopLevel)
    {
        while (_hasCurrent)
        {
            if (_current.Kind == TokenKind.EndOfFile)
                break;

            if (_current.Kind == TokenKind.Text)
            {
                children.Add(ParseText());
                continue;
            }

            if (_current.Kind == TokenKind.LeftDelim)
            {
                var leftTrim = _current.LeftTrim;
                var leftToken = _current;
                Advance(); // consume LeftDelim

                // Skip to content — look for Identifier or Comment
                while (_hasCurrent && _current.Kind != TokenKind.Identifier &&
                       _current.Kind != TokenKind.Comment && _current.Kind != TokenKind.RightDelim &&
                       _current.Kind != TokenKind.EndOfFile)
                {
                    Advance();
                }

                if (!_hasCurrent || _current.Kind == TokenKind.EndOfFile)
                    break;

                var keyword = _current.Kind == TokenKind.Identifier ? _current.Value : string.Empty;

                switch (keyword)
                {
                    case "define":
                    {
                        var defineNode = ParseDefine(leftTrim);
                        _defines[defineNode.Name] = defineNode;
                        // Define nodes are not added to children — they are extracted
                        break;
                    }

                    case "end":
                        Advance(); // consume 'end'
                        ConsumeUntilRightDelim(); // skip to and consume }}
                        return true; // signal end-of-block

                    case "else":
                    case "else if":
                        return true; // signal else/else-if boundary

                    case "if":
                    case "with":
                    case "range":
                    {
                        var blockNode = ParseBlock(keyword, leftTrim);
                        children.Add(blockNode);
                        break;
                    }

                    default:
                    {
                        if (_current.Kind == TokenKind.Comment)
                        {
                            children.Add(ParseComment(leftTrim));
                        }
                        else
                        {
                            children.Add(ParseAction(keyword, leftTrim));
                        }
                        break;
                    }
                }
                continue;
            }

            // Unexpected token — skip it
            Advance();
        }

        return false;
    }

    private TextNode ParseText()
    {
        var node = new TextNode
        {
            Content = _current.Value,
            StartOffset = _current.Offset,
            EndOffset = _current.Offset + _current.Value.Length,
            StartLine = _current.Line,
            EndLine = _current.Line,
        };
        Advance();
        return node;
    }

    private DefineNode ParseDefine(bool leftTrim)
    {
        var startToken = _current; // 'define' keyword
        Advance(); // consume 'define'

        // Consume the template name (quoted string)
        var nameToken = ConsumeNonRightDelim();
        var name = Unquote(nameToken.Value);

        // Consume any remaining tokens until RightDelim (the opening define tag's }})
        var rightTrimOpen = ConsumeUntilRightDelim();

        // Now parse the body until we hit {{ end }}
        var bodyDoc = new TemplateDocumentNode();
        ParseNodes(bodyDoc.Children, stopAtEnd: true, isTopLevel: false);

        var defineNode = new DefineNode
        {
            Name = name,
            Body = bodyDoc,
            LeftTrim = leftTrim,
            RightTrim = rightTrimOpen,
            StartOffset = startToken.Offset,
            StartLine = startToken.Line,
        };

        // Update end position from body or use current position
        if (bodyDoc.Children.Count > 0)
        {
            defineNode.EndOffset = bodyDoc.Children[^1].EndOffset;
            defineNode.EndLine = bodyDoc.Children[^1].EndLine;
        }

        return defineNode;
    }

    private BlockNode ParseBlock(string keyword, bool leftTrim)
    {
        var startToken = _current;
        Advance(); // consume keyword (if/with/range)

        // Build the expression string: everything until RightDelim
        var exprBuilder = new System.Text.StringBuilder();
        var hasRightTrim = false;

        while (_hasCurrent && _current.Kind != TokenKind.RightDelim && _current.Kind != TokenKind.EndOfFile)
        {
            if (exprBuilder.Length > 0)
                exprBuilder.Append(' ');
            exprBuilder.Append(_current.Value);
            Advance();
        }

        if (_hasCurrent && _current.Kind == TokenKind.RightDelim)
        {
            hasRightTrim = _current.RightTrim;
            Advance();
        }

        // Parse true body until else/else-if/end
        var trueBodyDoc = new TemplateDocumentNode();
        var elseFound = ParseNodes(trueBodyDoc.Children, stopAtEnd: true, isTopLevel: false);

        var block = new BlockNode
        {
            Keyword = keyword,
            Expression = exprBuilder.ToString().Trim(),
            LeftTrim = leftTrim,
            RightTrim = hasRightTrim,
            TrueBody = trueBodyDoc,
            StartOffset = startToken.Offset,
            StartLine = startToken.Line,
        };

        // Handle else / else-if chain
        if (elseFound && _hasCurrent && _current.Kind == TokenKind.Identifier)
        {
            var elseKeyword = _current.Value;

            if (elseKeyword == "else if")
            {
                ParseElseIfChain(block);
            }
            else if (elseKeyword == "else")
            {
                Advance(); // consume 'else'
                ConsumeUntilRightDelim(); // consume }}

                var elseBodyDoc = new TemplateDocumentNode();
                ParseNodes(elseBodyDoc.Children, stopAtEnd: true, isTopLevel: false);
                block.FalseBody = elseBodyDoc;
            }
        }

        // Update end position
        if (block.FalseBody is TemplateDocumentNode fd && fd.Children.Count > 0)
        {
            block.EndOffset = fd.Children[^1].EndOffset;
            block.EndLine = fd.Children[^1].EndLine;
        }
        else if (trueBodyDoc.Children.Count > 0)
        {
            block.EndOffset = trueBodyDoc.Children[^1].EndOffset;
            block.EndLine = trueBodyDoc.Children[^1].EndLine;
        }

        return block;
    }

    private void ParseElseIfChain(BlockNode block)
    {
        while (_hasCurrent && _current.Kind == TokenKind.Identifier && _current.Value == "else if")
        {
            Advance(); // consume 'else if'

            // Build condition expression until RightDelim
            var condBuilder = new System.Text.StringBuilder();
            while (_hasCurrent && _current.Kind != TokenKind.RightDelim && _current.Kind != TokenKind.EndOfFile)
            {
                if (condBuilder.Length > 0)
                    condBuilder.Append(' ');
                condBuilder.Append(_current.Value);
                Advance();
            }

            if (_hasCurrent && _current.Kind == TokenKind.RightDelim)
                Advance(); // consume }}

            var branchDoc = new TemplateDocumentNode();
            var foundElseOrEnd = ParseNodes(branchDoc.Children, stopAtEnd: true, isTopLevel: false);

            block.ElseIfChain.Add(new ElseIfBranch
            {
                Condition = condBuilder.ToString().Trim(),
                Body = branchDoc,
            });

            // If we stopped at 'else', parse the final else body
            if (foundElseOrEnd && _hasCurrent && _current.Kind == TokenKind.Identifier && _current.Value == "else")
            {
                Advance(); // consume 'else'
                ConsumeUntilRightDelim();
                var elseDoc = new TemplateDocumentNode();
                ParseNodes(elseDoc.Children, stopAtEnd: true, isTopLevel: false);
                block.FalseBody = elseDoc;
                break;
            }
        }
    }

    private CommentNode ParseComment(bool leftTrim)
    {
        var node = new CommentNode
        {
            Content = _current.Value,
            StartOffset = _current.Offset,
            EndOffset = _current.Offset + _current.Value.Length,
            StartLine = _current.Line,
            EndLine = _current.Line,
        };
        Advance(); // consume comment
        ConsumeUntilRightDelim(); // consume }}
        return node;
    }

    private ActionNode ParseAction(string keyword, bool leftTrim)
    {
        var startToken = _current.Kind == TokenKind.Identifier ? _current : default;
        var startOffset = _current.Offset;
        var startLine = _current.Line;

        // Build expression: keyword + remaining tokens until RightDelim
        var exprBuilder = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(keyword))
            exprBuilder.Append(keyword);

        var rightTrim = false;

        while (_hasCurrent && _current.Kind != TokenKind.RightDelim && _current.Kind != TokenKind.EndOfFile)
        {
            if (exprBuilder.Length > 0 && _current.Kind != TokenKind.Dot)
                exprBuilder.Append(' ');

            if (_current.Kind == TokenKind.LeftParen)
            {
                // Build sub-expression until matching )
                exprBuilder.Append('(');
                Advance();
                var depth = 1;
                while (_hasCurrent && depth > 0)
                {
                    if (_current.Kind == TokenKind.LeftParen) depth++;
                    else if (_current.Kind == TokenKind.RightParen)
                    {
                        depth--;
                        if (depth == 0)
                        {
                            exprBuilder.Append(')');
                            Advance();
                            break;
                        }
                    }
                    exprBuilder.Append(_current.Value);
                    Advance();
                }
                continue;
            }

            exprBuilder.Append(_current.Value);
            Advance();
        }

        if (_hasCurrent && _current.Kind == TokenKind.RightDelim)
        {
            rightTrim = _current.RightTrim;
            Advance(); // consume }}
        }

        return new ActionNode
        {
            Expression = exprBuilder.ToString().Trim(),
            LeftTrim = leftTrim,
            RightTrim = rightTrim,
            StartOffset = startOffset,
            EndOffset = _current.Offset,
            StartLine = startLine,
            EndLine = _current.Line,
        };
    }

    private Token ConsumeNonRightDelim()
    {
        while (_hasCurrent && _current.Kind != TokenKind.RightDelim && _current.Kind != TokenKind.EndOfFile)
        {
            var token = _current;
            Advance();
            return token;
        }
        return default;
    }

    private bool ConsumeUntilRightDelim()
    {
        var rightTrim = false;
        while (_hasCurrent && _current.Kind != TokenKind.RightDelim && _current.Kind != TokenKind.EndOfFile)
            Advance();

        if (_hasCurrent && _current.Kind == TokenKind.RightDelim)
        {
            rightTrim = _current.RightTrim;
            Advance();
        }

        return rightTrim;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }

    private void Advance()
    {
        _hasCurrent = _tokens.MoveNext();
        if (_hasCurrent)
            _current = _tokens.Current;
    }
}
