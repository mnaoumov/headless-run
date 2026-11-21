/**
 * @file
 *
 * Serves the documentation site with `astro dev` until it is stopped. `npm run docs:dev -- --port 4399` passes
 * arguments on to Astro.
 */

import { wrapCliTask } from '@mnaoumov/typescript-dev-toolkit/cli-utils';
import { resolveDocsSiteConfig } from '@mnaoumov/typescript-dev-toolkit/docs-site/docs-site-config';
import { execFromRoot } from '@mnaoumov/typescript-dev-toolkit/root';
import {
  existsSync,
  writeFileSync
} from 'node:fs';
import process from 'node:process';

import { DOCS_SITE_CONFIG } from './docs-site-config.ts';

await wrapCliTask(async () => {
  const resolved = resolveDocsSiteConfig(DOCS_SITE_CONFIG);
  if (!existsSync(resolved.apiDocSettings.sidebarFile)) {
    writeFileSync(resolved.apiDocSettings.sidebarFile, '[]\n');
  }

  const [, , ...astroArguments] = process.argv;
  // `ASTRO_DEV_BACKGROUND` keeps Astro's dev server in the foreground under a non-interactive environment.
  await execFromRoot(['astro', 'dev', ...astroArguments], {
    cwd: resolved.rootDirectory,
    env: {
      ASTRO_DEV_BACKGROUND: '1'
    },
    isInteractive: true
  });
});
