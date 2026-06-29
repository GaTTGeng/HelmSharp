using System.Text.Json;

namespace HelmSharp.Action;

/// <summary>
/// Manages Helm plugins — install, list, uninstall, and run.
/// Plugins are stored in ~/.helmsharp/plugins/.
/// </summary>
public class HelmPluginManager
{
    private readonly string _pluginsDir;

    public HelmPluginManager(string? pluginsDir = null)
    {
        _pluginsDir = pluginsDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".helmsharp", "plugins");
        Directory.CreateDirectory(_pluginsDir);
    }

    /// <summary>
    /// Installs a plugin from a URL or local directory.
    /// </summary>
    public async Task<string> InstallAsync(string name, string source, CancellationToken ct = default)
    {
        var pluginDir = Path.Combine(_pluginsDir, name);
        if (Directory.Exists(pluginDir))
            throw new InvalidOperationException($"Plugin '{name}' is already installed");

        Directory.CreateDirectory(pluginDir);

        if (Directory.Exists(source))
        {
            // Copy from local directory
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var dest = Path.Combine(pluginDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
        }
        else if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            // Download from URL
            using var http = new HttpClient();
            var response = await http.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();

            if (uri.AbsolutePath.EndsWith(".tgz") || uri.AbsolutePath.EndsWith(".tar.gz"))
            {
                var tempFile = Path.GetTempFileName();
                await File.WriteAllBytesAsync(tempFile, await response.Content.ReadAsByteArrayAsync(ct), ct);
                System.IO.Compression.ZipFile.ExtractToDirectory(tempFile, pluginDir, true);
                File.Delete(tempFile);
            }
            else
            {
                // Assume it's a script
                var content = await response.Content.ReadAsStringAsync(ct);
                await File.WriteAllTextAsync(Path.Combine(pluginDir, "plugin.sh"), content, ct);
            }
        }

        // Create plugin metadata
        var metadata = new
        {
            name,
            version = "1.0.0",
            installedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        await File.WriteAllTextAsync(
            Path.Combine(pluginDir, "plugin.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        return pluginDir;
    }

    /// <summary>
    /// Lists installed plugins.
    /// </summary>
    public List<HelmPluginInfo> List()
    {
        var plugins = new List<HelmPluginInfo>();
        if (!Directory.Exists(_pluginsDir))
            return plugins;

        foreach (var dir in Directory.GetDirectories(_pluginsDir))
        {
            var name = Path.GetFileName(dir);
            var metadataFile = Path.Combine(dir, "plugin.json");
            var version = "unknown";
            var description = "";

            if (File.Exists(metadataFile))
            {
                try
                {
                    var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(metadataFile));
                    if (doc.TryGetProperty("version", out var v)) version = v.GetString() ?? "unknown";
                    if (doc.TryGetProperty("description", out var d)) description = d.GetString() ?? "";
                }
                catch { }
            }

            plugins.Add(new HelmPluginInfo
            {
                Name = name,
                Version = version,
                Description = description,
                Path = dir
            });
        }

        return plugins;
    }

    /// <summary>
    /// Uninstalls a plugin.
    /// </summary>
    public void Uninstall(string name)
    {
        var pluginDir = Path.Combine(_pluginsDir, name);
        if (!Directory.Exists(pluginDir))
            throw new InvalidOperationException($"Plugin '{name}' is not installed");

        Directory.Delete(pluginDir, recursive: true);
    }

    /// <summary>
    /// Runs a plugin command.
    /// </summary>
    public async Task<CommandResult> RunAsync(string name, string[] args, CancellationToken ct = default)
    {
        var pluginDir = Path.Combine(_pluginsDir, name);
        if (!Directory.Exists(pluginDir))
            return new CommandResult { ExitCode = 1, StandardError = $"Plugin '{name}' is not installed" };

        // Find the plugin executable
        var exe = FindPluginExecutable(pluginDir);
        if (exe is null)
            return new CommandResult { ExitCode = 1, StandardError = $"No executable found for plugin '{name}'" };

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = pluginDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
            return new CommandResult { ExitCode = 1, StandardError = "Failed to start plugin process" };

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout,
            StandardError = stderr
        };
    }

    private static string? FindPluginExecutable(string pluginDir)
    {
        // Check for common executable patterns
        var candidates = new[] { "plugin.sh", "plugin.py", "plugin.rb", "main.sh", "main.py", "run.sh", "run" };
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(pluginDir, candidate);
            if (File.Exists(path))
                return path;
        }

        // Check for any executable file
        foreach (var file in Directory.GetFiles(pluginDir))
        {
            if (Path.GetFileName(file).StartsWith("plugin.") || Path.GetFileName(file).StartsWith("run."))
                return file;
        }

        return null;
    }
}

public class HelmPluginInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
