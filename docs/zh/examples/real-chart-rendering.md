# 真实 Chart 渲染

## 你在解决什么问题

真实公开 Chart 会覆盖手写小样例很少触达的模板行为。HelmSharp 1.1.0 验证了 `podinfo`、`metrics-server`、`external-dns`、`ingress-nginx` 和 `cert-manager`，129/129 个模板通过。

## 安装哪些包

```powershell
dotnet add package HelmSharp.Action --version 1.1.0
```

## 完整最小代码

```csharp
var result = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "ingress-nginx",
    Namespace = "ingress-system",
    Chart = "/charts/ingress-nginx",
    ValuesFiles = ["ci/controller-deployment-values.yaml"],
    KubeVersion = "1.30.0",
    ApiVersions =
    [
        "networking.k8s.io/v1",
        "policy/v1",
        "monitoring.coreos.com/v1"
    ],
    IncludeCRDs = true,
    ShowNotes = true
}, cancellationToken);

Console.WriteLine(result.StandardOutput);
```

## 关键 API 为什么这样用

`HelmTemplateRequest` 对应 Helm 用户熟悉的预览形态：release、namespace、chart、values、kube version、API versions、CRDs 和 notes。

## 生产环境注意事项

- 渲染公开 Chart 时固定 Chart 版本。
- 让 Chart 副本或 provenance 与预览输出关联。
- 当前真实 Chart 兼容性数据见兼容性页面。

## 下一步

查看 [Helm 兼容性](../helm-compatibility.md)。
