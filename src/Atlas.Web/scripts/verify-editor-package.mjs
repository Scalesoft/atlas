// Checks that the editor's lazily loaded OpenAPI assets made it into a build. Run by the AtlasBuildFrontend target
// (Directory.Build.targets), so by every `dotnet publish` including the Docker image build. The editor itself is built from
// src/Atlas.Markdown (scripts/build-editor.mjs); there is no released package to compare against.
//
//   pnpm run verify:editor --dist <dir>  after the build: the OpenAPI assets were emitted into <dir>/assets
import { existsSync, readdirSync } from "node:fs";
import { resolve } from "node:path";
import { parseArgs } from "node:util";
import { missingAssets, openApiAssets } from "./editorPackage.mjs";

function verifyDist(dist) {
  const assets = resolve(dist, "assets");
  const missing = existsSync(assets) ? missingAssets(readdirSync(assets)) : openApiAssets.map(asset => asset.name);
  for (const name of missing) console.error(`error: ${assets} has no ${name}.`);
  if (missing.length === 0) console.log(`OpenAPI assets present in ${assets}.`);
  return missing.length === 0 ? 0 : 1;
}

function main(args) {
  let options;
  try {
    ({ values: options } = parseArgs({ args, options: { dist: { type: "string" } }, strict: true, allowPositionals: false }));
  } catch (error) {
    console.error(error.message);
  }
  if (options?.dist === undefined) {
    console.error("Usage: node scripts/verify-editor-package.mjs --dist <dir>");
    return 2;
  }
  return verifyDist(resolve(options.dist));
}

process.exitCode = main(process.argv.slice(2));
