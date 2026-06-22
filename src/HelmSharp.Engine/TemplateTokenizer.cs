using System.Text;

namespace HelmSharp.Engine;

/// <summary>
/// Token types produced by the template tokenizer.
/// </summary>
public enum TokenKind
{
    /// <summary>Raw text between actions.</summary>
    Text,

    /// <summary>Opening action delimiter (<c>{{</c> or <c>{{-</c>).</summary>
    LeftDelim,

    /// <summary>Closing action delimiter (<c>}}</c> or <c>-}}</c>).</summary>
    RightDelim,

    /// <summary>Comment inside an action (<c>/* ... */</c>).</summary>
    Comment,

    /// <summary>
    /// Keyword or identifier token inside an action
    /// (function names, keywords like <c>if</c>/<c>end</c>/<c>range</c>, etc.).
    /// </summary>
    Identifier,

    /// <summary>Quoted string literal (<c>"..."</c> or <c>'...'</c>).</summary>
    StringLiteral,

    /// <summary>Numeric literal (<c>123</c>, <c>3.14</c>).</summary>
    NumberLiteral,

    /// <summary>Boolean literal (<c>true</c> or <c>false</c>).</summary>
    BoolLiteral,

    /// <summary>Nil literal (<c>nil</c>).</summary>
    NilLiteral,

    /// <summary>Variable token (<c>$name</c>).</summary>
    Variable,

    /// <summary>The dot token (<c>.</c>).</summary>
    Dot,

    /// <summary>Pipeline operator (<c>|</c>).</summary>
    Pipe,

    /// <summary>Assignment (<c>:=</c>) or declaration (<c>=</c>).</summary>
    Assign,

    /// <summary>Open parenthesis (<c>(</c>).</summary>
    LeftParen,

    /// <summary>Close parenthesis (<c>)</c>).</summary>
    RightParen,

    /// <summary>Comma (<c>,</c>).</summary>
    Comma,

    /// <summary>Colon (<c>:</c>).</summary>
    Colon,

    /// <summary>End of template input.</summary>
    EndOfFile,
}

/// <summary>
/// A single token produced by <see cref="TemplateTokenizer"/>.
/// </summary>
public struct Token
{
    public TokenKind Kind { get; init; }
    public string Value { get; init; }
    public int Offset { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }

    /// <summary>
    /// True when the surrounding left delimiter included a trim marker (<c>{{-</c>).
    /// Only meaningful for tokens that follow a LeftDelim.
    /// </summary>
    public bool LeftTrim { get; set; }

    /// <summary>
    /// True when the surrounding right delimiter included a trim marker (<c>-}}</c>).
    /// Only meaningful for tokens that precede a RightDelim.
    /// </summary>
    public bool RightTrim { get; set; }

    public override string ToString()
        => $"{Kind}({Value}) at {Line}:{Column}";
}

/// <summary>
/// Converts raw Helm/Go template text into a stream of <see cref="Token"/> values.
/// </summary>
/// <remarks>
/// Operates in two phases:
/// <list type="number">
/// <item>Split the template at <c>{{</c> boundaries into text and action segments.</item>
/// <item>Tokenize the content of each action segment (keywords, literals, operators, etc.).</item>
/// </list>
/// Comments (<c>{{/* ... */}}</c>) are emitted as a single <see cref="TokenKind.Comment"/> token.
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
    /// Returns all tokens from the input template.
    /// </summary>
    public List<Token> TokenizeAll()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Kind == TokenKind.EndOfFile)
                break;
        }
        return tokens;
    }

    /// <summary>
    /// Returns the next token from the input stream.
    /// </summary>
    public Token NextToken()
    {
        SkipNonNewlineWhitespace();

        if (_pos >= _input.Length)
            return MakeToken(TokenKind.EndOfFile, string.Empty);

        // Check for action delimiters: {{ or {{-
        if (MatchActionStart(out var leftTrim))
        {
            var startOffset = _pos;
            var startLine = _line;
            var startCol = _col;

            // Advance past the opening delimiter
            _pos += leftTrim ? 3 : 2;
            _col += leftTrim ? 3 : 2;

            var leftToken = new Token
            {
                Kind = TokenKind.LeftDelim,
                Value = leftTrim ? "{{-" : "{{",
                Offset = startOffset,
                Line = startLine,
                Column = startCol,
                LeftTrim = leftTrim,
            };

            // Tokenize content inside the action until }}
            var actionTokens = TokenizeActionContent(leftTrim, out var rightTrim);

            // Build the result list: LeftDelim + content + RightDelim
            // But we need to return one at a time — store action tokens for later
            // We'll handle this by buffering

            // For the caller-friendly API, emit LeftDelim, then content tokens, then RightDelim
            // Store remaining action tokens in a buffer
            _bufferedTokens = actionTokens;
            _bufferedRightTrim = rightTrim;
            return leftToken;
        }

        // Plain text until next {{ or EOF
        return ConsumeText();
    }

    private List<Token>? _bufferedTokens;
    private bool _bufferedRightTrim;

    /// <summary>
    /// Returns the next token, draining buffered action tokens first.
    /// Call this instead of NextToken() for consumers that want flat token streams.
    /// </summary>
    public Token NextTokenFlat()
    {
        // Drain buffered action tokens
        if (_bufferedTokens is { Count: > 0 })
        {
            var token = _bufferedTokens[0];
            _bufferedTokens.RemoveAt(0);

            // If this is the last action token, emit RightDelim next
            if (_bufferedTokens.Count == 0)
            {
                var rightDelimToken = new Token
                {
                    Kind = TokenKind.RightDelim,
                    Value = _bufferedRightTrim ? "-}}" : "}}",
                    Offset = token.Offset,
                    Line = token.Line,
                    Column = token.Column,
                    RightTrim = _bufferedRightTrim,
                };
                _bufferedTokens = null;
                // Buffer the right delim
                _bufferedTokens = new List<Token> { rightDelimToken };
            }

            return token;
        }

        return NextToken();
    }

    /// <summary>
    /// Yields all tokens as a flat stream (Text, LeftDelim, content..., RightDelim, ...).
    /// </summary>
    public IEnumerable<Token> TokenizeFlat()
    {
        _pos = 0;
        _line = 1;
        _col = 1;
        _bufferedTokens = null;

        while (true)
        {
            var token = NextTokenFlat();
            yield return token;
            if (token.Kind == TokenKind.EndOfFile)
                yield break;
        }
    }

    private Token ConsumeText()
    {
        var startOffset = _pos;
        var startLine = _line;
        var startCol = _col;
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

        return new Token
        {
            Kind = TokenKind.Text,
            Value = sb.ToString(),
            Offset = startOffset,
            Line = startLine,
            Column = startCol,
        };
    }

    private List<Token> TokenizeActionContent(bool leftTrim, out bool rightTrim)
    {
        var tokens = new List<Token>();
        rightTrim = false;

        // Find the matching }} or -}}
        var endIndex = FindActionEnd(out rightTrim);
        var content = _input[_pos..endIndex];

        // Check for comment
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("/*", StringComparison.Ordinal))
        {
            var commentEnd = content.IndexOf("*/", StringComparison.Ordinal);
            var commentText = commentEnd >= 0 ? content[..(commentEnd + 2)] : content;
            var offsetBase = _pos;
            var lineBase = _line;
            var colBase = _col;

            // Tokenize the comment content
            tokens.Add(new Token
            {
                Kind = TokenKind.Comment,
                Value = commentText.Trim(),
                Offset = offsetBase,
                Line = lineBase,
                Column = colBase,
                LeftTrim = leftTrim,
            });

            // Advance past content and closing delimiter
            _pos = endIndex + (rightTrim ? 3 : 2);
            AdvancePosition(content.Length + (rightTrim ? 3 : 2) + (endIndex - _pos + content.Length));

            return tokens;
        }

        // Tokenize the action expression
        var exprTokens = TokenizeExpression(content, _pos);
        foreach (var t in exprTokens)
        {
            var tok = t;
            tok.LeftTrim = leftTrim;
            tokens.Add(tok);
        }

        // Advance past content and closing delimiter
        var delLen = rightTrim ? 3 : 2;
        _pos = endIndex + delLen;
        AdvancePosition(content.Length + delLen);

        return tokens;
    }

    private int FindActionEnd(out bool rightTrim)
    {
        var searchPos = _pos;
        while (searchPos < _input.Length)
        {
            if (_input[searchPos] == '}' && searchPos + 1 < _input.Length && _input[searchPos + 1] == '}')
            {
                rightTrim = false;
                return searchPos;
            }

            // Check for -}} (right trim, but not --}})
            if (_input[searchPos] == '-' &&
                searchPos + 1 < _input.Length && _input[searchPos + 1] == '}' &&
                searchPos + 2 < _input.Length && _input[searchPos + 2] == '}' &&
                (searchPos == 0 || _input[searchPos - 1] != '-'))
            {
                rightTrim = true;
                return searchPos;
            }

            // Skip quoted strings
            if (_input[searchPos] is '"' or '\'')
            {
                var quote = _input[searchPos];
                searchPos++;
                while (searchPos < _input.Length)
                {
                    if (_input[searchPos] == '\\' && searchPos + 1 < _input.Length)
                    {
                        searchPos += 2;
                        continue;
                    }
                    if (_input[searchPos] == quote)
                    {
                        searchPos++;
                        break;
                    }
                    searchPos++;
                }
                continue;
            }

            // Skip comments
            if (_input[searchPos] == '/' && searchPos + 1 < _input.Length && _input[searchPos + 1] == '*')
            {
                searchPos += 2;
                while (searchPos + 1 < _input.Length)
                {
                    if (_input[searchPos] == '*' && _input[searchPos + 1] == '/')
                    {
                        searchPos += 2;
                        break;
                    }
                    searchPos++;
                }
                continue;
            }

            searchPos++;
        }

        rightTrim = false;
        return _input.Length;
    }

    private List<Token> TokenizeExpression(string expr, int baseOffset)
    {
        var tokens = new List<Token>();
        var pos = 0;

        // Calculate base line/col for the start of expression
        var baseLine = _line;
        var baseCol = _col;

        while (pos < expr.Length)
        {
            // Skip whitespace
            while (pos < expr.Length && IsSpaceWithoutNewline(expr[pos]))
                pos++;

            if (pos >= expr.Length)
                break;

            var ch = expr[pos];
            var curOffset = baseOffset + pos;
            var (curLine, curCol) = CalculateLineCol(expr, pos, baseLine, baseCol);

            // String literals
            if (ch is '"' or '\'')
            {
                var quote = ch;
                var sb = new StringBuilder();
                sb.Append(ch);
                pos++;
                while (pos < expr.Length)
                {
                    if (expr[pos] == '\\' && pos + 1 < expr.Length)
                    {
                        sb.Append(expr[pos]);
                        sb.Append(expr[pos + 1]);
                        pos += 2;
                        continue;
                    }
                    sb.Append(expr[pos]);
                    if (expr[pos] == quote)
                    {
                        pos++;
                        break;
                    }
                    pos++;
                }

                tokens.Add(new Token
                {
                    Kind = TokenKind.StringLiteral,
                    Value = sb.ToString(),
                    Offset = curOffset,
                    Line = curLine,
                    Column = curCol,
                });
                continue;
            }

            // Comment
            if (ch == '/' && pos + 1 < expr.Length && expr[pos + 1] == '*')
            {
                var commentStart = pos;
                pos += 2;
                while (pos + 1 < expr.Length)
                {
                    if (expr[pos] == '*' && expr[pos + 1] == '/')
                    {
                        pos += 2;
                        break;
                    }
                    pos++;
                }
                tokens.Add(new Token
                {
                    Kind = TokenKind.Comment,
                    Value = expr[commentStart..pos],
                    Offset = curOffset,
                    Line = curLine,
                    Column = curCol,
                });
                continue;
            }

            // Line comment (//) in template actions
            if (ch == '/' && pos + 1 < expr.Length && expr[pos + 1] == '/')
            {
                var commentStart = pos;
                while (pos < expr.Length && expr[pos] != '\n')
                    pos++;
                tokens.Add(new Token
                {
                    Kind = TokenKind.Comment,
                    Value = expr[commentStart..pos],
                    Offset = curOffset,
                    Line = curLine,
                    Column = curCol,
                });
                continue;
            }

            // Numbers
            if (char.IsDigit(ch) || (ch == '-' && pos + 1 < expr.Length && char.IsDigit(expr[pos + 1])))
            {
                var numStart = pos;
                pos++;
                var hasDot = false;
                while (pos < expr.Length)
                {
                    if (expr[pos] == '.' && !hasDot)
                    {
                        hasDot = true;
                        pos++;
                        continue;
                    }
                    if (char.IsDigit(expr[pos]))
                    {
                        pos++;
                        continue;
                    }
                    break;
                }
                tokens.Add(new Token
                {
                    Kind = TokenKind.NumberLiteral,
                    Value = expr[numStart..pos],
                    Offset = curOffset,
                    Line = curLine,
                    Column = curCol,
                });
                continue;
            }

            // Variables
            if (ch == '$')
            {
                var varStart = pos;
                pos++;
                while (pos < expr.Length && IsIdentChar(expr[pos]))
                    pos++;
                tokens.Add(new Token
                {
                    Kind = TokenKind.Variable,
                    Value = expr[varStart..pos],
                    Offset = curOffset,
                    Line = curLine,
                    Column = curCol,
                });
                continue;
            }

            // Identifiers and keywords
            if (IsIdentStart(ch))
            {
                var identStart = pos;
                pos++;
                while (pos < expr.Length && IsIdentChar(expr[pos]))
                    pos++;
                var ident = expr[identStart..pos];

                // Check for keyword detection: "else if"
                var elsePos = pos;
                while (elsePos < expr.Length && IsSpaceWithoutNewline(expr[elsePos]))
                    elsePos++;
                if (ident == "else" && elsePos + 1 < expr.Length && expr[elsePos] == 'i' && expr[elsePos + 1] == 'f')
                {
                    // Check that "if" is followed by non-ident char
                    var ifEnd = elsePos + 2;
                    if (ifEnd >= expr.Length || !IsIdentChar(expr[ifEnd]))
                    {
                        tokens.Add(new Token
                        {
                            Kind = TokenKind.Identifier,
                            Value = "else if",
                            Offset = curOffset,
                            Line = curLine,
                            Column = curCol,
                        });
                        pos = ifEnd;
                        continue;
                    }
                }

                tokens.Add(new Token
                {
                    Kind = TokenKind.Identifier,
                    Value = ident,
                    Offset = curOffset,
                    Line = curLine,
                    Column = curCol,
                });
                continue;
            }

            // Operators and punctuation
            switch (ch)
            {
                case '.':
                    tokens.Add(new Token { Kind = TokenKind.Dot, Value = ".", Offset = curOffset, Line = curLine, Column = curCol });
                    pos++;
                    break;
                case '|':
                    tokens.Add(new Token { Kind = TokenKind.Pipe, Value = "|", Offset = curOffset, Line = curLine, Column = curCol });
                    pos++;
                    break;
                case '(':
                    tokens.Add(new Token { Kind = TokenKind.LeftParen, Value = "(", Offset = curOffset, Line = curLine, Column = curCol });
                    pos++;
                    break;
                case ')':
                    tokens.Add(new Token { Kind = TokenKind.RightParen, Value = ")", Offset = curOffset, Line = curLine, Column = curCol });
                    pos++;
                    break;
                case ',':
                    tokens.Add(new Token { Kind = TokenKind.Comma, Value = ",", Offset = curOffset, Line = curLine, Column = curCol });
                    pos++;
                    break;
                case ':':
                    if (pos + 1 < expr.Length && expr[pos + 1] == '=')
                    {
                        tokens.Add(new Token { Kind = TokenKind.Assign, Value = ":=", Offset = curOffset, Line = curLine, Column = curCol });
                        pos += 2;
                    }
                    else
                    {
                        tokens.Add(new Token { Kind = TokenKind.Colon, Value = ":", Offset = curOffset, Line = curLine, Column = curCol });
                        pos++;
                    }
                    break;
                case '=':
                    tokens.Add(new Token { Kind = TokenKind.Assign, Value = "=", Offset = curOffset, Line = curLine, Column = curCol });
                    pos++;
                    break;
                default:
                    // Unrecognized character — treat as part of identifier for resilience
                    tokens.Add(new Token { Kind = TokenKind.Identifier, Value = ch.ToString(), Offset = curOffset, Line = curLine, Column = curCol });
                    pos++;
                    break;
            }
        }

        return tokens;
    }

    private (int line, int col) CalculateLineCol(string expr, int pos, int baseLine, int baseCol)
    {
        var line = baseLine;
        var col = baseCol;
        for (var i = 0; i < pos; i++)
        {
            if (expr[i] == '\n')
            {
                line++;
                col = 1;
            }
            else
            {
                col++;
            }
        }
        return (line, col);
    }

    private bool MatchActionStart(out bool leftTrim)
    {
        if (_pos + 1 >= _input.Length || _input[_pos] != '{' || _input[_pos + 1] != '{')
        {
            leftTrim = false;
            return false;
        }

        // Check for {{-
        if (_pos + 2 < _input.Length && _input[_pos + 2] == '-')
        {
            leftTrim = true;
            return true;
        }

        leftTrim = false;
        return true;
    }

    private void SkipNonNewlineWhitespace()
    {
        while (_pos < _input.Length)
        {
            var ch = _input[_pos];
            if (ch == ' ' || ch == '\t' || ch == '\r')
            {
                _pos++;
                _col++;
            }
            else
            {
                break;
            }
        }
    }

    private void AdvancePosition(int count)
    {
        for (var i = 0; i < count && _pos > 0; i++)
        {
            var ch = _input[_pos - count + i];
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
    }

    private static bool IsIdentStart(char ch)
        => char.IsLetter(ch) || ch == '_';

    private static bool IsIdentChar(char ch)
        => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-';

    private static bool IsSpaceWithoutNewline(char ch)
        => ch is ' ' or '\t' or '\r';

    private Token MakeToken(TokenKind kind, string value)
        => new()
        {
            Kind = kind,
            Value = value,
            Offset = _pos,
            Line = _line,
            Column = _col,
        };
}
