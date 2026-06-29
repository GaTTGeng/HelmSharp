import { defineConfig } from 'vitepress';

const englishNav = [
  { text: 'Guide', link: '/getting-started' },
  { text: 'API', link: '/api-overview' },
  { text: 'Compatibility', link: '/helm-compatibility' },
  { text: 'Roadmap', link: '/roadmap' },
  { text: 'Compare', link: '/compare' }
];

const englishSidebar = [
  {
    text: 'HelmSharp',
    items: [
      { text: 'Overview', link: '/' },
      { text: 'Getting Started', link: '/getting-started' },
      { text: 'API Overview', link: '/api-overview' },
      { text: 'Helm Compatibility', link: '/helm-compatibility' },
      { text: 'Roadmap', link: '/roadmap' },
      { text: 'Compare', link: '/compare' }
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
  { text: 'API', link: '/zh/api-overview' },
  { text: '兼容性', link: '/zh/helm-compatibility' },
  { text: '路线图', link: '/zh/roadmap' },
  { text: '对比', link: '/compare' }
];

const chineseSidebar = [
  {
    text: 'HelmSharp',
    items: [
      { text: '概览', link: '/zh/' },
      { text: '快速开始', link: '/zh/getting-started' },
      { text: 'API 选择', link: '/zh/api-overview' },
      { text: 'Helm 兼容性', link: '/zh/helm-compatibility' },
      { text: '路线图', link: '/zh/roadmap' },
      { text: '对比工具', link: '/compare' }
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
  base: '/HelmSharp/',
  cleanUrls: true,
  lastUpdated: true,
  head: [
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
    logo: '/logo.svg',
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
