# 多租户 Options

## 你在解决什么问题

SaaS 和内部平台通常需要按租户提供 namespace、kubeconfig、API version 和 timeout 默认值。`IHelmOptionsProvider` 把这些决策集中到 request 之外。

## 安装哪些包

```powershell
dotnet add package HelmSharp.Action --version 1.1.0
```

## 完整最小代码

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#options-provider{csharp}

## 关键 API 为什么这样用

Request 描述一次操作。`HelmExecutionOptions` 描述执行这次操作的环境策略：kubeconfig、默认 namespace、field manager、timeout、目标 Kubernetes 版本和 API versions。

## 生产环境注意事项

- 构造 client 或 provider 前先解析租户身份。
- 不要允许请求体指定任意 kubeconfig 路径。
- 租户 id、部署 id 等审计字段应写入产品自己的日志。

## 下一步

阅读 [错误处理](../guide/error-handling.md)，保证租户级失败可诊断。
