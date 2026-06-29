# Helm Compare Tool — Design Spec

## Overview

A VitePress-hosted tool that lets users upload a Helm chart, renders it with both HelmSharp and the real Helm CLI, then shows the results side-by-side in a diff view to demonstrate HelmSharp's parity.

## Architecture

```
Browser (VitePress /compare page)
    │  POST /api/v1/render  (multipart: chart.tgz + optional values.yaml)
    ▼
Docker: dotnet/aspnet:8.0 + helm v3 CLI
    │
    ├─→ HelmSharp template render
    └─→ helm template ... (real Helm CLI)
         │
         ▼
    JSON { helmOutput, helmsharpOutput, lintWarnings, chartName, isMatch, templatesCompared }
         │
         ▼
Browser: side-by-side line diff with summary badge
```

## Projects

| # | Project | Location | Tech | Purpose |
|---|---------|----------|------|---------|
| 1 | `HelmCompare` | `C:\Users\gengrp\RiderProjects\HelmCompare` | .NET 8 Minimal API | API server: receive upload, validate, dual render, return diff |
| 2 | VitePress compare page | `docs/compare.md` (existing `docs/` VitePress project) | Vue 3 SFC in VitePress | Upload UI + diff viewer |

## API Contract

### `POST /api/v1/render`

**Request:** `multipart/form-data`
- `chart` (file, required) — `.tgz` Helm chart package
- `values` (file, optional) — `.yaml` values override

**Response 200:**
```json
{
  "chartName": "nginx",
  "helmOutput": "apiVersion: v1\n...",
  "helmsharpOutput": "apiVersion: v1\n...",
  "lintWarnings": [],
  "isMatch": true,
  "templatesCompared": 5
}
```

**Response 400:**
```json
{
  "error": "包里没有 Chart.yaml，是不是传错了？"
}
```

**Response 500:**
```json
{
  "error": "渲染失败: <details>"
}
```

### `GET /api/v1/health`

**Response 200:**
```json
{ "status": "ok", "helmVersion": "v3.16.0", "helmsharpVersion": "..." }
```

## Validation Pipeline (3 layers)

### Layer 1 — Client-side (browser)
- File extension whitelist: `.tgz`, `.tar.gz`
- MIME sniff via `file.type` and magic bytes check (`.tgz` starts with `1F 8B`)
- Invalid → instant red border tooltip, no network request

### Layer 2 — Server unpack check
- `tar -tzf` the uploaded file
- Assert `Chart.yaml` exists at chart root
- Parse `apiVersion` (must be `v1` or `v2`), `name` must be non-empty
- Fail → HTTP 400 with Chinese error message

### Layer 3 — Helm lint
- Run `helm lint <chart-dir>` on the server
- Warnings are captured but don't block rendering — they appear as a yellow badge

## Rendering

Same values fed to both engines:
- Chart's built-in `values.yaml` is always loaded
- User-provided values (file or text) merged on top
- Both engines receive identical `chartPath` + `valuesPath`

### HelmSharp render
Use `HelmChartLoader.LoadAsync()` + `HelmTemplateRenderer` from `HelmSharp.Engine`.

### Helm CLI render
`helm template <name> <chart-dir> -f <values-path>` via `Process.Start`.

Both outputs captured as raw strings, returned in the JSON response.

## Docker Image

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN curl -fsSL https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8090
ENTRYPOINT ["dotnet", "HelmCompare.dll"]
```

- Base: `dotnet/aspnet:8.0` (includes ASP.NET runtime)
- Helm: installed via official `get-helm-3` script
- If Docker Hub is slow: use proxy `10.112.16.16:7890` during build

## VitePress Page

### Route
`/compare` — standalone full-width page (no sidebar), added to English nav.

### Components
Single-file component (`compare.md` with Vue SFC):

```
┌─────────────────────────────────────────┐
│  📦 Upload Helm Chart (.tgz)            │
│  ┌ [drop zone or click to browse] ────┐ │
│  └─────────────────────────────────────┘ │
│                                         │
│  ⚙️ Custom Values (optional)             │
│  ┌ textarea (yaml) ───────────────────┐ │
│  │ or upload values.yaml               │ │
│  └─────────────────────────────────────┘ │
│                                         │
│  [Start Compare]  (button)              │
├─────────────────────────────────────────┤
│  ⬇ Result area                          │
│  [✅ Match / ⚠️ 3 diffs / ❌ Error]      │
│  ┌─ Helm ──┬── HelmSharp ──┐           │
│  │ ...     │  ...           │           │
│  └─────────┴────────────────┘           │
└─────────────────────────────────────────┘
```

### Visual theme
Use existing VitePress CSS custom properties: `--vp-c-brand-1` (#2563eb), `--vp-c-brand-2` (#1d4ed8), `--vp-font-family-base`, `--vp-c-bg`, `--vp-c-text-1`, etc. Match the existing VitePress look & feel.

### Diff display
Custom lightweight line-by-line diff algorithm:
- Split both outputs into lines
- Compute LCS (longest common subsequence) for alignment
- Render: common lines → green background, added lines → green-highlight (+), removed/changed → red-highlight (-)
- Summary: total templates compared, match/mismatch count

## Deployment

1. Build `HelmCompare` .NET project: `dotnet publish -c Release -r linux-x64 --self-contained`
2. Build Docker image: `docker build -t helm-compare .`
3. Push to server `124.222.25.118`: `docker save` / `scp` / `docker load`
4. Run: `docker run -d -p 8090:8090 --name helm-compare helm-compare`
5. VitePress page: build via `npm run docs:build` and deploy to GitHub Pages (existing workflow)

## States & Edge Cases

| State | UI |
|-------|-----|
| No file selected | Button disabled, hint text visible |
| File selected, frontend check passes | Show file name + size, enable button |
| File rejected (wrong type) | Red border on drop zone, message: "请上传 .tgz 格式的 Helm Chart" |
| Uploading | Spinner on button, disabled |
| Server validation fail (400) | Red toast with server error message |
| Server render error (500) | Red card with error details |
| Both render, identical | Green "✅ 完全一致" badge, diff panel shows "all lines match" |
| Both render, with diffs | Yellow "⚠️ N 处差异" badge, diff panel with red/green highlights |
| HelmSharp fails, Helm works | Red "❌ HelmSharp 渲染失败" card showing exception, Helm output shown normally |
| Helm lint warnings | Yellow chip "⚠️ N lint warnings" in result header |

## Security

- File size limit: 10 MB (configurable server-side)
- Only `.tgz` / `.tar.gz` accepted
- Server unpacks to temp directory, cleans up after render
- `helm template` runs with `--dry-run` flag by default (no cluster connection needed)
- No chart installation — template rendering only, no side effects
