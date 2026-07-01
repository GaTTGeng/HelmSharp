# 公开 Chart 渲染

## 你在解决什么问题

公开 Chart 很适合作为集成检查，因为它们会触达辅助模板、嵌套 values、capabilities、CRDs 和格式细节，这些行为在手写小样例里经常缺失。需要预览固定版本的公开 Chart 并检查生成清单时，可以使用这个工作流。

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

`HelmTemplateRequest` 对应 Helm 用户熟悉的预览形态：发布、namespace、Chart、values、Kubernetes 版本、API 版本、CRDs 和 NOTES。

## 生产环境注意事项

- 渲染公开 Chart 时固定 Chart 版本。
- 让 Chart 副本或来源证明与预览输出关联。
- 当前公开 Chart 测试覆盖和已知边界见兼容性页面。

## 下一步

查看 [Helm 兼容性](../helm-compatibility.md)。
