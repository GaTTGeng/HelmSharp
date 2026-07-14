# Multi-tenant Options

## What problem this solves

SaaS and internal platforms often need per-tenant namespace, kubeconfig, API version, and timeout defaults. `IHelmOptionsProvider` centralizes those decisions outside individual requests.

## Packages to install

```powershell
dotnet add package HelmSharp.Action --version 1.1.1
```

## Minimal complete code

<<< @/snippets/HelmSharp.DocsSnippets/Snippets.cs#options-provider{csharp}

## Why these APIs

Requests describe an operation. `HelmExecutionOptions` describes the environment policy used to run that operation: kubeconfig, default namespace, field manager, timeout, target Kubernetes version, and API versions.

## Production notes

- Resolve tenant identity before constructing the client or provider.
- Do not let request bodies choose arbitrary kubeconfig paths.
- Put audit fields such as tenant id and deployment id in your own logs; HelmSharp keeps release labels separate from product audit records.

## Next step

Review [Error Handling](../guide/error-handling.md) so tenant-specific failures are diagnosable.
