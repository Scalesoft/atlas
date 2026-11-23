/**
 * The release notes, one Markdown file per release in `src/Atlas.Web/release-notes/<version>.md`, bundled with the page that shows
 * them (WhatsNewPage) and nowhere else. Every release has one: `tooling/release-notes.test.mjs` fails main without it, and
 * `tooling/release-notes.mjs` drafts it from the commits since the previous tag.
 *
 * From 4.0 the notes are bilingual: `<version>.md` stays the Czech source, `<version>.en.md` is its English version (same header
 * lines, `### Highlights` for `### Hlavně`, the date written in English). A page in English reads the English file when it exists and
 * the Czech one otherwise; the English files are lazy chunks, fetched only for English (the eager set holds the Czech notes only).
 *
 * A minor or major release opens its file with its codename and wireframe mark (`assets/brand/<mark>.svg`, tooling/brand) and has a
 * `### Hlavně` section: one sentence and up to six highlights (a major release x.0.0: twelve) for the splash after the update (`- **Název** `126 ms`: popis`, the
 * code span optional). The section stays out of the full changelog, whose Nové says it all. Its patch releases (x.y.1 …) have no
 * codename: the changelog shows them beneath it, smaller (groupReleases), never as releases of their own.
 *
 *   codename: Performant Pear
 *   mark: pear
 *   cover: secret      (optional: the splash covers the mark with the invisible ink until scratched away, ReleaseCover)
 *   demos: pointers qr (optional: a live demo in the splash card of each highlight in turn, `-` for none; whatsNew/releaseDemos.ts)
 *
 *   *2. 11. 2026*
 *
 *   ### Hlavně
 *   Stránky zakázek jsou svižné …
 *   - **Hledat ve stránce** `Ctrl F`: výsledky s okolím …
 */
import { language, locale, waitForTexts } from "../i18n";
import { brandMarks } from "./brandMarks";
import { releaseDemos } from "./releaseDemos";
import { releaseLine } from "./notice";

export interface Highlight {
  title: string; text: string; key?: string;
  /** A live demo of it in the splash card (components/release/demos/<demo>.vue), from the notes' `demos:` line. */
  demo?: string;
}
export interface ReleaseNote {
  version: string;
  /** The Markdown shown in the changelog: without the header lines and without `### Hlavně`. */
  text: string;
  codename?: string;
  /** The mark's name (assets/brand/<mark>.svg, whatsNew/brandMarks.ts); undefined when no such mark is bundled. */
  mark?: string;
  /** `secret`: the splash shows the mark covered until the account scratches it away (a fundamental change, 3.6). */
  cover?: string;
  /** The demos of the highlights in turn (`demos: pointers - qr`); only those bundled are kept on the highlights. */
  demos?: string;
  /** „2. 11. 2026“, when the notes open with a date. */
  date?: string;
  /** From `### Hlavně`. */
  lead?: string;
  highlights: Highlight[];
}

/** Newest first, by number („3.10.0“ after „3.9.0“). */
export function compareReleases(left: string, right: string): number {
  const a = left.split(".").map(Number), b = right.split(".").map(Number);
  for (let i = 0; i < 3; i++) if (a[i] !== b[i]) return (b[i] ?? 0) - (a[i] ?? 0);
  return 0;
}

/** A named version (x.y.0: codename, mark, highlights) with its patch releases (x.y.1 …, newest first): one entry of the changelog. */
export interface ReleaseGroup { release: ReleaseNote; patches: ReleaseNote[] }

/** The notes (newest first) by named version; a version whose x.y.0 notes are missing is headed by its oldest patch. */
export function groupReleases(notes: readonly ReleaseNote[]): ReleaseGroup[] {
  const lines = new Map<string, ReleaseNote[]>();
  for (const note of notes) lines.set(releaseLine(note.version), [...lines.get(releaseLine(note.version)) ?? [], note]);
  return [...lines.values()].map(line => ({ release: line.at(-1)!, patches: line.slice(0, -1) }));
}

const HEADER = /^(codename|mark|cover|demos): *(.+?) *\r?\n/;
const MAIN = /^### (?:Hlavně|Highlights)[ \t]*\r?\n([\s\S]*?)(?=^### |(?![\s\S]))/m;
const HIGHLIGHT = /^- \*\*(.+?):?\*\*(?: `([^`]+)`)?:? *(.+)$/;

/** A major release (x.0.0): its splash is larger, with a halo and up to twelve highlights (ReleaseSplash). */
export const isMajorRelease = (version: string) => /^\d+\.0\.0$/.test(version);

export function parseReleaseNote(version: string, raw: string, marks: Record<string, unknown> = brandMarks, demos: Record<string, unknown> = releaseDemos): ReleaseNote {
  const note: ReleaseNote = { version, text: raw, highlights: [] };
  for (let match; (match = HEADER.exec(note.text));) {
    note[match[1] as "codename" | "mark" | "cover" | "demos"] = match[2];
    note.text = note.text.slice(match[0].length);
  }
  note.text = note.text.replace(/^\s+/, "");
  if (note.mark && !(note.mark in marks)) delete note.mark;
  note.date = /^\*(\d{1,2}\. \d{1,2}\. \d{4})\*/.exec(note.text)?.[1];
  const main = MAIN.exec(note.text);
  if (main) {
    const lines = main[1]!.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
    note.lead = lines.filter(line => !line.startsWith("- ")).join(" ") || undefined;
    note.highlights = lines.flatMap(line => {
      const item = HIGHLIGHT.exec(line);
      return item ? [{ title: item[1]!, text: item[3]!, ...(item[2] ? { key: item[2] } : {}) }] : [];
    }).slice(0, isMajorRelease(version) ? 12 : 6);
    note.demos?.split(/\s+/).forEach((demo, index) => { if (demo in demos && note.highlights[index]) note.highlights[index]!.demo = demo; });
    note.text = note.text.replace(main[0], "");
  }
  return note;
}

export function releaseNotesFrom(files: Record<string, string>, marks: Record<string, unknown> = brandMarks): ReleaseNote[] {
  return Object.entries(files)
    .map(([path, text]) => parseReleaseNote(path.split("/").at(-1)!.replace(/\.md$/, ""), text, marks))
    .filter(note => /^\d+\.\d+\.\d+$/.test(note.version))
    .sort((left, right) => compareReleases(left.version, right.version));
}

/** The plain first sentence of each bullet of a note: what a patch says in a strip or a list (Markdown marks dropped). */
export const points = (note: ReleaseNote) => [...note.text.matchAll(/^- (.+)$/gm)]
  .map(bullet => bullet[1]!.replace(/\*\*|`/g, "").replace(/^(.+?[.!?])\s.*$/, "$1").replace(/[.!?]$/, ""));
export const firstPoint = (note: ReleaseNote): string | undefined => points(note)[0];

/**
 * What an account has not seen yet: the releases after `since` up to `current` (newest first), the newest named one among them
 * (codename and mark, the splash's motif) and the others. When they are only patches of the named version the account knows,
 * `patched` is that version: the update is a small one of it, not a new release.
 */
export function unseenReleases(notes: readonly ReleaseNote[], since: string | undefined, current: string | undefined) {
  const unseen = since ? notes.filter(note => compareReleases(note.version, since) < 0 && (!current || compareReleases(note.version, current) >= 0)) : [];
  const featured = unseen.find(note => note.codename && note.mark);
  const line = since && releaseLine(since);
  const patched = !featured && unseen.length > 0 && unseen.every(note => releaseLine(note.version) === line)
    ? notes.find(note => note.version === `${line}.0`) : undefined;
  return { unseen, featured, patched, others: unseen.filter(note => note !== featured) };
}

export const releaseNotes: ReleaseNote[] = releaseNotesFrom(
  import.meta.glob<string>(["../../release-notes/*.md", "!../../release-notes/*.en.md"], { query: "?raw", import: "default", eager: true }),
);

const englishNotes = import.meta.glob<string>("../../release-notes/*.en.md", { query: "?raw", import: "default" });

/** The date of the Czech note („2. 11. 2026“) as the page language writes it. */
const dateIn = (date: string | undefined) => {
  const [day, month, year] = date?.match(/\d+/g)?.map(Number) ?? [];
  return year ? new Date(year, month! - 1, day).toLocaleDateString(locale, { dateStyle: "medium" }) : date;
};

// The English note replaces the Czech one in place, before anything that waits for texts renders (i18n `waitForTexts`).
if (language !== "cs") waitForTexts(Promise.all(releaseNotes.map(async note => {
  const load = englishNotes[`../../release-notes/${note.version}.en.md`];
  if (load) Object.assign(note, parseReleaseNote(note.version, await load()), { date: dateIn(note.date) });
})));
