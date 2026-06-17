using System.Diagnostics;
using System.Text;

namespace HelmSharp.Tests;

internal static class HelmCliRunner
{
    public static bool IsAvailable()
    {
        try
        {
            var result = RunAsync(["version", "--short"], CancellationToken.None).GetAwaiter().GetResult();
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
            ["template", releaseName, chartPath, "--namespace", releaseNamespace],
            cancellationToken);

    private static async Task<HelmCliResult> RunAsync(
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
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

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }

        return new HelmCliResult(
            process.ExitCode,
            NormalizeLineEndings(await stdout),
            NormalizeLineEndings(await stderr));
    }

    public static string NormalizeLineEndings(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
}

internal sealed record HelmCliResult(int ExitCode, string Stdout, string Stderr);
