import { defineConfig } from 'astro/config';

export default defineConfig({
  site: 'https://letterboxdsync.dev',
  trailingSlash: 'ignore',
  build: {
    assets: 'assets',
  },
});
