/**
 * @file
 *
 * Builds the documentation site: the toolkit's `buildDocsSite` without its API reference step, since this repository
 * has no TypeScript API. It generates the Open Graph cards, runs `astro build`, checks every link of the output and
 * checks the repository's GitHub homepage. It first refuses a `package.json` version that differs from the launcher's.
 */

import { wrapCliTask } from '@mnaoumov/typescript-dev-toolkit/cli-utils';
import { assertDocsLinksResolve } from '@mnaoumov/typescript-dev-toolkit/docs-site/docs-links';
import { getDocsSiteAssetsDirectory } from '@mnaoumov/typescript-dev-toolkit/docs-site/docs-site-assets';
import { resolveDocsSiteConfig } from '@mnaoumov/typescript-dev-toolkit/docs-site/docs-site-config';
import { generateOgImages } from '@mnaoumov/typescript-dev-toolkit/docs-site/og-images';
import { syncRepoHomepage } from '@mnaoumov/typescript-dev-toolkit/docs-site/repo-homepage';
import { execFromRoot } from '@mnaoumov/typescript-dev-toolkit/root';
import {
  existsSync,
  readFileSync,
  writeFileSync
} from 'node:fs';
import { join } from 'node:path';

import { DOCS_SITE_CONFIG } from './docs-site-config.ts';

await wrapCliTask(async () => {
  const resolved = resolveDocsSiteConfig(DOCS_SITE_CONFIG);
  assertVersionsMatch(resolved.rootDirectory, resolved.packageVersion);

  // The shared Astro config reads the API sidebar the generator writes; this site has none.
  const sidebarFile = resolved.apiDocSettings.sidebarFile;
  if (!existsSync(sidebarFile)) {
    writeFileSync(sidebarFile, '[]\n');
  }

  await generateOgImages({
    brandText: resolved.packageName,
    contentDocsDirectory: resolved.contentDocsDirectory,
    faviconPath: resolved.faviconPath,
    fontsDirectory: join(getDocsSiteAssetsDirectory(), 'fonts'),
    outputDirectory: resolved.ogOutputDirectory
  });
  await execFromRoot(['astro', 'build'], { cwd: resolved.rootDirectory });
  await assertDocsLinksResolve({ outputDirectory: resolved.distDirectory, siteBaseUrl: resolved.siteBaseUrl });
  await syncRepoHomepage({ homepageUrl: resolved.siteBaseUrl, mode: resolved.repoHomepage, owner: resolved.owner, repo: resolved.repo });
});

function assertVersionsMatch(rootDirectory: string, packageVersion: string): void {
  const projectPath = join(rootDirectory, 'src', 'headless-run', 'headless-run.csproj');
  const match = /<Version>(?<version>[^<]+)<\/Version>/u.exec(readFileSync(projectPath, 'utf-8'));
  const launcherVersion = match?.groups?.['version'];
  if (launcherVersion !== packageVersion) {
    throw new Error(`package.json version ${packageVersion} differs from the launcher's ${String(launcherVersion)} in ${projectPath}.`);
  }
}
