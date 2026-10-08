namespace HelmSharp.Action;

/// <summary>
/// Captures the outcome of a Helm operation as a CLI-style result: exit code plus
/// captured standard output and error streams.
/// </summary>
public class CommandResult
{
    /// <summary>Process exit code; zero indicates success.</summary>
    public int ExitCode { get; set; }

    /// <summary>Text written to standard output by the operation.</summary>
    public string StandardOutput { get; set; } = string.Empty;

    /// <summary>Text written to standard error by the operation.</summary>
    public string StandardError { get; set; } = string.Empty;

    /// <summary>True when <see cref="ExitCode"/> is zero.</summary>
    public bool Succeeded => ExitCode == 0;
}
