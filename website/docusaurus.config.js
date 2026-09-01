// @ts-check

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'RogueMod',
  tagline: 'Mod loader and SDK for Deadzone: Rogue',
  url: 'https://freakdaniel.github.io',
  baseUrl: '/RogueMod/',
  organizationName: 'freakdaniel',
  projectName: 'RogueMod',
  onBrokenLinks: 'throw',
  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },
  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          path: '../docs',
          routeBasePath: 'docs',
          sidebarPath: './sidebars.js',
          editUrl: 'https://github.com/freakdaniel/RogueMod/edit/master/docs/',
        },
        blog: false,
      }),
    ],
  ],
  plugins: [
    [
      '@easyops-cn/docusaurus-search-local',
      /** @type {import('@easyops-cn/docusaurus-search-local').PluginOptions} */
      ({
        hashed: true,
        indexBlog: false,
        docsRouteBasePath: ['docs', 'api'],
        docsDir: ['../docs', 'reference'],
      }),
    ],
    [
      '@docusaurus/plugin-content-docs',
      /** @type {import('@docusaurus/plugin-content-docs').Options} */
      ({
        id: 'api',
        path: 'reference',
        routeBasePath: 'api',
        sidebarPath: './apiSidebars.js',
        editUrl: 'https://github.com/freakdaniel/RogueMod/edit/master/website/reference/',
      }),
    ],
  ],
  themeConfig:
    /** @type {import('@docusaurus/preset-classic').ThemeConfig} */
    ({
      navbar: {
        title: 'RogueMod',
        items: [
          { to: '/docs', label: 'Docs', position: 'left' },
          { to: '/api', label: 'API Reference', position: 'left' },
          {
            href: 'https://github.com/freakdaniel/RogueMod',
            label: 'GitHub',
            position: 'right',
          },
        ],
      },
      footer: {
        style: 'dark',
        copyright: `RogueMod — mod loader and SDK for Deadzone: Rogue. Built with Docusaurus.`,
      },
    }),
};

module.exports = config;
