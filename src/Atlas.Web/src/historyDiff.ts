import t from "./i18n/shell-more";
export interface HistoryDiffPart { kind: "same" | "added" | "removed"; text: string }
export interface HistoryTextDiff { parts: HistoryDiffPart[]; added: number; removed: number; coarse: boolean }

/** A bounded word comparison; the fallback still preserves both exact inputs. */
export function compareHistoryText(before: string, after: string): HistoryTextDiff {
  // Anchor complete lines first, so repeated spaces cannot align unrelated
  // paragraphs. Refine only each changed span to words.
  const lines = (text: string) => text.match(/[^\n]*\n|[^\n]+$/g) ?? [];
  const outer = compareTokens(lines(before), lines(after));
  const tokens = (text: string) => text.match(/[\p{L}\p{N}_]+|\s+|[^\p{L}\p{N}_\s]/gu) ?? [];
  const parts: HistoryDiffPart[] = [];
  let coarse = outer.coarse;
  for (let index = 0; index < outer.parts.length;) {
    const part = outer.parts[index]!;
    if (part.kind === "same") { parts.push(part); index++; continue; }
    let removed = "", added = "";
    while (index < outer.parts.length && outer.parts[index]!.kind !== "same") {
      const changed = outer.parts[index++]!;
      if (changed.kind === "removed") removed += changed.text;
      else added += changed.text;
    }
    const inner = compareTokens(tokens(removed), tokens(added));
    parts.push(...inner.parts); coarse ||= inner.coarse;
  }
  const count = (kind: string) => parts.filter(p => p.kind === kind).reduce((sum, p) => sum + (p.text.match(/[\p{L}\p{N}_]+/gu)?.length ?? 0), 0);
  return { parts, added: count("added"), removed: count("removed"), coarse };
}

function compareTokens(a: string[], b: string[]): { parts: HistoryDiffPart[]; coarse: boolean } {
  let start = 0, end = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start++;
  while (end < a.length - start && end < b.length - start && a[a.length - 1 - end] === b[b.length - 1 - end]) end++;
  const left = a.slice(start, a.length - end), right = b.slice(start, b.length - end);
  const parts: HistoryDiffPart[] = [];
  function append(kind: HistoryDiffPart["kind"], text: string) {
    if (!text) return;
    if (parts.at(-1)?.kind === kind) parts[parts.length - 1]!.text += text;
    else parts.push({ kind, text });
  }
  append("same", a.slice(0, start).join(""));
  // lazy: cap the LCS matrix at 1M cells. Large rewrites use a labelled whole-span
  // replacement; a worker-based Myers diff can replace this if large docs need finer detail.
  const coarse = (left.length + 1) * (right.length + 1) > 1_000_000;
  if (coarse) {
    append("removed", left.join("")); append("added", right.join(""));
  } else {
    const width = right.length + 1;
    const lengths = new Uint32Array((left.length + 1) * width);
    for (let i = left.length - 1; i >= 0; i--)
      for (let j = right.length - 1; j >= 0; j--)
        lengths[i * width + j] = left[i] === right[j]
          ? lengths[(i + 1) * width + j + 1]! + left[i]!.length
          : Math.max(lengths[(i + 1) * width + j]!, lengths[i * width + j + 1]!);
    let i = 0, j = 0;
    while (i < left.length || j < right.length) {
      if (i < left.length && j < right.length && left[i] === right[j]) { append("same", left[i++]!); j++; }
      else if (i < left.length && (j === right.length || lengths[(i + 1) * width + j]! >= lengths[i * width + j + 1]!)) append("removed", left[i++]!);
      else append("added", right[j++]!);
    }
  }
  append("same", end ? a.slice(-end).join("") : "");
  return { parts, coarse };
}

/** One embedded OpenAPI definition that differs between two revisions. */
export interface OpenApiChange {
  kind: "added" | "removed" | "source" | "settings";
  /** `info.title` of the definition, or the filename when the source cannot be read. */
  title: string;
  filename: string;
  /** Czech description of the change for the approver. */
  label: string;
}
interface OpenApiBlock { filename: string; source: string; mode: string; title: string }

const openApiLabels: Record<OpenApiChange["kind"], string> = {
  get added() { return t.diff.added; }, get removed() { return t.diff.removed; }, get source() { return t.diff.sourceChanged; }, get settings() { return t.diff.settingsChanged; },
};

function openApiBlocks(contentJson: string | null | undefined): OpenApiBlock[] {
  if (!contentJson) return [];
  let root: unknown;
  try { root = JSON.parse(contentJson); } catch { return []; }
  const blocks: OpenApiBlock[] = [];
  const visit = (node: unknown) => {
    if (!node || typeof node !== "object") return;
    const { type, attrs, content } = node as { type?: unknown; attrs?: { data?: unknown }; content?: unknown };
    if (type === "openapi") {
      const data = (attrs?.data ?? {}) as { filename?: unknown; source?: unknown; mode?: unknown };
      const filename = typeof data.filename === "string" ? data.filename : "openapi";
      const source = typeof data.source === "string" ? data.source : "";
      blocks.push({ filename, source, mode: typeof data.mode === "string" ? data.mode : "interactive", title: openApiTitle(source) ?? filename });
    }
    if (Array.isArray(content)) content.forEach(visit);
  };
  visit((root as { document?: unknown } | null)?.document);
  return blocks;
}

/** `info.title` read with a tolerant line scan: history must describe even an invalid definition. */
function openApiTitle(source: string): string | undefined {
  const json = source.trimStart().startsWith("{");
  if (json) {
    try { const title = (JSON.parse(source) as { info?: { title?: unknown } }).info?.title; return typeof title === "string" && title.trim() ? title.trim() : undefined; }
    catch { return undefined; }
  }
  const lines = source.split(/\r?\n/);
  const info = lines.findIndex(line => /^info\s*:\s*$/.test(line));
  if (info < 0) return undefined;
  for (const line of lines.slice(info + 1)) {
    if (/^\S/.test(line)) break;
    const match = line.match(/^\s+title\s*:\s*(.+?)\s*$/);
    if (match) return match[1]!.replace(/^(['"])(.*)\1$/, "$2").trim() || undefined;
  }
  return undefined;
}

/**
 * Every OpenAPI block whose definition changed between two revisions. The page's Markdown projection
 * only carries a summary line, so without this a source-only change looks like "nothing changed" to the
 * space approver. Blocks carry no identity: pair them by filename, then an unchanged source (a rename),
 * then the API title; anything left over is reported as added or removed.
 */
export function openApiChanges(previousJson: string | null | undefined, nextJson: string | null | undefined): OpenApiChange[] {
  const before = openApiBlocks(previousJson), after = openApiBlocks(nextJson);
  const partner = new Map<number, number>(), taken = new Set<number>();
  for (const same of [(a: OpenApiBlock, b: OpenApiBlock) => a.filename === b.filename,
    (a: OpenApiBlock, b: OpenApiBlock) => a.source === b.source,
    (a: OpenApiBlock, b: OpenApiBlock) => a.title === b.title]) {
    after.forEach((block, index) => {
      if (partner.has(index)) return;
      const match = before.findIndex((candidate, position) => !taken.has(position) && same(candidate, block));
      if (match >= 0) { partner.set(index, match); taken.add(match); }
    });
  }
  const pairs: [OpenApiBlock | undefined, OpenApiBlock | undefined][] = after.map((block, index) =>
    [partner.has(index) ? before[partner.get(index)!] : undefined, block]);
  before.forEach((block, index) => { if (!taken.has(index)) pairs.push([block, undefined]); });
  const changes: OpenApiChange[] = [];
  for (const [previous, next] of pairs) {
    const kind: OpenApiChange["kind"] | undefined = !previous ? "added" : !next ? "removed"
      : previous.source !== next.source ? "source"
        : previous.filename !== next.filename || previous.mode !== next.mode ? "settings" : undefined;
    if (!kind) continue;
    const block = (next ?? previous)!;
    changes.push({ kind, title: block.title, filename: block.filename, label: openApiLabels[kind] });
  }
  return changes;
}

let worker: Worker | undefined, nextId = 0;
const waiting = new Map<number, { before: string; after: string; resolve: (diff: HistoryTextDiff) => void }>();
/** The same comparison off the main thread (a large rewrite fills the 1M-cell matrix); without Worker support, or after a worker failure, it runs inline. */
export function compareHistoryTextAsync(before: string, after: string): Promise<HistoryTextDiff> {
  if (typeof Worker === "undefined") return Promise.resolve(compareHistoryText(before, after));
  try {
    if (!worker) {
      worker = new Worker(new URL("./history/diffWorker.ts", import.meta.url), { type: "module" });
      worker.onmessage = (event: MessageEvent<{ id: number; diff: HistoryTextDiff }>) => { waiting.get(event.data.id)?.resolve(event.data.diff); waiting.delete(event.data.id); };
      worker.onerror = () => {
        worker = undefined;
        for (const job of waiting.values()) job.resolve(compareHistoryText(job.before, job.after));
        waiting.clear();
      };
    }
    const id = nextId++;
    return new Promise(resolve => { waiting.set(id, { before, after, resolve }); worker!.postMessage({ id, before, after }); });
  } catch { return Promise.resolve(compareHistoryText(before, after)); }
}
