namespace HelmSharp.Engine;

/// <summary>
/// Thrown when a template cannot be parsed because of malformed input:
/// unclosed action delimiters, missing <c>end</c> keywords, or
/// other structural errors that prevent a complete AST from being built.
/// </summary>
public sealed class TemplateParseException : Exception
{
    /// <summary>The 1-based line number where the error was detected.</summary>
    public int Line { get; }

    /// <summary>The 1-based column number where the error was detected.</summary>
    public int Column { get; }

    /// <summary>The byte offset into the template string where the error was detected.</summary>
    public int Offset { get; }

    public TemplateParseException(string message, int line, int column, int offset)
        : base(FormatMessage(message, line, column, offset))
    {
        Line = line;
        Column = column;
        Offset = offset;
    }

    private static string FormatMessage(string message, int line, int column, int offset)
        => $"{message} at line {line}, column {column} (offset {offset})";
}
