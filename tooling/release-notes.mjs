// Drafts the release notes („Co je nového“) of a release from the commits since the previous tag. Run from the repository root:
//   node tooling/release-notes.mjs [version]        (default: the version in version.json)
// It writes src/Atlas.Web/release-notes/<version>.md only when the file does not exist yet. The draft carries a marker comment
// that tooling/release-notes.test.mjs refuses on main: rewrite it for users (Czech, short bullets, no internals), then drop it.
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const notesDirectory = 'src/Atlas.Web/release-notes';
export const draftMarker = '<!-- koncept';
const stable = /^\d+\.\d+\.\d+$/;

/** feat → Nové, fix and perf → Opravy a Vylepšení; the scope and the rest of the history (docs, test, chore, merges) are left out. */
export function draftNotes(version, date, subjects, since) {
  const section = (title, types) => {
    const lines = subjects.map(subject => /^(\w+)(?:\([^)]*\))?!?: (.+)$/.exec(subject)).filter(match => match && types.includes(match[1])).map(match => `- ${match[2]}`);
    return lines.length ? `\n### ${title}\n${lines.join('\n')}\n` : '';
  };
  return `*${date.getDate()}. ${date.getMonth() + 1}. ${date.getFullYear()}*\n\n`
    + `${draftMarker} Atlas ${version} z commitů ${since}..HEAD: přepište pro uživatele česky (co je nového, co se zlepšilo, co se opravilo), `
    + `5–12 krátkých bodů bez interních názvů a zákaznických dat, pak tento komentář smažte. -->\n`
    + section('Nové', ['feat']) + section('Vylepšení', ['perf']) + section('Opravy', ['fix']);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  try {
    const git = (...args) => execFileSync('git', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true }).trim();
    const version = process.argv[2] ?? JSON.parse(readFileSync('version.json', 'utf8')).version;
    if (!stable.test(version)) throw new Error('Expected a stable version major.minor.patch');
    const path = `${notesDirectory}/${version}.md`;
    if (existsSync(path)) throw new Error(`${path} exists already; edit it instead`);
    const since = git('tag', '--list', 'v*', '--sort=-v:refname').split('\n').find(tag => stable.test(tag.slice(1)) && tag !== `v${version}`);
    if (!since) throw new Error('No previous release tag v<version>');
    const subjects = git('log', '--no-merges', '--format=%s', `${since}..HEAD`).split('\n').filter(Boolean);
    writeFileSync(path, draftNotes(version, new Date(), subjects, since));
    console.log(path);
  } catch (error) {
    console.error('release-notes: ' + error.message);
    process.exitCode = 1;
  }
}
