# HelmSharp.Storage

`HelmSharp.Storage` 定义 `IHelmReleaseStore` 扩展契约。它面向需要替换或包装 release 持久化的产品；普通应用应使用 `HelmSharp.Action` 及其内置 release store。

```powershell
dotnet add package HelmSharp.Storage --version 1.3.1
```

实现接口时，必须保留 revision 和产生它的 Chart 之间的区别。存储层应能检查历史生命周期状态，而不能悄悄改用新版本 Chart 重新渲染。

请看[生成的 Storage API](../api/generated/storage.md)和[发布工作流](../guide/release-workflows.md)。
