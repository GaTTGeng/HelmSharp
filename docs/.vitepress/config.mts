import { defineConfig } from 'vitepress';

const docsBase = '/HelmSharp/';

const englishNav = [
  { text: 'Guide', link: '/getting-started' },
  { text: 'Examples', link: '/examples/render-preview-api' },
  { text: 'Packages', link: '/packages/action' },
  { text: 'API', link: '/api/' },
  { text: 'Compatibility', link: '/helm-compatibility' },
  { text: 'Compare', link: '/compare' }
];

const englishSidebar = [
  {
    text: 'HelmSharp',
    items: [
      { text: 'Overview', link: '/' },
      { text: 'Getting Started', link: '/getting-started' },
      { text: 'API Overview', link: '/api-overview' },
      { text: 'Roadmap', link: '/roadmap' },
      { text: 'Compare', link: '/compare' }
    ]
  },
  {
    text: 'Guide',
    items: [
      { text: 'Installation', link: '/guide/installation' },
      { text: 'First Render', link: '/guide/first-render' },
      { text: 'Values', link: '/guide/values' },
      { text: 'Template Rendering', link: '/guide/template-rendering' },
      { text: 'Chart Distribution', link: '/guide/chart-distribution' },
      { text: 'Release Workflows', link: '/guide/release-workflows' },
      { text: 'Kubernetes Operations', link: '/guide/kubernetes-operations' },
      { text: 'Error Handling', link: '/guide/error-handling' }
    ]
  },
  {
    text: 'Examples',
    items: [
      { text: 'Render Preview API', link: '/examples/render-preview-api' },
      { text: 'GitOps PR Generator', link: '/examples/gitops-pr-generator' },
      { text: 'Dry-run Deployment', link: '/examples/dry-run-deployment' },
      { text: 'Public Chart Rendering', link: '/examples/real-chart-rendering' },
      { text: 'Multi-tenant Options', link: '/examples/multi-tenant-options' }
    ]
  },
  {
    text: 'Packages',
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
    text: 'API Reference',
    items: [
      { text: 'How to read API pages', link: '/api/' },
      { text: 'Action API', link: '/api/generated/action' },
      { text: 'Chart API', link: '/api/generated/chart' },
      { text: 'Engine API', link: '/api/generated/engine' },
      { text: 'Kube API', link: '/api/generated/kube' },
      { text: 'Release API', link: '/api/generated/release' },
      { text: 'Repo API', link: '/api/generated/repo' },
      { text: 'Registry API', link: '/api/generated/registry' },
      { text: 'Storage API', link: '/api/generated/storage' },
      { text: 'PostRenderer API', link: '/api/generated/postrenderer' }
    ]
  },
  {
    text: 'Compatibility',
    items: [
      { text: 'Helm Compatibility', link: '/helm-compatibility' }
    ]
  },
  {
    text: 'Project',
    items: [
      { text: 'GitHub', link: 'https://github.com/GaTTGeng/HelmSharp' },
      { text: 'NuGet', link: 'https://www.nuget.org/packages/HelmSharp.Action' },
      { text: 'Changelog', link: 'https://github.com/GaTTGeng/HelmSharp/blob/master/CHANGELOG.md' }
    ]
  }
];

const chineseNav = [
  { text: '指南', link: '/zh/getting-started' },
  { text: '示例', link: '/zh/examples/render-preview-api' },
  { text: '包', link: '/zh/packages/action' },
  { text: 'API', link: '/zh/api/' },
  { text: '兼容性', link: '/zh/helm-compatibility' },
  { text: '对比', link: '/zh/compare' }
];

const chineseSidebar = [
  {
    text: 'HelmSharp',
    items: [
      { text: '概览', link: '/zh/' },
      { text: '快速开始', link: '/zh/getting-started' },
      { text: 'API 选择', link: '/zh/api-overview' },
      { text: '路线图', link: '/zh/roadmap' },
      { text: '对比工具', link: '/zh/compare' }
    ]
  },
  {
    text: '指南',
    items: [
      { text: '安装', link: '/zh/guide/installation' },
      { text: '第一次渲染', link: '/zh/guide/first-render' },
      { text: '值配置（Values）', link: '/zh/guide/values' },
      { text: '模板渲染', link: '/zh/guide/template-rendering' },
      { text: 'Chart 分发', link: '/zh/guide/chart-distribution' },
      { text: '发布工作流', link: '/zh/guide/release-workflows' },
      { text: 'Kubernetes 操作', link: '/zh/guide/kubernetes-operations' },
      { text: '错误处理', link: '/zh/guide/error-handling' }
    ]
  },
  {
    text: '示例',
    items: [
      { text: '渲染预览 API', link: '/zh/examples/render-preview-api' },
      { text: 'GitOps PR 生成器', link: '/zh/examples/gitops-pr-generator' },
      { text: '试运行部署', link: '/zh/examples/dry-run-deployment' },
      { text: '公开 Chart 渲染', link: '/zh/examples/real-chart-rendering' },
      { text: '多租户选项', link: '/zh/examples/multi-tenant-options' }
    ]
  },
  {
    text: '包',
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
      { text: '如何阅读 API 页', link: '/zh/api/' },
      { text: 'Action API', link: '/zh/api/generated/action' },
      { text: 'Chart API', link: '/zh/api/generated/chart' },
      { text: 'Engine API', link: '/zh/api/generated/engine' },
      { text: 'Kube API', link: '/zh/api/generated/kube' },
      { text: 'Release API', link: '/zh/api/generated/release' },
      { text: 'Repo API', link: '/zh/api/generated/repo' },
      { text: 'Registry API', link: '/zh/api/generated/registry' },
      { text: 'Storage API', link: '/zh/api/generated/storage' },
      { text: 'PostRenderer API', link: '/zh/api/generated/postrenderer' }
    ]
  },
  {
    text: '兼容性',
    items: [
      { text: 'Helm 兼容性', link: '/zh/helm-compatibility' }
    ]
  },
  {
    text: '项目',
    items: [
      { text: 'GitHub', link: 'https://github.com/GaTTGeng/HelmSharp' },
      { text: 'NuGet', link: 'https://www.nuget.org/packages/HelmSharp.Action' },
      { text: '更新日志', link: 'https://github.com/GaTTGeng/HelmSharp/blob/master/CHANGELOG.md' }
    ]
  }
];

export default defineConfig({
  title: 'HelmSharp',
  description: 'Managed Helm-compatible chart rendering and Kubernetes release workflows for .NET.',
  base: docsBase,
  cleanUrls: true,
  lastUpdated: true,
  head: [
    ['link', { rel: 'icon', type: 'image/svg+xml', href: `${docsBase}logo.svg` }],
    ['meta', { name: 'theme-color', content: '#2563eb' }],
    ['meta', { property: 'og:type', content: 'website' }],
    ['meta', { property: 'og:title', content: 'HelmSharp Documentation' }],
    ['meta', { property: 'og:description', content: 'Managed Helm-compatible SDK for .NET.' }]
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
      description: 'Managed Helm-compatible chart rendering and Kubernetes release workflows for .NET.',
      themeConfig: {
        nav: englishNav,
        sidebar: englishSidebar,
        editLink: {
          pattern: 'https://github.com/GaTTGeng/HelmSharp/edit/master/docs/:path',
          text: 'Edit this page on GitHub'
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
      description: '面向 .NET 的托管 Helm 兼容 SDK。',
      themeConfig: {
        nav: chineseNav,
        sidebar: chineseSidebar,
        editLink: {
          pattern: 'https://github.com/GaTTGeng/HelmSharp/edit/master/docs/:path',
          text: '在 GitHub 上编辑此页'
        },
        outline: {
          label: '本页内容'
        },
        docFooter: {
          prev: '上一页',
          next: '下一页'
        },
        footer: {
          message: '基于 MIT License 发布。',
          copyright: 'Copyright (c) 2026 HelmSharp contributors'
        }
      }
    }
  }
});
