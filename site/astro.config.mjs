import { defineConfig } from 'astro/config';

export default defineConfig({
  site: 'https://builtbyproxy.github.io',
  base: '/jellyfin-plugin-letterboxd',
  trailingSlash: 'ignore',
  build: {
    assets: 'assets',
  },
});
