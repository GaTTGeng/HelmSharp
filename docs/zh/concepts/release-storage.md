# Release 存储

`HelmSharp.Action` 通过 `HelmSharp.Release` 保存生命周期 revision。默认存储使用 Kubernetes Secret，因此操作完成后可以按 release 名称和 revision 检查结果。

## Revision 表示什么

Revision 记录生命周期证据：release 名称和命名空间、Chart 与 values 数据、渲染后的清单、revision 编号、状态和操作元数据。它不是集群实时状态清单。

失败 revision 可能包含尝试提交的完整清单，即使 Kubernetes 没有提交任何资源或只提交了部分资源。应将它作为操作证据，而不是查询集群的替代品。

## 生命周期行为

| 操作 | 保存的结果 |
| --- | --- |
| 安装或升级 | 创建下一个 revision 并记录最终状态。 |
| 回滚 | 创建一个表示回滚结果的新 revision。 |
| `KeepHistory = true` 的卸载 | 保留 `uninstalled` revision。 |
| 默认卸载 | 删除资源后清除 release 历史。 |

使用 `StatusAsync`、`HistoryAsync`、`GetManifestAsync` 和 `GetValuesAsync` 检查保存的 revision。这些 API 读取已记录的数据，不会重新渲染当前 Chart。

## 与 Helm 共存

基于 Secret 的历史遵循 Helm v3 release 存储约定。与其他工具共享同一个 release 名称前，请依据[兼容性约定](../helm-compatibility.md)验证 Chart 和工作流所需的行为。不要因为读取到 revision 就假定当前集群对象仍与其中清单一致。

高层工作流见[安装和升级 Release](../guide/release-workflows.md)。
