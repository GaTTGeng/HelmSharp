import { defineConfig } from 'vitepress';

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
    nav: [
      { text: 'Guide', link: '/getting-started' },
      { text: 'API', link: '/api-overview' },
      { text: 'Compatibility', link: '/helm-compatibility' },
      { text: 'Roadmap', link: '/roadmap' }
    ],
    sidebar: [
      {
        text: 'HelmSharp',
        items: [
          { text: 'Overview', link: '/' },
          { text: 'Getting Started', link: '/getting-started' },
          { text: 'API Overview', link: '/api-overview' },
          { text: 'Helm Compatibility', link: '/helm-compatibility' },
          { text: 'Roadmap', link: '/roadmap' }
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
    ],
    socialLinks: [
      { icon: 'github', link: 'https://github.com/GaTTGeng/HelmSharp' }
    ],
    search: {
      provider: 'local'
    },
    editLink: {
      pattern: 'https://github.com/GaTTGeng/HelmSharp/edit/master/docs/:path',
      text: 'Edit this page on GitHub'
    },
    footer: {
      message: 'Released under the MIT License.',
      copyright: 'Copyright (c) 2026 HelmSharp contributors'
    }
  }
});
