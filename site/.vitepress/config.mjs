// The documentation site, published to GitHub Pages by .github/workflows/pages.yml (site/README.md).
//
// The pages are the repository's own Markdown, where they already are: the README, docs/, the samples, the changelog and
// the governance files. Nothing is copied: the site's source directory is the repository root, `pages` below says which
// files are on the site and at which address, and every other Markdown file is left out.
//
// The files link to one another the way GitHub shows them (relative to the file), and also to code and workflows. The
// relativeLinks plugin below turns a link to a file on the site into that page's address, and any other relative link
// into the file on GitHub, so the Markdown reads the same on GitHub and on the site.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitepress';

const repository = 'https://github.com/siebe41/SemanticRouter.NET';
const site = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const root = path.dirname(site);

// Source file (relative to the repository root) → page (relative to the site's root).
const pages = {
  'site/index.md': 'index.md',
  'site/videos.md': 'videos.md',
  'README.md': 'overview.md',
  'docs/README.md': 'docs.md',
  'CHANGELOG.md': 'changelog.md',
  'SECURITY.md': 'security.md',
  'CONTRIBUTING.md': 'contributing.md',
  'GOVERNANCE.md': 'governance.md',
};
for (const file of fs.readdirSync(path.join(root, 'docs'))) {
  if (file.endsWith('.md') && file !== 'README.md') pages[`docs/${file}`] = file.toLowerCase();
}
for (const sample of fs.readdirSync(path.join(root, 'samples'), { withFileTypes: true })) {
  if (sample.isDirectory() && fs.existsSync(path.join(root, 'samples', sample.name, 'README.md'))) {
    pages[`samples/${sample.name}/README.md`] = `samples/${sample.name.toLowerCase()}.md`;
  }
}

// Every other Markdown file in the repository stays off the site.
function otherMarkdown(dir = root, found = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (['.git', 'node_modules', 'bin', 'obj', 'cache', 'dist'].includes(entry.name)) continue;
    const full = path.join(dir, entry.name);
    const relative = path.relative(root, full).split(path.sep).join('/');
    if (entry.isDirectory()) otherMarkdown(full, found);
    else if (entry.name.endsWith('.md') && !(relative in pages)) found.push(relative);
  }
  return found;
}

// A relative link in a page: to another page's address, or to the file or folder on GitHub.
function relativeLinks(md) {
  md.core.ruler.push('relative-links', (state) => {
    const source = state.env.realPath ?? state.env.path; // the source file; env.path is its page
    if (!source) return;
    const from = path.relative(root, source).split(path.sep).join('/');

    const visit = (tokens) => {
      for (const token of tokens) {
        if (token.children) visit(token.children);
        if (token.type !== 'link_open') continue;
        const href = token.attrGet('href');
        if (!href || /^([a-z][a-z0-9+.-]*:|#|\/)/i.test(href)) continue;
        const [target, hash = ''] = href.split(/(?=#)/);
        const file = path.posix.normalize(path.posix.join(path.posix.dirname(from), decodeURI(target)));
        if (file.startsWith('../')) continue;
        if (file in pages) {
          token.attrSet('href', `/${pages[file]}${hash}`);
        } else {
          const isDir = fs.existsSync(path.join(root, file)) && fs.statSync(path.join(root, file)).isDirectory();
          token.attrSet('href', `${repository}/${isDir ? 'tree' : 'blob'}/main/${encodeURI(file.replace(/\/$/, ''))}${hash}`);
        }
      }
    };
    visit(state.tokens);
  });
}

// A ```mermaid block becomes the theme's Mermaid component, which draws it in the browser.
function mermaid(md) {
  const fence = md.renderer.rules.fence;
  md.renderer.rules.fence = (tokens, i, options, env, self) => tokens[i].info.trim() === 'mermaid'
    ? `<Mermaid code="${encodeURIComponent(tokens[i].content)}" />`
    : fence(tokens, i, options, env, self);
}

// The pages are outside site/, where there is no node_modules: what they import (vue) resolves as if from site/.
function fromSite() {
  return {
    name: 'resolve-from-site',
    enforce: 'pre',
    resolveId(source, importer, options) {
      if (!importer || /^[./\0]/.test(source) || source.startsWith('virtual:') || importer.startsWith(site + path.sep)) {
        return null;
      }
      return this.resolve(source, path.join(site, 'package.json'), { ...options, skipSelf: true });
    },
  };
}

const page = (text, file) => ({ text, link: `/${pages[file].replace(/\.md$/, '')}` });

export default defineConfig({
  title: 'SemanticRouter.NET',
  description: 'Send every prompt to the cheapest model that can answer it: local models, your subscription CLIs, or paid APIs.',
  // The Pages workflow passes the address the site is served under (/SemanticRouter.NET/ for a project site).
  base: process.env.SITE_BASE || '/SemanticRouter.NET/',
  srcDir: root,
  srcExclude: otherMarkdown(),
  rewrites: pages,
  outDir: path.join(site, 'dist'),
  cacheDir: path.join(site, 'cache'),
  cleanUrls: true,
  // The guides link to the router's own pages at http://localhost:5080.
  ignoreDeadLinks: 'localhostLinks',
  lastUpdated: true,
  head: [['link', { rel: 'icon', type: 'image/png', href: `${process.env.SITE_BASE || '/SemanticRouter.NET/'}logo.png` }]],
  vite: {
    publicDir: path.join(site, 'public'),
    plugins: [fromSite()],
    // Mermaid's chunks are large, and loaded only on a page with a diagram.
    build: { chunkSizeWarningLimit: 1000 },
  },
  markdown: {
    config(md) {
      relativeLinks(md);
      mermaid(md);
    },
  },
  themeConfig: {
    logo: '/logo.png',
    nav: [
      { text: 'Get started', link: '/overview#quick-start' },
      { text: 'Guides', link: '/docs' },
      { text: 'Videos', link: '/videos' },
      { text: 'Changelog', link: '/changelog' },
      { text: 'NuGet', link: 'https://www.nuget.org/packages/SemanticRouter.Host' },
    ],
    sidebar: [
      {
        text: 'Get started',
        items: [
          page('Overview and configuration', 'README.md'),
          page('Install and run', 'docs/host.md'),
          page('Desktop app', 'docs/desktop.md'),
          page('Connect your tools', 'docs/clients.md'),
          page('Providers', 'docs/providers.md'),
          { text: 'Videos', link: '/videos' },
        ],
      },
      {
        text: 'Use it',
        items: [
          page('Dashboard', 'docs/dashboard.md'),
          page('Routing quality', 'docs/routing-quality.md'),
          page('Routing profiles', 'docs/routing-profiles.md'),
          page('Usage and telemetry', 'docs/observability.md'),
          page('Memory map', 'docs/memory.md'),
          page('Network traffic', 'docs/network.md'),
        ],
      },
      {
        text: 'Run it in an organisation',
        items: [
          page('Machine-wide policy', 'docs/enterprise.md'),
          page('Verifying a release', 'docs/releases.md'),
          page('Architecture', 'docs/architecture.md'),
          page('Public API', 'docs/api-surface.md'),
        ],
      },
      {
        text: 'Samples',
        collapsed: true,
        items: Object.entries(pages)
          .filter(([file]) => file.startsWith('samples/'))
          .map(([file]) => page(file.split('/')[1], file)),
      },
      {
        text: 'Project',
        items: [
          page('All guides', 'docs/README.md'),
          page('Roadmap', 'docs/roadmap.md'),
          page('Changelog', 'CHANGELOG.md'),
          page('Subscription CLI terms', 'docs/TERMS.md'),
          page('Security', 'SECURITY.md'),
          page('Contributing', 'CONTRIBUTING.md'),
          page('Governance', 'GOVERNANCE.md'),
        ],
      },
    ],
    socialLinks: [{ icon: 'github', link: repository }],
    search: { provider: 'local' },
    outline: { level: [2, 3] },
    editLink: { pattern: `${repository}/edit/main/:path`, text: 'Edit this page on GitHub' },
    footer: {
      message: 'Released under the MIT License.',
      copyright: 'SemanticRouter.NET contributors',
    },
  },
});
