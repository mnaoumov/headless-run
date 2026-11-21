/**
 * @file
 *
 * Serves the last build of the documentation site with `astro preview` until it is stopped.
 * `npm run docs:preview -- --port 4399` passes arguments on to Astro.
 */

import { wrapCliTask } from '@mnaoumov/typescript-dev-toolkit/cli-utils';
import { resolveDocsSiteConfig } from '@mnaoumov/typescript-dev-toolkit/docs-site/docs-site-config';
import { execFromRoot } from '@mnaoumov/typescript-dev-toolkit/root';
import process from 'node:process';

import { DOCS_SITE_CONFIG } from './docs-site-config.ts';

await wrapCliTask(async () => {
  const resolved = resolveDocsSiteConfig(DOCS_SITE_CONFIG);
  const [, , ...astroArguments] = process.argv;
  await execFromRoot(['astro', 'preview', ...astroArguments], { cwd: resolved.rootDirectory, isInteractive: true });
});
