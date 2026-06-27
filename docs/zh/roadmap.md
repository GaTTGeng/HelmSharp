# 路线图

路线图围绕用户真正需要信任的事情组织：同一个 chart 能否渲染，values 规则能否解释清楚，Kubernetes 变更是否只在应用明确允许时发生。

具体 issue 范围以 GitHub milestones 为准。

## 当前重点

**M1: Helm Template Parity** 是当前活跃工作流。项目已经有真实 chart golden tests，接下来继续缩小 values、built-in objects、template functions、whitespace、`.Files`、capabilities 和 subcharts 上的差异。

可以跟踪 [GitHub M1](https://github.com/GaTTGeng/HelmSharp/milestone/1)，或查看 [兼容性页面](helm-compatibility.md)。

## 交付原则

- 消费者应用运行时保持托管 .NET 行为，不 shell out 到 Helm。
- Helm CLI 输出作为测试基准，而不是 SDK 依赖。
- 先覆盖常见生产 chart 行为，再处理少见终端格式细节。
- 包边界清晰，让应用只依赖自己需要的层。
- Kubernetes 变更、凭据、OCI、provenance 都按安全敏感工作处理。

## Milestone 计划

| Phase | Status | 用户结果 |
| --- | --- | --- |
| [M1: Helm Template Parity](https://github.com/GaTTGeng/HelmSharp/milestone/1) | Active | 从 .NET 渲染常见真实 chart，输出具备可预期的 Helm 兼容性。 |
| [M2: Chart Packaging and Repository Parity](https://github.com/GaTTGeng/HelmSharp/milestone/2) | Planned | 不交给 CLI 也能 package、index、pull 和处理 dependencies。 |
| [M3: Release Lifecycle Parity](https://github.com/GaTTGeng/HelmSharp/milestone/6) | Planned | install、upgrade、rollback、uninstall、status、history 和 hooks 行为可解释。 |
| [M4: Kubernetes Apply and Wait Semantics](https://github.com/GaTTGeng/HelmSharp/milestone/5) | Planned | 资源身份、namespace、readiness、Jobs、删除和 hook cleanup 更完整。 |
| [M5: OCI and Provenance](https://github.com/GaTTGeng/HelmSharp/milestone/4) | Planned | registry 认证、chart pull/push、签名和校验。 |
| [M6: Public SDK Hardening](https://github.com/GaTTGeng/HelmSharp/milestone/3) | Ongoing | 文档、示例、nullable 正确性、包质量和 API 细节更稳。 |
| [M7: Compatibility Expansion Research](https://github.com/GaTTGeng/HelmSharp/milestone/7) | Research | 基于证据决定 `netstandard`、.NET Framework 和长期目标框架支持。 |

## 什么算完成

一个兼容性事项准备好离开 milestone 时，需要满足：

1. 行为被聚焦 unit、integration 或 golden test 捕获。
2. 兼容性页面说明已支持内容和剩余边界。
3. 公共 API 变化包含从用户视角出发的示例。
4. `net8.0`、`net9.0`、`net10.0` 的 Release 构建和测试通过。

## 参与贡献

优先从已有 milestone issue 开始。新发现 Helm 差异时，请提交最小 chart、Helm 命令、HelmSharp API 调用和两边输出。
