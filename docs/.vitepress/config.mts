import { defineConfig } from 'vitepress';

const docsBase = '/HelmSharp/';

const englishNav = [
  { text: 'Get started', link: '/getting-started' },
  { text: 'Guides', link: '/guide/first-render' },
  { text: 'Examples', link: '/examples/render-preview-api' },
  { text: 'Reference', link: '/api-overview' },
  { text: 'Compatibility', link: '/helm-compatibility' },
  { text: 'Compare', link: '/compare' }
];

const englishSidebar = [
  {
    text: 'Start here',
    items: [
      { text: 'Overview', link: '/' },
      { text: 'Quickstart', link: '/getting-started' },
      { text: 'Choose a package', link: '/api-overview' },
      { text: 'Architecture', link: '/concepts/architecture' },
      { text: 'Release storage', link: '/concepts/release-storage' }
    ]
  },
  {
    text: 'Guides',
    items: [
      { text: 'Install packages', link: '/guide/installation' },
      { text: 'Render a chart', link: '/guide/first-render' },
      { text: 'Values and overrides', link: '/guide/values' },
      { text: 'Render for a target cluster', link: '/guide/template-rendering' },
      { text: 'Install and upgrade releases', link: '/guide/release-workflows' },
      { text: 'Apply manifests directly', link: '/guide/kubernetes-operations' },
      { text: 'Package charts and manage dependencies', link: '/guide/chart-distribution' },
      { text: 'Troubleshoot failures', link: '/guide/error-handling' }
    ]
  },
  {
    text: 'Scenarios',
    items: [
      { text: 'ASP.NET Core chart preview', link: '/scenarios/aspnet-core-preview' },
      { text: 'Kubernetes deployment service', link: '/scenarios/deployment-service' },
      { text: 'GitOps manifests', link: '/examples/gitops-pr-generator' }
    ]
  },
  {
    text: 'Examples',
    items: [
      { text: 'Build a render-preview endpoint', link: '/examples/render-preview-api' },
      { text: 'Turn a review into a deployment', link: '/examples/dry-run-deployment' },
      { text: 'Generate manifests for GitOps', link: '/examples/gitops-pr-generator' },
      { text: 'Render a public chart', link: '/examples/real-chart-rendering' },
      { text: 'Keep tenant defaults isolated', link: '/examples/multi-tenant-options' }
    ]
  },
  {
    text: 'Package reference',
    items: [
      { text: 'HelmSharp.Action', link: '/packages/action' },
      { text: 'HelmSharp.Chart', link: '/packages/chart' },
      { text: 'HelmSharp.Engine', link: '/packages/engine' },
      { text: 'HelmSharp.Kube', link: '/packages/kube' },
      { text: 'HelmSharp.Release', link: '/packages/release' },
      { text: 'HelmSharp.Repo', link: '/packages/repo' },
      { text: 'HelmSharp.Registry', link: '/packages/registry' },
      { text: 'HelmSharp.Storage', link: '/packages/storage' },
      { text: 'HelmSharp.PostRenderer', link: '/packages/post-renderer' }
    ]
  },
  {
    text: 'API reference',
    items: [
      { text: 'About the API reference', link: '/api/' },
      { text: 'Action API', link: '/api/generated/action' },
      { text: 'Chart API', link: '/api/generated/chart' },
      { text: 'Engine API', link: '/api/generated/engine' },
      { text: 'Kube API', link: '/api/generated/kube' },
      { text: 'Release API', link: '/api/generated/release' },
      { text: 'Repo API', link: '/api/generated/repo' },
      { text: 'Registry API', link: '/api/generated/registry' },
      { text: 'Storage API', link: '/api/generated/storage' },
      { text: 'Post-renderer API', link: '/api/generated/postrenderer' }
    ]
  },
  {
    text: 'Compatibility and project',
    items: [
      { text: 'Compatibility contract', link: '/helm-compatibility' },
      { text: 'Template-function matrix', link: '/template-function-compatibility' },
      { text: 'HelmCompare', link: '/compare' },
      { text: 'Roadmap', link: '/roadmap' },
      { text: 'Migrate from Helm CLI', link: '/migration/from-helm-cli' },
      { text: 'Changelog', link: 'https://github.com/GaTTGeng/HelmSharp/blob/master/CHANGELOG.md' }
    ]
  }
];

const chineseNav = [
  { text: '开始使用', link: '/zh/getting-started' },
  { text: '指南', link: '/zh/guide/first-render' },
  { text: '示例', link: '/zh/examples/render-preview-api' },
  { text: '参考', link: '/zh/api-overview' },
  { text: '兼容性', link: '/zh/helm-compatibility' },
  { text: '对比', link: '/zh/compare' }
];

const chineseSidebar = [
  {
    text: '从这里开始',
    items: [
      { text: '概览', link: '/zh/' },
      { text: '快速开始', link: '/zh/getting-started' },
      { text: '选择包和 API', link: '/zh/api-overview' },
      { text: '架构', link: '/zh/concepts/architecture' },
      { text: 'Release 存储', link: '/zh/concepts/release-storage' }
    ]
  },
  {
    text: '使用指南',
    items: [
      { text: '安装包', link: '/zh/guide/installation' },
      { text: '渲染 Chart', link: '/zh/guide/first-render' },
      { text: 'Values 与覆盖项', link: '/zh/guide/values' },
      { text: '按目标集群渲染', link: '/zh/guide/template-rendering' },
      { text: '安装和升级 Release', link: '/zh/guide/release-workflows' },
      { text: '直接提交清单', link: '/zh/guide/kubernetes-operations' },
      { text: '打包 Chart 与管理依赖', link: '/zh/guide/chart-distribution' },
      { text: '排查失败', link: '/zh/guide/error-handling' }
    ]
  },
  {
    text: '场景',
    items: [
      { text: 'ASP.NET Core Chart 预览', link: '/zh/scenarios/aspnet-core-preview' },
      { text: 'Kubernetes 部署服务', link: '/zh/scenarios/deployment-service' },
      { text: 'GitOps 清单', link: '/zh/examples/gitops-pr-generator' }
    ]
  },
  {
    text: '示例',
    items: [
      { text: '构建渲染预览接口', link: '/zh/examples/render-preview-api' },
      { text: '把评审结果变成部署', link: '/zh/examples/dry-run-deployment' },
      { text: '为 GitOps 生成清单', link: '/zh/examples/gitops-pr-generator' },
      { text: '渲染公开 Chart', link: '/zh/examples/real-chart-rendering' },
      { text: '隔离租户默认配置', link: '/zh/examples/multi-tenant-options' }
    ]
  },
  {
    text: '包参考',
    items: [
      { text: 'HelmSharp.Action', link: '/zh/packages/action' },
      { text: 'HelmSharp.Chart', link: '/zh/packages/chart' },
      { text: 'HelmSharp.Engine', link: '/zh/packages/engine' },
      { text: 'HelmSharp.Kube', link: '/zh/packages/kube' },
      { text: 'HelmSharp.Release', link: '/zh/packages/release' },
      { text: 'HelmSharp.Repo', link: '/zh/packages/repo' },
      { text: 'HelmSharp.Registry', link: '/zh/packages/registry' },
      { text: 'HelmSharp.Storage', link: '/zh/packages/storage' },
      { text: 'HelmSharp.PostRenderer', link: '/zh/packages/post-renderer' }
    ]
  },
  {
    text: 'API 参考',
    items: [
      { text: '如何使用 API 参考', link: '/zh/api/' },
      { text: 'Action API', link: '/zh/api/generated/action' },
      { text: 'Chart API', link: '/zh/api/generated/chart' },
      { text: 'Engine API', link: '/zh/api/generated/engine' },
      { text: 'Kube API', link: '/zh/api/generated/kube' },
      { text: 'Release API', link: '/zh/api/generated/release' },
      { text: 'Repo API', link: '/zh/api/generated/repo' },
      { text: 'Registry API', link: '/zh/api/generated/registry' },
      { text: 'Storage API', link: '/zh/api/generated/storage' },
      { text: 'Post-renderer API', link: '/zh/api/generated/postrenderer' }
    ]
  },
  {
    text: '兼容性和项目',
    items: [
      { text: '兼容性约定', link: '/zh/helm-compatibility' },
      { text: '模板函数矩阵', link: '/zh/template-function-compatibility' },
      { text: 'HelmCompare', link: '/zh/compare' },
      { text: '路线图', link: '/zh/roadmap' },
      { text: '从 Helm CLI 迁移', link: '/zh/migration/from-helm-cli' },
      { text: '更新日志', link: 'https://github.com/GaTTGeng/HelmSharp/blob/master/CHANGELOG.md' }
    ]
  }
];

export default defineConfig({
  title: 'HelmSharp',
  description: 'Use Helm charts from managed .NET code.',
  base: docsBase,
  cleanUrls: true,
  lastUpdated: true,
  head: [
    ['link', { rel: 'icon', type: 'image/svg+xml', href: `${docsBase}logo.svg` }],
    ['meta', { name: 'theme-color', content: '#1f5f52' }],
    ['meta', { property: 'og:type', content: 'website' }],
    ['meta', { property: 'og:title', content: 'HelmSharp documentation' }],
    ['meta', { property: 'og:description', content: 'Render, inspect, and release Helm charts from .NET.' }]
  ],
  markdown: {
    theme: {
      light: 'github-light',
      dark: 'github-dark'
    }
  },
  themeConfig: {
    logo: {
      src: '/logo.svg',
      alt: 'HelmSharp logo'
    },
    socialLinks: [
      { icon: 'github', link: 'https://github.com/GaTTGeng/HelmSharp' }
    ],
    search: {
      provider: 'local'
    }
  },
  locales: {
    root: {
      label: 'English',
      lang: 'en-US',
      link: '/',
      title: 'HelmSharp',
      description: 'Use Helm charts from managed .NET code.',
      themeConfig: {
        nav: englishNav,
        sidebar: englishSidebar,
        outline: { label: 'On this page' },
        docFooter: { prev: 'Previous page', next: 'Next page' },
        editLink: {
          pattern: 'https://github.com/GaTTGeng/HelmSharp/edit/master/docs/:path',
          text: 'Edit this page'
        },
        footer: {
          message: 'Released under the MIT License.',
          copyright: 'Copyright (c) 2026 HelmSharp contributors'
        }
      }
    },
    zh: {
      label: '简体中文',
      lang: 'zh-CN',
      link: '/zh/',
      title: 'HelmSharp',
      description: '在 .NET 进程中使用 Helm Chart。',
      themeConfig: {
        nav: chineseNav,
        sidebar: chineseSidebar,
        outline: { label: '本页内容' },
        docFooter: { prev: '上一页', next: '下一页' },
        editLink: {
          pattern: 'https://github.com/GaTTGeng/HelmSharp/edit/master/docs/:path',
          text: '在 GitHub 上编辑此页'
        },
        footer: {
          message: '基于 MIT License 发布。',
          copyright: 'Copyright (c) 2026 HelmSharp contributors'
        }
      }
    }
  }
});
