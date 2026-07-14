namespace HelmSharp.Action;

/// <summary>
/// Helm 基础操作封装。当前实现为托管子集，不要求运行环境中存在 helm 可执行文件。
/// </summary>
public interface IHelmClient
{
    // ─── Release Lifecycle ───
    Task<CommandResult> VersionAsync(CancellationToken cancellationToken = default);

    Task<CommandResult> ListReleasesAsync(
        string? @namespace = null,
        bool allNamespaces = false,
        string? selector = null,
        int? limit = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> UpgradeInstallAsync(
        HelmUpgradeInstallRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> UpgradeInstallStreamAsync(
        HelmUpgradeInstallRequest request,
        CancellationToken cancellationToken = default);

    Task<CommandResult> UninstallAsync(
        string releaseName,
        string? @namespace = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> UninstallAsync(
        HelmUninstallRequest request,
        CancellationToken cancellationToken = default);

    Task<CommandResult> StatusAsync(
        string releaseName,
        string? @namespace = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> RollbackAsync(
        string releaseName,
        int revision,
        string? @namespace = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> HistoryAsync(
        string releaseName,
        string? @namespace = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> GetValuesAsync(
        string releaseName,
        string? @namespace = null,
        bool allValues = false,
        CancellationToken cancellationToken = default);

    // ─── Get Release Info ───
    Task<CommandResult> GetManifestAsync(
        string releaseName,
        string? @namespace = null,
        int revision = 0,
        CancellationToken cancellationToken = default);

    Task<CommandResult> GetNotesAsync(
        string releaseName,
        string? @namespace = null,
        int revision = 0,
        CancellationToken cancellationToken = default);

    Task<CommandResult> GetHooksAsync(
        string releaseName,
        string? @namespace = null,
        int revision = 0,
        CancellationToken cancellationToken = default);

    Task<CommandResult> GetAllAsync(
        string releaseName,
        string? @namespace = null,
        int revision = 0,
        CancellationToken cancellationToken = default);

    // ─── Test ───
    Task<CommandResult> TestAsync(
        string releaseName,
        string? @namespace = null,
        int? timeoutSeconds = null,
        bool showLogs = false,
        CancellationToken cancellationToken = default);

    // ─── Template / Rendering ───
    Task<CommandResult> TemplateAsync(
        HelmTemplateRequest request,
        CancellationToken cancellationToken = default);

    Task<CommandResult> TemplateWithNotesAsync(
        HelmTemplateRequest request,
        CancellationToken cancellationToken = default);

    Task<CommandResult> DiffAsync(
        string releaseName,
        HelmUpgradeInstallRequest request,
        CancellationToken cancellationToken = default);

    // ─── Chart Operations ───
    Task<CommandResult> LintAsync(
        string chartPath,
        string? valuesContent = null,
        Dictionary<string, string>? setValues = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> ShowManifestAsync(
        string chartPath,
        string? version = null,
        string? valuesContent = null,
        Dictionary<string, string>? setValues = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> ShowChartAsync(
        string chartPath,
        CancellationToken cancellationToken = default);

    Task<CommandResult> ShowValuesAsync(
        string chartPath,
        CancellationToken cancellationToken = default);

    Task<CommandResult> ShowReadmeAsync(
        string chartPath,
        CancellationToken cancellationToken = default);

    Task<CommandResult> ShowCrdsAsync(
        string chartPath,
        CancellationToken cancellationToken = default);

    Task<CommandResult> PullAsync(
        string chartRef,
        string? version = null,
        string? destination = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> PackageAsync(
        string chartPath,
        string? destination = null,
        string? version = null,
        string? appVersion = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> CreateAsync(
        string chartName,
        string? destination = null,
        string? starter = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> DependencyUpdateAsync(
        string chartPath,
        CancellationToken cancellationToken = default);

    Task<CommandResult> PushAsync(
        string chartRef,
        string remote,
        CancellationToken cancellationToken = default);

    // ─── Repository Operations ───
    Task<CommandResult> RepoAddAsync(
        string name,
        string url,
        string? username = null,
        string? password = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> RepoRemoveAsync(
        string name,
        CancellationToken cancellationToken = default);

    Task<CommandResult> RepoListAsync(
        CancellationToken cancellationToken = default);

    Task<CommandResult> SearchRepoAsync(
        string keyword,
        string? repoUrl = null,
        CancellationToken cancellationToken = default);

    // ─── Registry Operations ───
    Task<CommandResult> RegistryLoginAsync(
        string host,
        string username,
        string password,
        CancellationToken cancellationToken = default);

    Task<CommandResult> RegistryLogoutAsync(
        string host,
        CancellationToken cancellationToken = default);

    // ─── Repo Index / Update ───
    Task<CommandResult> RepoIndexAsync(
        string dirPath,
        string? url = null,
        CancellationToken cancellationToken = default,
        string? mergeIndexPath = null);

    Task<CommandResult> RepoUpdateAsync(
        CancellationToken cancellationToken = default);

    // ─── Search Hub ───
    Task<CommandResult> SearchHubAsync(
        string keyword,
        CancellationToken cancellationToken = default);

    // ─── Show All ───
    Task<CommandResult> ShowAllAsync(
        string chartPath,
        CancellationToken cancellationToken = default);

    // ─── Env ───
    Task<CommandResult> EnvAsync(
        CancellationToken cancellationToken = default);

    // ─── Dependency Build/List ───
    Task<CommandResult> DependencyBuildAsync(
        string chartPath,
        CancellationToken cancellationToken = default);

    Task<CommandResult> DependencyListAsync(
        string chartPath,
        CancellationToken cancellationToken = default);
}
