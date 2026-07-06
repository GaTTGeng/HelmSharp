using System.Diagnostics;
using System.Text;

namespace HelmSharp.Tests;

internal static class HelmCliRunner
{
    private static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly Lazy<bool> HelmAvailability = new(
        DetectAvailability,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable() => HelmAvailability.Value;

    public static HelmCliHome CreateHome() => new();

    private static bool DetectAvailability()
    {
        try
        {
            var result = RunAsync(
                    ["version", "--short"],
                    AvailabilityTimeout,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Task<HelmCliResult> TemplateAsync(
        string chartPath,
        string releaseName,
        string releaseNamespace,
        CancellationToken cancellationToken)
        => RunAsync(
            [
                "template",
                releaseName,
                NormalizeHelmPath(chartPath),
                "--namespace",
                releaseNamespace,
                "--kube-version",
                "v1.29.0"
            ],
            CommandTimeout,
            cancellationToken);

    public static Task<HelmCliResult> PackageAsync(
        string chartPath,
        string destination,
        string? version,
        string? appVersion,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "package",
            NormalizeHelmPath(chartPath),
            "--destination",
            NormalizeHelmPath(destination)
        };

        if (version is not null)
        {
            arguments.Add("--version");
            arguments.Add(version);
        }

        if (appVersion is not null)
        {
            arguments.Add("--app-version");
            arguments.Add(appVersion);
        }

        return RunAsync(arguments, CommandTimeout, cancellationToken);
    }

    public static Task<HelmCliResult> RepoAddAsync(
        string name,
        string url,
        HelmCliHome home,
        CancellationToken cancellationToken)
        => RunAsync(
            [
                "repo",
                "add",
                name,
                url
            ],
            CommandTimeout,
            cancellationToken,
            home);

    public static Task<HelmCliResult> RepoIndexAsync(
        string directory,
        string? url,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "repo",
            "index",
            NormalizeHelmPath(directory)
        };

        if (url is not null)
        {
            arguments.Add("--url");
            arguments.Add(url);
        }

        return RunAsync(arguments, CommandTimeout, cancellationToken);
    }

    public static Task<HelmCliResult> PullAsync(
        string chartRef,
        string destination,
        string? version,
        string? repoUrl,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "pull",
            chartRef,
            "--destination",
            NormalizeHelmPath(destination)
        };

        if (version is not null)
        {
            arguments.Add("--version");
            arguments.Add(version);
        }

        if (repoUrl is not null)
        {
            arguments.Add("--repo");
            arguments.Add(repoUrl);
        }

        return RunAsync(arguments, CommandTimeout, cancellationToken);
    }

    public static Task<HelmCliResult> DependencyListAsync(
        string chartPath,
        CancellationToken cancellationToken)
        => RunAsync(
            [
                "dependency",
                "list",
                NormalizeHelmPath(chartPath)
            ],
            CommandTimeout,
            cancellationToken);

    public static Task<HelmCliResult> DependencyUpdateAsync(
        string chartPath,
        CancellationToken cancellationToken)
        => RunAsync(
            [
                "dependency",
                "update",
                NormalizeHelmPath(chartPath)
            ],
            CommandTimeout,
            cancellationToken);

    public static Task<HelmCliResult> DependencyBuildAsync(
        string chartPath,
        CancellationToken cancellationToken)
        => RunAsync(
            [
                "dependency",
                "build",
                NormalizeHelmPath(chartPath),
                "--skip-refresh"
            ],
            CommandTimeout,
            cancellationToken);

    public static Task<HelmCliResult> DependencyBuildAsync(
        string chartPath,
        HelmCliHome home,
        CancellationToken cancellationToken)
        => RunAsync(
            [
                "dependency",
                "build",
                NormalizeHelmPath(chartPath),
                "--skip-refresh"
            ],
            CommandTimeout,
            cancellationToken,
            home);

    private static async Task<HelmCliResult> RunAsync(
        IReadOnlyCollection<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        HelmCliHome? home = null)
    {
        var ownedHome = home is null ? new HelmCliHome() : null;
        home ??= ownedHome!;

        using var process = new Process();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        process.StartInfo = new ProcessStartInfo
        {
            FileName = "helm",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        process.StartInfo.Environment["HELM_CONFIG_HOME"] = home.ConfigHome;
        process.StartInfo.Environment["HELM_CACHE_HOME"] = home.CacheHome;
        process.StartInfo.Environment["HELM_DATA_HOME"] = home.DataHome;

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Failed to start Helm CLI.");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(linkedSource.Token);
            }
            catch (OperationCanceledException)
            {
                await TerminateProcessAsync(process);

                await Task.WhenAll(stdout, stderr);

                if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw new TimeoutException($"Helm CLI did not exit within {timeout.TotalSeconds:0} seconds.");

                throw;
            }

            var output = await Task.WhenAll(stdout, stderr);

            return new HelmCliResult(
                process.ExitCode,
                NormalizeLineEndings(output[0]),
                NormalizeLineEndings(output[1]));
        }
        finally
        {
            ownedHome?.Dispose();
        }
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between the HasExited check and Kill.
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
    }

    public static string NormalizeLineEndings(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    private static string NormalizeHelmPath(string path)
        => path.Replace('\\', '/');

    public sealed class HelmCliHome : IDisposable
    {
        private readonly string _root;

        internal HelmCliHome()
        {
            _root = Path.Combine(Path.GetTempPath(), "helmsharp-helm-cli", Guid.NewGuid().ToString("N"));
            ConfigHome = Path.Combine(_root, "config");
            CacheHome = Path.Combine(_root, "cache");
            DataHome = Path.Combine(_root, "data");
            Directory.CreateDirectory(ConfigHome);
            Directory.CreateDirectory(CacheHome);
            Directory.CreateDirectory(DataHome);
        }

        public string ConfigHome { get; }

        public string CacheHome { get; }

        public string DataHome { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }
}

internal sealed record HelmCliResult(int ExitCode, string Stdout, string Stderr);
