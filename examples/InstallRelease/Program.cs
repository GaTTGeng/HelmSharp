// Demonstrates install/upgrade of a release through HelmClient. Safe by default:
// runs as a dry-run unless --apply is passed, so no cluster changes happen by accident.
// Usage: InstallRelease [chart-path] [release-name] [--apply]
using HelmSharp.Action;

var chartPath = args.Length > 0
    ? args[0]
    // Fall back to examples/sample-chart relative to the binary output directory.
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "sample-chart"));
var releaseName = args.Length > 1 ? args[1] : "demo";
var dryRun = !args.Contains("--apply", StringComparer.OrdinalIgnoreCase);

Console.WriteLine(dryRun
    ? $"Rendering dry-run release '{releaseName}' from '{chartPath}'..."
    : $"Installing release '{releaseName}' from '{chartPath}'...");

var client = new HelmClient(new StaticHelmOptionsProvider());

var result = await client.UpgradeInstallAsync(new HelmUpgradeInstallRequest
{
    ReleaseName = releaseName,
    Namespace = "default",
    Chart = chartPath,
    CreateNamespace = true,
    Wait = true,
    TimeoutSeconds = 300,
    DryRun = dryRun
});

if (result.ExitCode != 0)
{
    Console.Error.WriteLine($"Failed (exit code {result.ExitCode}):");
    Console.Error.WriteLine(result.StandardError);
    return;
}

Console.WriteLine(result.StandardOutput);

if (!dryRun)
{
    var status = await client.StatusAsync(releaseName);
    Console.WriteLine($"Release status: {status.StandardOutput}");
}
else
{
    Console.WriteLine("Dry run completed. Pass --apply to submit resources to the configured Kubernetes cluster.");
}

// Minimal options provider pinned to the "default" namespace and a fixed field manager.
sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
{
    public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new HelmExecutionOptions
        {
            DefaultNamespace = "default",
            FieldManager = "helmsharp"
        });
}
