import { defineConfig } from 'astro/config';

export default defineConfig({
  site: 'https://lachlanyoung.dev',
  base: '/jellyfin-plugin-letterboxd',
  trailingSlash: 'ignore',
  build: {
    assets: 'assets',
  },
});
