using HelmSharp.Action;

namespace HelmSharp.Tests;

/// <summary>Verifies that an unsupported OCI push cannot report remote publication.</summary>
public class OciPushTests
{
    private sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
    {
        public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new HelmExecutionOptions());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PushAsync_WithoutTransport_FailsForArchiveAndDirectory(bool directory)
    {
        var root = Path.Combine(Path.GetTempPath(), "helmsharp-push-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var name = "push-" + Guid.NewGuid().ToString("N");
        var packaged = Path.Combine(Environment.CurrentDirectory, name + "-0.1.0.tgz");
        try
        {
            var client = new HelmClient(new StaticHelmOptionsProvider());
            await client.CreateAsync(name, root);
            var chart = Path.Combine(root, name);
            if (!directory)
            {
                chart = Path.Combine(root, "sample.tgz");
                await File.WriteAllTextAsync(chart, "archive");
            }

            var result = await client.PushAsync(chart, "oci://registry.example:5000/team");

            Assert.False(result.Succeeded);
            Assert.Empty(result.StandardOutput);
            Assert.Contains("not supported", result.StandardError);
            Assert.False(File.Exists(packaged));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(packaged);
        }
    }

    [Theory]
    [InlineData("https://registry.example/team")]
    [InlineData("oci://user:secret@registry.example/team")]
    [InlineData("oci://registry.example/team:tag")]
    [InlineData("oci://registry.example/team?token=secret")]
    [InlineData("oci://registry.example/Bad_Path")]
    public async Task PushAsync_InvalidDestination_ReturnsRedactedFailure(string remote)
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        var result = await client.PushAsync("missing.tgz", remote);

        Assert.False(result.Succeeded);
        Assert.Contains("Invalid OCI", result.StandardError);
        Assert.DoesNotContain("secret", result.StandardError);
    }
}
