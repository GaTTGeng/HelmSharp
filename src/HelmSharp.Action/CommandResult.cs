namespace HelmSharp.Action;

public class CommandResult
{
    public int ExitCode { get; set; }

    public string StandardOutput { get; set; } = string.Empty;

    public string StandardError { get; set; } = string.Empty;

    public bool Succeeded => ExitCode == 0;
}
