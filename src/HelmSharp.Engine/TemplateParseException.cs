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

    /// <summary>
    /// Creates a parse error with a source location. The location is appended to
    /// <paramref name="message"/> so the exception message alone is actionable.
    /// </summary>
    /// <param name="message">Description of the structural error.</param>
    /// <param name="line">1-based line where the error was detected.</param>
    /// <param name="column">1-based column where the error was detected.</param>
    /// <param name="offset">Character offset into the template text where the error was detected.</param>
    public TemplateParseException(string message, int line, int column, int offset)
        : base(FormatMessage(message, line, column, offset))
    {
        Line = line;
        Column = column;
        Offset = offset;
    }

    /// <summary>Appends "at line L, column C (offset O)" to the raw message.</summary>
    private static string FormatMessage(string message, int line, int column, int offset)
        => $"{message} at line {line}, column {column} (offset {offset})";
}
