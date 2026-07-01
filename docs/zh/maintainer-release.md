# 维护者发布指南

本仓库通过 GitHub Actions 的 NuGet 可信发布（Trusted Publishing）发布 NuGet 包。正常发布不需要、也不应在 GitHub secrets 中创建长期 NuGet API key。

## 一次性 NuGet.org 设置

在 NuGet.org 为本仓库创建可信发布策略（trusted publishing policy）：

- 包所有者（Package Owner）：拥有 HelmSharp 包的 NuGet.org 账号。
- 仓库所有者（Repository Owner）：`GaTTGeng`
- 仓库（Repository）：`HelmSharp`
- 工作流文件（Workflow File）：`release-nuget.yml`
- 环境（Environment）：除非后续工作流改为使用 GitHub environment，否则留空。

工作流使用 `NuGet/login@v1`，因此 `.github/workflows/release-nuget.yml` 必须保留 `id-token: write` 权限。

## GitHub 设置

如果 NuGet.org 用户名不同于 GitHub 仓库所有者，创建名为 `NUGET_USER` 的仓库变量，值为 NuGet.org 用户名。如果未设置，工作流会使用 GitHub 仓库所有者。

不需要 `NUGET_API_KEY` 仓库 secret。

分支保护应要求 `CI / Build, test, and pack` 状态检查通过后才能合并到 `master`，并要求所有对话已解决。

Actions 加固方面，优先只允许 GitHub 自有和 Marketplace 已验证的 actions。如果需要更严格的供应链控制，将第三方 actions 固定到完整提交 SHA，并在仓库或组织级别启用 SHA 固定。

## 发布版本

使用不带前缀 `v` 的 NuGet SemVer tag：

```powershell
git tag 1.0.1
git push origin 1.0.1
```

发布工作流会拒绝带 `v` 前缀的 tag。发布 tag 必须指向一个可从 `origin/master` 到达的提交。

如需手动发布，打开 `Release NuGet` 工作流，输入包版本并启用发布。手动预发布包版本（例如 `1.0.1-preview.1`）受支持；工作流会从数字版本核心派生稳定的 `AssemblyVersion` 和 `FileVersion`。

## 重新运行失败的发布

修复工作流配置后，可以从 GitHub Actions 页面重新运行失败的工作流，或使用：

```powershell
gh run rerun <run-id> --repo GaTTGeng/HelmSharp
```

对于可信发布配置完成前创建并失败的 `1.0.1` 发布，把 `1.0.1` tag 更新到包含可信发布工作流的提交后，会触发新的发布运行。
