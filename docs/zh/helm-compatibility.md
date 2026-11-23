# Helm 兼容性

HelmSharp 不是命令行模拟器。它要解决的是：.NET 应用在进程内渲染 chart、管理 release 时，需要哪些可观测的 Helm 行为。

新的兼容性验证以 Helm CLI v4.2.2 为测试基准，CI 和发布工作流也固定使用该版本。消费者运行时不需要安装 Helm。

## 验证范围

CI 会将聚焦测试用 Chart 和选定公开 Chart 与 `helm template` 进行比较。它们覆盖辅助模板、嵌套 values、`.Files`、capabilities 和格式细节，这些情况很难由手写小 Chart 覆盖。这是对已覆盖行为的回归测试，不是通用兼容性认证；若 Chart 依赖少见 Helm 行为，仍应单独验证。

模板函数的精确范围请参阅维护中的[模板函数兼容性矩阵](template-function-compatibility.md)。其中记录上游版本、每个 Helm 可见函数和缺口的明确处置。

## 兼容性契约

一个行为会被视为已支持，需要满足：

- 可以通过文档化的托管 API 使用；
- 有聚焦的自动化测试覆盖；
- 在 `net8.0`、`net9.0`、`net10.0` 上表现一致；
- 对用户可见的渲染输出、release 状态或失败行为与 Helm 语义一致。

CLI 颜色、进度文案、终端格式和插件执行不是目标，除非它们影响 chart 输出或自动化流程。

## 能力快照

| 区域 | 当前级别 | 对用户意味着什么 |
| --- | --- | --- |
| 从目录和 `.tgz` 加载 Chart | 已支持 | 可以从渲染和打包工具开始使用，并由测试用 Chart 与公开 Chart 测试覆盖。 |
| values 文件和 `--set` 风格覆盖 | 部分支持 | 常见流程可用；部分类型转换边界仍在补齐。 |
| Helm 风格模板渲染 | 已支持 | 常见解析、控制流、命名模板、内置对象、空白裁剪和部分 Sprig 函数有聚焦测试。未实现的函数会产生包含模板路径的渲染诊断。 |
| Chart 打包和仓库 | 部分支持 | 已有可用 API；归档和仓库边界仍需扩展。 |
| 安装、升级、回滚、卸载 | 已支持 | 托管生命周期会持久化 deployed、superseded、failed 以及保留历史的卸载 revision，并支持操作级超时、原子恢复、回滚预留和确定性 hook；不支持的 Helm 选项会在变更前失败。 |
| Kubernetes 提交、删除、等待 | 已支持（选定就绪类型） | 常见资源使用强类型 endpoint，其余资源通过集群发现执行提交/删除；升级使用三方字段归属合并；12 种资源具备与 Helm 对齐的显式就绪谓词，其余类型提交后直接通过。Server-side apply 和 operator 自定义资源的领域就绪仍不支持。 |
| Kubernetes Secrets 发布历史 | 已支持 | 不依赖 Helm CLI 也能保存发布记录。 |
| OCI 注册表 | 计划中 | 注册表认证和 Chart 推拉对齐尚未完成。 |
| Chart 来源证明 | 部分支持 | 已支持 OpenPGP 签名和可信密钥验证；要求与限制见 [Chart 溯源与签名](guide/chart-provenance.md)。 |

### Kubernetes 提交与清理契约

下表描述当前行为。[#258](https://github.com/GaTTGeng/HelmSharp/issues/258) 已由 [PR #270](https://github.com/GaTTGeng/HelmSharp/pull/270) 完成；字段归属行为已可用，但受下述基线和列表限制约束。相关选项和方法见 [Action API](api/generated/action.md) 与 [Kube API](api/generated/kube.md)。

| 范围 | 当前行为与边界 | 测试证据 |
| --- | --- | --- |
| 命名空间与作用域 | 清单显式声明的命名空间优先于默认值；集群级资源不带命名空间。`CreateNamespace` 默认为 true，会创建缺失的目标命名空间。 | [身份与命名空间测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesManifestApplierTests.cs#L481-L577) |
| 发现 | 常见类型使用强类型 endpoint；其他类型通过集群发现及其作用域访问。提交使用声明的 API version，并短暂重试注册延迟；删除可使用同 group 的其他 served version。不支持的 core 类型和直接删除发现耗尽会失败。 | [发现测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesManifestApplierTests.cs#L2739-L2908)、[删除测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesManifestApplierTests.cs#L3090-L3200) |
| 创建与更新 | 提交器创建不存在的对象并替换已有对象。直接 `ApplyAsync` 没有历史 release 基线。升级有先前清单时使用下述三方合并；新资源和卸载后重新安装没有此基线。 | [提交测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesManifestApplierTests.cs#L578-L635)、[合并测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ThreeWayMergeTests.cs)、[生命周期测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L902) |
| 提交模式 | 托管生命周期在变更前拒绝 `HelmExecutionOptions.ServerSideApply = true`；非空请求 `ServerSideApply`、`Force` 和 `TakeOwnership` 也会被拒绝。field manager 只发送给发现路径的创建/替换请求，不发送给强类型资源；它不会启用 server-side apply。 | [生命周期校验测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1497)、[提交测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesManifestApplierTests.cs#L2739) |
| 删除与传播策略 | 直接删除和 release 清理按清单逆序处理。直接删除默认为 `Background`；卸载的 `DeletionPropagation` 还支持 `Foreground` 和 `Orphan`。回滚及 hook 清理使用 background。对象 404 视为成功；其他 API 错误停止删除并报告资源身份。 | [删除测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesManifestApplierTests.cs#L3062-L3358)、[生命周期测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1935) |
| Keep 策略 | 卸载和回滚清理会过滤 `helm.sh/resource-policy: keep`；直接 `DeleteAsync` 不解释该注解。删除 owner 或命名空间时，该注解无法阻止 Kubernetes 级联删除。 | [生命周期测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L671-L767)、[卸载测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1756) |
| Hook 清理 | 支持 `before-hook-creation`（默认）、`hook-succeeded` 和 `hook-failed`；清理使用 background 并等待对象消失。成功 hook 在整批结束后逆序清理；失败或取消会触发有界收尾。策略不会删除 CRD hook。`DisableHooks` 会跳过执行及清理。 | [Hook 测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/HelmHookTests.cs#L389-L669)、[失败测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/HelmHookTests.cs#L760-L947)、[禁用测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1796) |

清理失败处理和生命周期选项详见 [release 工作流](guide/release-workflows.md)；直接调用和发现错误详见 [Kubernetes 操作](guide/kubernetes-operations.md)；可编译的端到端配置见 [配置等待、Job 与清理](examples/kubernetes-wait-and-cleanup.md)。

### Kubernetes 就绪、等待、超时与取消契约

`KubernetesResourceWaiter` 使用与 Helm `ReadyChecker` 对齐的确定性轮询（[#148](https://github.com/GaTTGeng/HelmSharp/issues/148)、[#233](https://github.com/GaTTGeng/HelmSharp/issues/233)、[#234](https://github.com/GaTTGeng/HelmSharp/issues/234) 与 [#259](https://github.com/GaTTGeng/HelmSharp/issues/259)）。[#259](https://github.com/GaTTGeng/HelmSharp/issues/259) 已完成：处于 `CrashLoopBackOff` 的 Pod 会保持等待，而不会立即判为终止失败。

#### 会等待就绪的资源类型

| API 版本与类型 | 就绪条件 | 等待中 / 终止失败边界 | 测试证据 |
| --- | --- | --- | --- |
| `apps/v1` `Deployment` | `spec.paused == true`，或 `observedGeneration == generation`、匹配 `spec.template`（忽略 `pod-template-hash`）的 controller-owned ReplicaSet 已观测其 generation 且 `readyReplicas >= desired - maxUnavailable`（`Recreate` 要求全部副本；`RollingUpdate` 默认 25% surge 向上取整 / 25% unavailable 向下取整，两者皆为 0 时强制 1 个 unavailable）。 | generation 滞后、缺少新 ReplicaSet、就绪副本不足以及 `ProgressDeadlineExceeded` 均保持等待直到超时，不立即终止失败。 | [Deployment 对齐测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L62-L85)、[kind 就绪测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KindLifecycleTests.cs#L318-L355) |
| `apps/v1` `StatefulSet` | `observedGeneration == generation`，且 `updateStrategy.type != "RollingUpdate"`（例如 `OnDelete`），或同时满足 `updatedReplicas >= max(0, desired - partition)`、`readyReplicas == desired` 且（当 `partition == 0` 时）`currentRevision == updateRevision`。 | generation 滞后、分区滚动未完成、副本未就绪或 revision 未对齐时保持等待直到超时。 | [StatefulSet 对齐测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L96-L103) |
| `apps/v1` `DaemonSet` | `observedGeneration == generation`，且 `updateStrategy.type != "RollingUpdate"`，或 `updatedNumberScheduled == desiredNumberScheduled` 且 `numberReady >= desired - maxUnavailable`（`desired > 0` 时默认为 1，百分比向上取整，非法值回退为 `desired`）。 | generation 滞后、更新未全部调度或就绪数不足时保持等待直到超时。 | [DaemonSet 对齐测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L86-L95) |
| `apps/v1` `ReplicaSet` | `observedGeneration == generation` 且所有匹配 `spec.selector` 的 Pod 报告 `Ready=True`。 | generation 滞后或任意匹配 Pod 未就绪时保持等待直到超时。 | [ReplicaSet 对齐测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L104-L109) |
| `v1` `ReplicationController` | `observedGeneration == generation` 且所有匹配 `spec.selector` 的 Pod 报告 `Ready=True`。 | generation 滞后或任意匹配 Pod 未就绪时保持等待直到超时。 | [ReplicationController 对齐测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L110-L115) |
| `v1` `Pod` | 条件 `Ready=True`。 | **终止失败**：`status.phase == "Failed"`。**等待中（[#259](https://github.com/GaTTGeng/HelmSharp/issues/259)）**：`CrashLoopBackOff` 及其他未 Ready 状态会继续轮询直到 `Ready=True` 或超时；超时诊断会保留 `Pod in CrashLoopBackOff`。 | [Pod 对齐测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L116-L119)、[CrashLoopBackOff 测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L114-L144) |
| `batch/v1` `Job` | `WaitForJobs` 为 `false` 时立即视为就绪（不请求 API）。`WaitForJobs` 为 `true` 时，满足 `Complete=True`、`spec.completions` 未设置或 `status.succeeded >= spec.completions` 即就绪。 | **终止失败**（`WaitForJobs` 为 `true` 时）：`status.failed > spec.backoffLimit`（默认 6）或条件 `Failed=True`；其余情况保持等待直到完成数达标。 | [Job 对齐测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L177-L196)、[WaitForJobs 测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L69-L111) |
| `v1` `PersistentVolumeClaim` | `status.phase == "Bound"`。 | **终止失败**：`status.phase == "Lost"`。其他阶段保持等待直到超时。 | [PVC 对齐测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L120-L122) |
| `v1` `Service` | `ExternalName` 立即就绪；其他类型要求非空 `spec.clusterIP`；`LoadBalancer` 还要求存在 `spec.externalIPs` 或 `status.loadBalancer.ingress`。 | 缺少 `clusterIP` 或 LoadBalancer ingress 尚未分配时保持等待直到超时。 | [Service 对齐测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L128-L141)、[等待器 Service 测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L162-L192) |
| `v1` `Endpoints` | 至少一个 subset 包含一个或多个就绪 `addresses`。 | subset 为空或只有 `notReadyAddresses` 时保持等待直到超时。 | [Endpoints 对齐测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L123-L124) |
| `apiextensions.k8s.io/v1` 与 `v1beta1` `CustomResourceDefinition` | 通过清单声明的 API version 读取；`Established=True` 或 `NamesAccepted=False` 即就绪（对齐 Helm，避免名称冲突卡住安装）。 | 其他或缺失状态条件保持等待直到超时。 | [CRD 对齐测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L146-L173)、[beta CRD 测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L195-L251) |
| `autoscaling/v2` `HorizontalPodAutoscaler` | 条件 `ScalingActive=True`。 | **终止失败**：`ScalingActive=False`。缺少状态条件时保持等待。`autoscaling/v1` HPA 不会轮询，提交后直接视为就绪。 | [HPA 对齐测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesReadyCheckerParityTests.cs#L198-L214) |
| 其他所有类型（`ConfigMap`、`Secret`、`ServiceAccount`、`Ingress`、`CronJob`、`autoscaling/v1` HPA、`PodDisruptionBudget`、`EndpointSlice`、RBAC、自定义资源实例、未知类型） | 提交后立即接受为就绪，`WaitForReadyAsync` 不会查询其状态。 | 不会推导 operator 管理的自定义资源的领域就绪状态；需要时应在应用层补充健康检查。 | [非等待类型跳过测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L23-L66) |

#### 等待控制、Hook、超时、取消与清理边界

| 范围 | 当前行为与限制 | 测试证据 |
| --- | --- | --- |
| 等待策略与 API 重试 | `WaitForReadyAsync` 每 3 秒轮询一次；`WaitForDeletedAsync` 和 Pod hook 完成等待每 1 秒轮询一次。Helm 的 legacy status-watcher 策略未实现也未暴露。就绪等待期间遇到 `404` 会重置错误计数并保持等待（`Resource not found yet`）。其他读取错误线性退避（`min(30s, 3s * consecutiveErrors)`），连续超过 10 次失败后抛出 `KubernetesResourceOperationException`；`WaitForDeletedAsync` 遇到非 `404` 且非类型缺失的 API 错误会立即抛出。 | [轮询与重试测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L254-L324)、[删除等待测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L327-L611) |
| Job 与 Pod Hook 等待 | Hook 按 weight，再按 name/kind/path 依次执行。Job hook 始终等待完成（`waitForJobs: true`）。Pod hook 每 1 秒轮询直到 `status.phase == "Succeeded"` 或 `"Failed"`（等待终止完成，而非 `Ready=True`）。其他类型的 hook 提交后不观察完成状态。`DisableHooks = true` 会跳过 hook 执行与清理。 | [Hook Job 与 Pod 测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/HelmHookTests.cs#L520-L791)、[Pod hook 完成测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/HelmHookTests.cs#L948-L974)、[kind hook 测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KindLifecycleTests.cs#L318-L414) |
| 超时控制 | `KubernetesResourceWaiter`、`HelmUpgradeInstallRequest` 和 `HelmRollbackRequest` 的 `TimeoutSeconds` 必须 `> 0`（零或负数抛出 `ArgumentOutOfRangeException`）；省略时使用 `HelmExecutionOptions.TimeoutSeconds`（默认 300 秒）。`WaitForJobs = true` 在安装/升级时要求启用 `Wait` 或 `Atomic`（回滚时要求 `Wait`）。在 `HelmClient` 中，单个操作级超时覆盖提交、hook 和等待，到期后可能抛出 `TimeoutException`（含最后观察状态）或 `OperationCanceledException`（调用方令牌未取消）。`UninstallAsync` 省略 `TimeoutSeconds` 时不创建操作级超时令牌，但 hook 和 `Wait = true` 仍使用 `HelmExecutionOptions.TimeoutSeconds`。`TestAsync` 超时会将卡住的 hook 记为 `FAILED` 并停止后续 hook。 | [超时校验与诊断测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L13-L20)、[回滚超时测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1083-L1137)、[测试 hook 超时测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1652-L1693)、[kind 等待超时测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KindLifecycleTests.cs#L472-L509) |
| 调用方取消 | 调用方 `CancellationToken` 会传入每次 Kubernetes 读取与轮询等待，并作为 `OperationCanceledException` 传播，绝不会被当作就绪成功。安装/升级与回滚在最终持久化前取消会记录 `failed` revision（安装/升级的 `Atomic`/`CleanupOnFail` 恢复会使用 `CancellationToken.None` 执行）。回滚一旦完成资源提交，最终 `deployed` revision 的写入会独立于操作令牌完成，避免历史中同时保留两个 `deployed` revision。 | [等待器取消测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L280-L295)、[删除等待取消测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L596-L611)、[回滚取消测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L1195-L1317) |
| Hook 与删除清理限制 | Hook 失败或取消后的终结清理在整个批次内共享一个被限制在 2–10 秒的有界窗口，遇到单个删除错误会继续清理其余 hook，并把清理异常附加到 `Exception.Data["HelmSharp.HookCleanupError"]`，不覆盖原始失败。删除策略绝不会删除 CRD hook。`WaitForDeletedAsync` 将 `404`、已移除的发现 CRD/API 以及不支持的 core 类型视为不存在；`helm.sh/resource-policy: keep` 无法阻止 Kubernetes 因命名空间或 owner 被删除而触发的级联删除。 | [Hook 终结清理测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/HelmHookTests.cs#L793-L946)、[卸载等待测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/ChartOperationsTests.cs#L2883-L2932)、[已移除 API 删除等待测试](https://github.com/GaTTGeng/HelmSharp/blob/master/tests/HelmSharp.Tests/KubernetesResourceWaiterTests.cs#L467-L547) |

### 升级时的资源字段归属

对于上一已部署 release 清单中已有的资源，`HelmClient` 升级会比较该 revision 保存的清单、新渲染的 Chart 清单和 Kubernetes 当前对象。只存在于 live 对象中的字段、live 侧的删除，以及 Chart 未修改字段的 live 改动会保留。Chart 删除的字段会保持缺省，即使 live 对象曾修改该字段也会删除。已知 Kubernetes list-map 路径和 CRD schema 声明的 list-map 键会逐项合并；其他列表视为原子值并遵循 Chart。`resourceVersion`、`uid` 和 `status` 等 Kubernetes 管理字段仍以集群为准。没有先前清单条目的资源、卸载后重新安装，以及直接调用 `KubernetesManifestApplier.ApplyAsync` 时，替换会使用期望清单，不执行三方归属合并。该行为由实际 install/upgrade 生命周期路径覆盖；详见合并和生命周期专项测试。

## 已知边界

如果生产流程依赖以下行为，接入前应先验证：

- 少见 values 类型转换和 list 语法；
- OCI 认证和注册表流程；
- 已文档化签名和校验流程之外的来源证明互操作；
- 实现范围之外的 Sprig 函数或 Go-template 行为；
- 少见 Kubernetes 资源类型的就绪判断；
- Helm plugin 的安全替代模型。

基准输出测试只证明其实际覆盖的行为。新的 Chart 仍可能暴露未实现函数、解析边界或清单差异，因此兼容性报告应包含最小 Chart 和等价 Helm CLI 命令。

## 报告兼容性差异

请提交包含以下信息的兼容性 issue：

- Helm CLI 和 HelmSharp 版本；
- 最小 Chart 和 values 输入；
- 精确 Helm 命令和输出；
- 等价 HelmSharp API 调用和输出；
- 差异是否影响渲染、发布状态或集群变更。

小而可复现的 Chart 比截图或大型私有 Chart 更有价值。

## 持续验证

基准输出测试结果会在每次推送和拉取请求中通过 [CI 工作流](https://github.com/GaTTGeng/HelmSharp/blob/master/.github/workflows/ci.yml) 验证。CI 运行器会在 .NET SDK 旁安装 Helm CLI（`v4.2.2`），并执行完整的基准输出测试套件，包括测试用 Chart 和公开 Chart 对比。JSON 报告会作为每次工作流运行的产物发布。

Kubernetes apply、wait、hook、rollback、uninstall 与 release Secret 行为还会在一次性 kind 集群上验证。覆盖范围、触发方式与失败诊断见 [Kind 生命周期测试通道](testing-kind-lifecycle.md)。
