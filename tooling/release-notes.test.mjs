// Every release has its notes („Co je nového“, src/Atlas.Web/release-notes/<version>.md): main fails without them. A minor or major
// release has a codename, a wireframe mark (tooling/brand) and highlights for the splash after the update.
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { draftMarker, draftNotes, notesDirectory } from './release-notes.mjs';

const root = new URL('../', import.meta.url);
const read = path => readFileSync(new URL(path, root), 'utf8').replace(/\r\n/g, '\n');
const brand = 'src/Atlas.Web/src/assets/brand';
/** The header lines (`codename: …`, `mark: …`) and the rest, as whatsNew/releaseNotes.ts reads them. */
const header = text => {
  const fields = {};
  for (let match; (match = /^(codename|mark|cover): *(.+?) *\n/.exec(text));) { fields[match[1]] = match[2]; text = text.slice(match[0].length); }
  return { ...fields, body: text.trimStart() };
};
const newer = (a, b) => { const x = a.split('.').map(Number), y = b.split('.').map(Number); return x[0] - y[0] || x[1] - y[1] || x[2] - y[2]; };

test('the release in version.json, and a stable build, has finished notes', () => {
  const releases = new Set([JSON.parse(read('version.json')).version]);
  const props = read('Directory.Build.props');
  // On main the build is the release (no VersionSuffix); on develop it is the next -dev version, which needs none yet.
  if (!/<VersionSuffix>\s*\S/.test(props)) releases.add(/<VersionPrefix>(.+)<\/VersionPrefix>/.exec(props)[1].trim());
  for (const version of releases) {
    const path = `${notesDirectory}/${version}.md`;
    assert.ok(existsSync(new URL(path, root)), `${path} is missing: draft it with node tooling/release-notes.mjs ${version}`);
    const text = read(path);
    assert.ok(!text.includes(draftMarker), `${path} is still the draft from the commits; rewrite it for users`);
    assert.match(header(text).body, /^\*\d{1,2}\. \d{1,2}\. \d{4}\*/, `${path} starts with the release date (*2. 11. 2026*)`);
  }
});

test('a minor or major release since 3.5 has a codename, a wireframe mark and highlights; a hotfix needs none', async () => {
  const versions = readdirSync(new URL(notesDirectory + '/', root)).map(file => file.replace(/\.md$/, '')).filter(version => /^\d+\.\d+\.\d+$/.test(version));
  for (const version of versions.filter(version => version.endsWith('.0') && newer(version, '3.5.0') >= 0)) {
    const path = `${notesDirectory}/${version}.md`, { codename, mark, body } = header(read(path));
    assert.ok(codename && mark, `${path} opens with "codename: …" and "mark: …" (AGENTS.md, Branches and releases)`);
    assert.match(body, /^### Hlavně\n[^-\n].*\n(- \*\*.+\n){1,12}/m, `${path} has "### Hlavně": one sentence, then up to six (a major release: twelve) "- **Název**: popis" highlights`);
  }
  for (const version of versions) {
    const { mark } = header(read(`${notesDirectory}/${version}.md`));
    if (!mark) continue;
    const svgPath = `${brand}/${mark}.svg`;
    assert.ok(existsSync(new URL(svgPath, root)), `${svgPath} is missing: generate it with tooling/brand (wireframe-mark.mjs)`);
    const svg = read(svgPath);
    // Inside the SVG only transform, opacity and stroke-dashoffset animate, and reduced motion shows the final state.
    for (const [, frames] of svg.matchAll(/@keyframes [\w-]+\{((?:[^{}]*\{[^}]*\})*)\}/g)) {
      for (const [, property] of frames.matchAll(/([a-z-]+):/g)) assert.ok(['transform', 'opacity', 'stroke-dashoffset'].includes(property), `${svgPath} animates ${property}`);
    }
    assert.match(svg, /@media\(prefers-reduced-motion:reduce\)\{[^}]*animation:none/, `${svgPath} stops for reduced motion`);
    assert.match(svg, /\[data-still\] [^{]*\{animation:none/, `${svgPath} stops under [data-still] (Atlas's own „Omezit animace“)`);
    const generator = new URL(`brand/${mark}-mark.mjs`, import.meta.url);
    if (existsSync(generator)) assert.equal(svg, (await import(generator)).mark(), `${svgPath} is not what tooling/brand/${mark}-mark.mjs writes: run it`);
  }
});

test('CHANGELOG.md: a named version is "## X.Y · Codename" and its patch releases "### X.Y.Z — date" sections beneath it', () => {
  const changelog = read('CHANGELOG.md');
  let line;
  for (const [, level, version] of changelog.matchAll(/^(##|###) (\d+\.\d+(?:\.\d+)?)\b/gm)) {
    const own = version.split('.').slice(0, 2).join('.');
    if (level === '##') assert.equal((line = version), own, `"## ${version}": a release is "## ${own} · Codename" with its patches beneath as "### ${own}.N — date"`);
    else assert.equal(line, own, `"### ${version}" stands under "## ${line}"; move it under "## ${own}"`);
  }
  const released = JSON.parse(read('version.json')).version, own = released.split('.').slice(0, 2).join('.');
  assert.match(changelog, released.endsWith('.0') ? new RegExp(`^## ${own}\\b`, 'm') : new RegExp(`^### ${released} — `, 'm'), `CHANGELOG.md has no section for ${released}`);
});

test('the draft keeps the features, improvements and fixes and leaves the rest of the history out', () => {
  const text = draftNotes('3.6.0', new Date(2026, 10, 9), [
    'feat(projector): sets in the catalog', 'fix: the dock stays at the edge', 'perf(editor): fewer layouts',
    'docs: plan', 'test(perf): budget', 'chore: open 3.6.0-dev'], 'v3.5.0');
  assert.match(text, /^\*9\. 11\. 2026\*/);
  assert.ok(text.includes(draftMarker) && text.includes('v3.5.0..HEAD'));
  assert.match(text, /### Nové\n- sets in the catalog\n/);
  assert.match(text, /### Vylepšení\n- fewer layouts\n/);
  assert.match(text, /### Opravy\n- the dock stays at the edge\n/);
  assert.doesNotMatch(text, /plan|budget|open 3\.6\.0/);
});
