// Run from the repository root. No commits or tags are created by this tool.
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';

const git = (...args) => execFileSync('git', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true }).trim();
const stable = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/;
function parts(version) {
  if (typeof version !== 'string' || !stable.test(version)) throw new Error('Expected stable SemVer major.minor.patch');
  return version.split('.').map(BigInt);
}
function compare(left, right) {
  const a = parts(left), b = parts(right);
  for (let i = 0; i < 3; i++) if (a[i] !== b[i]) return a[i] > b[i] ? 1 : -1;
  return 0;
}

try {
  const [command, first, second, ...extra] = process.argv.slice(2);
  if (extra.length || !['bump', 'check'].includes(command) || !first || !second) {
    throw new Error('Usage: release-version.mjs bump <patch|minor|major> <manifest.json> OR check <manifest.json> <tag-prefix>');
  }
  const path = command === 'bump' ? second : first;
  const manifest = JSON.parse(readFileSync(path, 'utf8'));
  const versionParts = parts(manifest.version);
  if (command === 'bump') {
    const index = ['major', 'minor', 'patch'].indexOf(first);
    if (index < 0) throw new Error('Increment must be patch, minor or major');
    const branch = git('branch', '--show-current');
    if (!branch || ['main', 'release'].includes(branch)) {
      throw new Error('Bump the version on develop or a preparation branch before merging into main');
    }
    versionParts[index]++;
    for (let i = index + 1; i < 3; i++) versionParts[i] = 0n;
    manifest.version = versionParts.join('.');
    writeFileSync(path, JSON.stringify(manifest, null, 2) + '\n');
    console.log(manifest.version);
  } else {
    if (!/^[a-zA-Z][a-zA-Z0-9-]*$/.test(second)) throw new Error('Invalid tag prefix');
    if (git('status', '--porcelain', '--untracked-files=no')) throw new Error('Release source has uncommitted changes');
    const version = manifest.version, tag = second + version, sha = git('rev-parse', 'HEAD');
    const tags = git('tag', '--list', second + '*').split('\n').filter(Boolean);
    const existing = tags.includes(tag);
    if (existing) {
      if (git('rev-parse', tag + '^{commit}') !== sha) throw new Error('Version already belongs to another commit; bump before releasing');
    } else {
      for (const previous of tags.map(value => value.slice(second.length)).filter(value => stable.test(value))) {
        if (compare(version, previous) <= 0) throw new Error('New version must be greater than ' + previous);
      }
    }
    console.log(JSON.stringify({ version, tag, sha, existing }));
  }
} catch (error) {
  console.error('release-version: ' + error.message);
  process.exitCode = 1;
}
