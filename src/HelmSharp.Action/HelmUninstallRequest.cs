namespace HelmSharp.Action;

public class HelmUninstallRequest
{
    public string ReleaseName { get; set; } = string.Empty;

    public string? Namespace { get; set; }

    public string? KubeConfigPath { get; set; }

    public string? KubeConfigContent { get; set; }
}
