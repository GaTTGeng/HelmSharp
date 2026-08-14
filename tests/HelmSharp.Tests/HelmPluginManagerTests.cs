using HelmSharp.Action;

namespace HelmSharp.Tests;

public sealed class HelmPluginManagerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "helmsharp-plugin-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("hidden.")]
    [InlineData("-plugin")]
    [InlineData("plugin-")]
    [InlineData("nested/plugin")]
    [InlineData("nested\\plugin")]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("../plugins-evil")]
    [InlineData("..\\plugins-evil")]
    [InlineData("nested/..\\outside")]
    [InlineData("C:\\outside")]
    [InlineData("/tmp/outside")]
    [InlineData("\\\\server\\share")]
    [InlineData("plugin:name")]
    [InlineData("plugin name")]
    [InlineData("插件")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("Com1")]
    [InlineData("lPt9.log")]
    public async Task PluginOperations_RejectNonPortableNamesWithoutAccessingOutsideRoot(string name)
    {
        var pluginRoot = Path.Combine(_tempDir, "plugins");
        var source = Path.Combine(_tempDir, "source");
        var outside = Path.Combine(_tempDir, "plugins-evil");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "marker.txt");
        await File.WriteAllTextAsync(marker, "keep");
        var manager = new HelmPluginManager(pluginRoot);

        var installException = await Assert.ThrowsAsync<ArgumentException>(
            () => manager.InstallAsync(name, source));
        var uninstallException = Assert.Throws<ArgumentException>(() => manager.Uninstall(name));
        var runException = await Assert.ThrowsAsync<ArgumentException>(
            () => manager.RunAsync(name, []));

        Assert.Equal("name", installException.ParamName);
        Assert.Equal("name", uninstallException.ParamName);
        Assert.Equal("name", runException.ParamName);
        Assert.True(File.Exists(marker));
    }

    [Theory]
    [InlineData("diff")]
    [InlineData("helm-secrets")]
    [InlineData("plugin_name")]
    [InlineData("Plugin.Name-2")]
    public async Task InstallAsync_AcceptsPortablePluginNames(string name)
    {
        var pluginRoot = Path.Combine(_tempDir, "plugins", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(_tempDir, "source", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "plugin.sh"), "echo ok");
        var manager = new HelmPluginManager(pluginRoot);

        var installedPath = await manager.InstallAsync(name, source);

        Assert.Equal(Path.Combine(Path.GetFullPath(pluginRoot), name), installedPath);
        Assert.True(File.Exists(Path.Combine(installedPath, "plugin.sh")));
        Assert.True(File.Exists(Path.Combine(installedPath, "plugin.json")));
        Assert.Contains(manager.List(), plugin => plugin.Name == name && plugin.Path == installedPath);

        manager.Uninstall(name);
        Assert.False(Directory.Exists(installedPath));
    }

    [Fact]
    public async Task InstallAsync_UsesPlatformCaseSemantics()
    {
        var pluginRoot = Path.Combine(_tempDir, "plugins");
        var source = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(source);
        var manager = new HelmPluginManager(pluginRoot);
        await manager.InstallAsync("MixedCase", source);

        if (OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.InstallAsync("mixedcase", source));
        }
        else
        {
            var secondPath = await manager.InstallAsync("mixedcase", source);
            Assert.NotEqual(Path.Combine(pluginRoot, "MixedCase"), secondPath);
        }
    }

    [Fact]
    public async Task PluginOperations_RejectLinkedPluginDirectory()
    {
        var pluginRoot = Path.Combine(_tempDir, "plugins");
        var source = Path.Combine(_tempDir, "source");
        var outside = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "marker.txt");
        await File.WriteAllTextAsync(marker, "keep");
        var manager = new HelmPluginManager(pluginRoot);
        var link = Path.Combine(pluginRoot, "linked");

        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.InstallAsync("linked", source));
        Assert.Throws<InvalidOperationException>(() => manager.Uninstall("linked"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.RunAsync("linked", []));
        Assert.DoesNotContain(manager.List(), plugin => plugin.Name == "linked");
        Assert.True(File.Exists(marker));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
