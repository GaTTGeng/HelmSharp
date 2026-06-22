using System.Text;

namespace HelmSharp.Engine;

/// <summary>
/// Token types produced by the template tokenizer.
/// Kept minimal — only structural tokens. Expression content is carried as raw text.
/// </summary>
public enum TokenKind
{
    /// <summary>Raw text between actions.</summary>
    Text,

    /// <summary>Opening action delimiter (<c>{{</c> or <c>{{-</c>).</summary>
    LeftDelim,

    /// <summary>Raw content between delimiters. The expression is stored as-is.</summary>
    ActionContent,

    /// <summary>Closing action delimiter (<c>}}</c> or <c>-}}</c>).</summary>
    RightDelim,

    /// <summary>End of template input.</summary>
    EndOfFile,
}

/// <summary>
/// A single token produced by <see cref="TemplateTokenizer"/>.
/// </summary>
public sealed class Token
{
    public TokenKind Kind { get; set; }

    /// <summary>Raw text value of this token.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Byte offset where this token starts in the original template.</summary>
    public int Offset { get; set; }

    /// <summary>Starting line number (1-based).</summary>
    public int Line { get; set; }

    /// <summary>Starting column number (1-based).</summary>
    public int Column { get; set; }

    /// <summary>
    /// True when the surrounding left delimiter included a trim marker (<c>{{-</c>).
    /// Only meaningful on LeftDelim tokens.
    /// </summary>
    public bool LeftTrim { get; set; }

    /// <summary>
    /// True when the surrounding right delimiter included a trim marker (<c>-}}</c>).
    /// Only meaningful on RightDelim tokens.
    /// </summary>
    public bool RightTrim { get; set; }

    public override string ToString()
        => $"{Kind}({Value}) at {Line}:{Column}";
}

/// <summary>
/// Converts raw Helm/Go template text into a flat stream of structural tokens:
/// Text, LeftDelim, ActionContent, RightDelim, EndOfFile.
/// </summary>
/// <remarks>
/// Expression content inside delimiters is emitted as a single <see cref="TokenKind.ActionContent"/>
/// token — the existing evaluation engine handles expression parsing.
/// Only structural constructs (block keywords, define, else, end) are inspected by the parser.
/// </remarks>
public sealed class TemplateTokenizer
{
    private readonly string _input;
    private int _pos;
    private int _line;
    private int _col;

    public TemplateTokenizer(string input)
    {
        _input = input;
        _pos = 0;
        _line = 1;
        _col = 1;
    }

    /// <summary>
    /// Yields all tokens as a flat stream (Text, LeftDelim, ActionContent, RightDelim, ..., EndOfFile).
    /// </summary>
    public IEnumerable<Token> TokenizeFlat()
    {
        _pos = 0;
        _line = 1;
        _col = 1;

        while (_pos < _input.Length)
        {
            // Check for action delimiter
            if (TryMatchLeftDelim(out var leftTrim))
            {
                yield return ConsumeLeftDelim(leftTrim);

                // Save position before consuming action content
                var contentStartOffset = _pos;
                var contentStartLine = _line;
                var contentStartCol = _col;

                var (content, rightTrim) = ConsumeActionContent();
                yield return new Token
                {
                    Kind = TokenKind.ActionContent,
                    Value = content,
                    Offset = contentStartOffset,
                    Line = contentStartLine,
                    Column = contentStartCol,
                    LeftTrim = leftTrim,
                };

                yield return new Token
                {
                    Kind = TokenKind.RightDelim,
                    Value = rightTrim ? "-}}" : "}}",
                    Offset = _pos,
                    Line = _line,
                    Column = _col,
                    RightTrim = rightTrim,
                };
            }
            else
            {
                // Consume text until next {{ or EOF
                var textStartOffset = _pos;
                var textStartLine = _line;
                var textStartCol = _col;

                var text = ConsumeText();
                if (text.Length > 0)
                {
                    yield return new Token
                    {
                        Kind = TokenKind.Text,
                        Value = text,
                        Offset = textStartOffset,
                        Line = textStartLine,
                        Column = textStartCol,
                    };
                }
            }
        }

        yield return new Token { Kind = TokenKind.EndOfFile, Value = string.Empty, Offset = _pos, Line = _line, Column = _col };
    }

    private bool TryMatchLeftDelim(out bool leftTrim)
    {
        leftTrim = false;
        if (_pos + 1 >= _input.Length || _input[_pos] != '{' || _input[_pos + 1] != '{')
            return false;

        // {{-
        if (_pos + 2 < _input.Length && _input[_pos + 2] == '-')
        {
            leftTrim = true;
            return true;
        }

        return true;
    }

    private Token ConsumeLeftDelim(bool leftTrim)
    {
        var startOffset = _pos;
        var startLine = _line;
        var startCol = _col;
        var len = leftTrim ? 3 : 2;

        _pos += len;
        _col += len;

        return new Token
        {
            Kind = TokenKind.LeftDelim,
            Value = leftTrim ? "{{-" : "{{",
            Offset = startOffset,
            Line = startLine,
            Column = startCol,
            LeftTrim = leftTrim,
        };
    }

    private (string content, bool rightTrim) ConsumeActionContent()
    {
        var sb = new StringBuilder();
        var rightTrim = false;

        while (_pos < _input.Length)
        {
            // Check for }}
            if (_input[_pos] == '}' && _pos + 1 < _input.Length && _input[_pos + 1] == '}')
            {
                rightTrim = false;
                _pos += 2;
                _col += 2;
                return (sb.ToString(), rightTrim);
            }

            // Check for -}} (right trim, but not --}})
            if (_input[_pos] == '-' &&
                _pos + 1 < _input.Length && _input[_pos + 1] == '}' &&
                _pos + 2 < _input.Length && _input[_pos + 2] == '}' &&
                (_pos == 0 || _input[_pos - 1] != '-'))
            {
                rightTrim = true;
                _pos += 3;
                _col += 3;
                return (sb.ToString(), rightTrim);
            }

            // Skip quoted strings (don't mistake }} inside strings for delimiters)
            if (_input[_pos] is '"' or '\'')
            {
                var quote = _input[_pos];
                sb.Append(quote);
                _pos++;
                _col++;

                while (_pos < _input.Length)
                {
                    if (_input[_pos] == '\\' && _pos + 1 < _input.Length)
                    {
                        sb.Append(_input[_pos]);
                        sb.Append(_input[_pos + 1]);
                        _pos += 2;
                        _col += 2;
                        continue;
                    }
                    sb.Append(_input[_pos]);
                    _pos++;
                    _col++;
                    if (_input[_pos - 1] == quote)
                        break;
                }
                continue;
            }

            // Skip comments (don't mistake }} inside comments)
            if (_input[_pos] == '/' && _pos + 1 < _input.Length && _input[_pos + 1] == '*')
            {
                sb.Append('/');
                sb.Append('*');
                _pos += 2;
                _col += 2;

                while (_pos + 1 < _input.Length)
                {
                    if (_input[_pos] == '*' && _input[_pos + 1] == '/')
                    {
                        sb.Append('*');
                        sb.Append('/');
                        _pos += 2;
                        _col += 2;
                        break;
                    }
                    var commentCh = _input[_pos];
                    sb.Append(commentCh);
                    _pos++;
                    if (commentCh == '\n')
                    {
                        _line++;
                        _col = 1;
                    }
                    else
                    {
                        _col++;
                    }
                }
                continue;
            }

            // Regular character — add to content
            var ch = _input[_pos];
            sb.Append(ch);
            _pos++;
            if (ch == '\n')
            {
                _line++;
                _col = 1;
            }
            else
            {
                _col++;
            }
        }

        return (sb.ToString(), false);
    }

    private string ConsumeText()
    {
        var sb = new StringBuilder();

        while (_pos < _input.Length)
        {
            if (_input[_pos] == '{' && _pos + 1 < _input.Length && _input[_pos + 1] == '{')
                break;

            var ch = _input[_pos];
            sb.Append(ch);
            _pos++;

            if (ch == '\n')
            {
                _line++;
                _col = 1;
            }
            else
            {
                _col++;
            }
        }

        return sb.ToString();
    }
}
