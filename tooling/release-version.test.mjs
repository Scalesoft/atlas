import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync, execFileSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const script = fileURLToPath(new URL('./release-version.mjs', import.meta.url));
function repository(t, version = '1.2.3') {
  const cwd = mkdtempSync(join(tmpdir(), 'release-version-'));
  t.after(() => rmSync(cwd, { recursive: true, force: true }));
  const git = (...args) => execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  git('init', '-b', 'candidate/next');
  git('config', 'user.name', 'Release test');
  git('config', 'user.email', 'release@example.invalid');
  const setVersion = value => writeFileSync(join(cwd, 'package.json'), JSON.stringify({ name: 'fixture', version: value }));
  setVersion(version);
  const commit = () => { git('add', '.'); git('commit', '-m', 'fixture'); };
  commit();
  const run = (...args) => spawnSync(process.execPath, [script, ...args], { cwd, encoding: 'utf8' });
  return { cwd, git, run, commit, setVersion };
}

test('bump resets subordinate components and preserves the manifest', t => {
  for (const [kind, expected] of [['patch', '1.2.4'], ['minor', '1.3.0'], ['major', '2.0.0']]) {
    const repo = repository(t);
    const result = repo.run('bump', kind, 'package.json');
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(readFileSync(join(repo.cwd, 'package.json'))), { name: 'fixture', version: expected });
  }
});

test('bump refuses main and invalid increments without changing the version', t => {
  const repo = repository(t);
  for (const kind of ['banana', 'constructor']) assert.match(repo.run('bump', kind, 'package.json').stderr, /Increment must/);
  for (const branch of ['main', 'release']) {
    repo.git('checkout', '-b', branch);
    assert.match(repo.run('bump', 'patch', 'package.json').stderr, /before merging into main/);
  }
  assert.equal(JSON.parse(readFileSync(join(repo.cwd, 'package.json'))).version, '1.2.3');
});

test('develop can prepare the next candidate before promotion to main', t => {
  const repo = repository(t);
  repo.git('checkout', '-b', 'develop');
  const result = repo.run('bump', 'minor', 'package.json');
  assert.equal(result.status, 0, result.stderr);
  assert.equal(JSON.parse(readFileSync(join(repo.cwd, 'package.json'))).version, '1.3.0');
});

test('release permits a new version and same-commit retry, but rejects reassignment', t => {
  const repo = repository(t);
  const first = repo.run('check', 'package.json', 'v');
  assert.equal(first.status, 0, first.stderr);
  assert.deepEqual(JSON.parse(first.stdout), { version: '1.2.3', tag: 'v1.2.3', sha: repo.git('rev-parse', 'HEAD'), existing: false });
  repo.git('tag', 'v1.2.3');
  const retry = repo.run('check', 'package.json', 'v');
  assert.equal(retry.status, 0, retry.stderr);
  assert.equal(JSON.parse(retry.stdout).existing, true);
  writeFileSync(join(repo.cwd, 'change.txt'), 'different source');
  repo.commit();
  assert.match(repo.run('check', 'package.json', 'v').stderr, /another commit/);
});

test('release compares numerically and rejects regression and dirty sources', t => {
  const repo = repository(t, '1.10.0');
  repo.git('tag', 'v1.9.0');
  assert.equal(repo.run('check', 'package.json', 'v').status, 0);
  repo.git('tag', 'v2.0.0');
  assert.match(repo.run('check', 'package.json', 'v').stderr, /greater than/);
  repo.setVersion('2.1.0');
  assert.match(repo.run('check', 'package.json', 'v').stderr, /uncommitted/);
});

test('stable releases reject malformed versions and ignore unrelated tag namespaces', t => {
  const repo = repository(t);
  repo.git('tag', 'other-v99.0.0');
  assert.equal(repo.run('check', 'package.json', 'markdown-v').status, 0);
  for (const value of ['01.2.3', '1.2', '1.2.3-rc.1', '1.2.3+build', 'garbage']) {
    repo.setVersion(value);
    repo.commit();
    assert.match(repo.run('check', 'package.json', 'v').stderr, /stable SemVer/);
  }
});
