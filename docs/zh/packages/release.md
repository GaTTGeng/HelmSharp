# HelmSharp.Release

`HelmSharp.Release` 包含 release 模型及其基于 Kubernetes Secret 的存储，生命周期操作会使用它。应用代码通常经由 `HelmSharp.Action` 间接使用。

只有在 `HelmClient` 外部实现或检查 release 持久化时，才直接安装：

```powershell
dotnet add package HelmSharp.Release --version 1.3.1
```

存储 revision 是 deployed、superseded、failed 和保留卸载等生命周期结果的证据。失败 revision 即使没有资源被应用、或只有部分资源被应用，也可能保留完整的尝试清单；因此它不是实际集群状态的记录，也不是让系统重新渲染当前 Chart 的请求。

在让应用代码直接依赖 release 存储前，先看 `HelmSharp.Action` 包指南。成员级细节以[生成的 Release API](../api/generated/release.md)为准。
