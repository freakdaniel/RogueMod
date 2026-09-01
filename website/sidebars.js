// @ts-check

/** @type {import('@docusaurus/plugin-content-docs').SidebarsConfig} */
const sidebars = {
  guides: [
    {
      type: 'doc',
      id: 'index',
      label: 'Introduction',
    },
    {
      type: 'category',
      label: 'Quick starts',
      items: [
        'creating-managed-mod',
        'creating-lua-mod',
        'creating-native-mod',
        'creating-pak-mod',
      ],
    },
    {
      type: 'category',
      label: 'Mod development',
      items: [
        'managed-api',
        'generated-sdk',
        'abstractions-api',
        'reflection-api',
      ],
    },
    {
      type: 'category',
      label: 'Packaging and management',
      items: [
        'mod-manifest',
        'mod-manager',
        'cli-reference',
      ],
    },
    {
      type: 'category',
      label: 'Design',
      items: [
        'architecture',
      ],
    },
    {
      type: 'category',
      label: 'Building RogueMod',
      items: [
        'windows-development',
        'linux-development',
      ],
    },
    {
      type: 'category',
      label: 'Contributing',
      items: [
        'contributing',
        'code-style',
      ],
    },
  ],
};

module.exports = sidebars;
