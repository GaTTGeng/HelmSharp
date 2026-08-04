# ASP.NET Core Chart 预览

当 HTTP API 必须返回渲染后的 YAML、但不能创建 release 或访问集群时，使用此模式。

## 安装包

安装 `HelmSharp.Chart` 和 `HelmSharp.Engine`。接口通过应用自有的目录解析 Chart 和 values 标识，然后创建 `HelmTemplateRenderer`。

## 请求流程

1. 验证调用方身份及其 Chart 和 values 标识。
2. 将标识解析为应用授权的内容。
3. 加载 Chart、合并 values，并使用明确的 Kubernetes capabilities 输入渲染。
4. 返回 YAML；需要时在独立响应字段中返回 notes。
5. 如果之后可以部署该预览，记录 Chart 版本和生效输入。

完整代码见[构建渲染预览接口](../examples/render-preview-api.md)。

## 不要跨越边界

不要接受调用方提供的文件路径，不要创建 `HelmClient` 生命周期请求，也不要在这个接口中提交渲染出的清单。审批到部署的工作流需要不可变输入和 release 状态控制；此场景请使用[把评审结果变成部署](../examples/dry-run-deployment.md)。
