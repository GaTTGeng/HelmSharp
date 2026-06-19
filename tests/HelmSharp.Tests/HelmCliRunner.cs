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
                chartPath,
                "--namespace",
                releaseNamespace,
                "--kube-version",
                "v1.29.0"
            ],
            CommandTimeout,
            cancellationToken);

    private static async Task<HelmCliResult> RunAsync(
        IReadOnlyCollection<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
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
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

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
}

internal sealed record HelmCliResult(int ExitCode, string Stdout, string Stderr);
