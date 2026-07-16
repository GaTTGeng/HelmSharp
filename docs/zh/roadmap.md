# 路线图

路线图围绕用户真正需要信任的事情组织：同一个 Chart 能否渲染，values 规则能否解释清楚，Kubernetes 变更是否只在应用明确允许时发生。

具体议题范围以 GitHub milestones 为准。

## 当前重点

**M1：Helm 模板对齐** 已在 1.1.0 完成，**M2：Chart 打包和仓库对齐** 正在为计划中的 1.2.0 版本做最终审查。发布工作流、Kubernetes 操作、OCI/来源证明，以及来自真实用户 Chart 的兼容性扩展也在继续推进。

最新发布版本是 1.1.1。在 1.2.0 发布前，M2 API 文档和示例描述的是当前 `master` 分支，而不是 1.1.1 NuGet 包中可用的 API。

当前渲染覆盖范围见 [兼容性页面](helm-compatibility.md)，M2 工作流见 [Chart 分发指南](guide/chart-distribution.md)；发布生命周期、Kubernetes 语义、OCI 和公共 SDK 加固可继续跟踪 GitHub 上的后续 milestones。

## 交付原则

- 消费者应用运行时保持托管 .NET 行为，不 shell out 到 Helm。
- Helm CLI 输出作为测试基准，而不是 SDK 依赖。
- 先覆盖常见生产 Chart 行为，再处理少见终端格式细节。
- 包边界清晰，让应用只依赖自己需要的层。
- Kubernetes 变更、凭据、OCI、来源证明都按安全敏感工作处理。

## 里程碑计划

| 阶段 | 状态 | 用户结果 |
| --- | --- | --- |
| [M1：Helm 模板对齐](https://github.com/GaTTGeng/HelmSharp/milestone/1) | 1.1.0 已完成 | 从 .NET 渲染常见 Chart，输出具备可预期的 Helm 兼容性。 |
| [M2：Chart 打包和仓库对齐](https://github.com/GaTTGeng/HelmSharp/milestone/2) | 1.2.0 最终审查中 | 不交给 CLI 也能打包、生成索引、拉取和处理依赖。 |
| [M3：发布生命周期对齐](https://github.com/GaTTGeng/HelmSharp/milestone/6) | 计划中 | 安装、升级、回滚、卸载、状态、历史和 hooks 行为可解释。 |
| [M4：Kubernetes 提交和等待语义](https://github.com/GaTTGeng/HelmSharp/milestone/5) | 计划中 | 资源身份、命名空间、就绪、Jobs、删除和 hook 清理更完整。 |
| [M5：OCI 和来源证明](https://github.com/GaTTGeng/HelmSharp/milestone/4) | 计划中 | 注册表认证、Chart 拉取/推送、签名和校验。 |
| [M6：公共 SDK 加固](https://github.com/GaTTGeng/HelmSharp/milestone/3) | 进行中 | 文档、示例、nullable 正确性、包质量和 API 细节更稳。 |
| [M7：兼容性扩展研究](https://github.com/GaTTGeng/HelmSharp/milestone/7) | 研究中 | 基于证据决定 `netstandard`、.NET Framework 和长期目标框架支持。 |

## 什么算完成

一个兼容性事项准备好离开里程碑时，需要满足：

1. 行为被聚焦的单元测试、集成测试或基准输出测试捕获。
2. 兼容性页面说明已支持内容和剩余边界。
3. 公共 API 变化包含从用户视角出发的示例。
4. `net8.0`、`net9.0`、`net10.0` 的 Release 构建和测试通过。

## 参与贡献

优先从已有里程碑 issue 开始。新发现 Helm 差异时，请提交最小 Chart、Helm 命令、HelmSharp API 调用和两边输出。
