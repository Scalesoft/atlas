import type { Editor } from "@tiptap/core";
import type { Node as PMNode } from "@tiptap/pm/model";
import { shallowRef, watch, type Component, type ShallowRef } from "vue";
import { playLevelChange } from "./overviewMotion";

/** A choice of how much of a kind's components shows on the page (NONE, S, M, L); `short` its letter on the slider (default: the id in capitals). */
export interface OverviewLevel { readonly id: string; readonly label: string; readonly icon?: string; readonly short?: string }

/** A kind's action bound to what its overview collected, a button in the card's foot (OverviewActions.vue). */
export interface OverviewCardAction { readonly id: string; readonly label: string; readonly icon?: string; run(): void | Promise<void> }

/** A bulk action of an overview: a button of its card, run over the components of the kind it collected (a page's or a board's). */
export interface OverviewAction<Item = unknown> {
  readonly id: string;
  readonly label: string;
  readonly icon?: string;
  /** Only in a page's editor (it needs the page: „Poskládat tabuli“); a board's overview leaves it out. */
  readonly pageOnly?: boolean;
  /** A page-only action also offered in a page that is read, while this says so („Tabule nasazení“ once the board is made). */
  readable?(editor: Editor): boolean;
  run(context: { editor?: Editor; items: readonly Item[] }): void | Promise<void>;
}

/** What a page editor gives an overview's card: the page it is on. The projector's host (ProjectorHost) is one. */
export interface OverviewHost { pageId(): string | undefined }

/** A stored node as JSON: a page's document, a block a board holds. */
export interface OverviewJson { readonly type?: string; readonly text?: string; readonly attrs?: Readonly<Record<string, unknown>>; readonly content?: readonly OverviewJson[] }
/** What a kind collects from: a page's document in an editor, or any stored document (a read page, a board as one document). */
export type OverviewDocument = PMNode | OverviewJson;
/** The plain text of a stored node: its text nodes, in order. */
export const jsonText = (node: OverviewJson): string => node.text ?? (node.content ?? []).map(jsonText).join("");
/** Calls `visit` with the type and attributes of every node of the document, in document order (a page's editor or stored JSON). */
export function eachNode(document: OverviewDocument, visit: (type: string | undefined, attrs: Readonly<Record<string, unknown>> | undefined) => void) {
  if ("descendants" in document) document.descendants(node => { visit(node.type.name, node.attrs); });
  else { const walk = (node: OverviewJson) => { visit(node.type, node.attrs); node.content?.forEach(walk); }; walk(document); }
}
/** What an overview reads of a board's item (BoardItemDto's shape). */
export interface OverviewBoardItem {
  readonly kind: string;
  readonly text?: string | null;
  readonly props?: Readonly<Record<string, unknown>> | null;
  readonly targetResourceId?: string | null;
  /** Soft-deleted (kept for an undo): not on the board. */
  readonly deleted?: boolean;
}

/**
 * One kind of the overview (plan 3.4 R6): the block `overview` with `kind` set to {@link kind} shows {@link card} in a page's editor and
 * {@link readCard} on a board, and the reader's level of the kind's components is one state per page or board and kind
 * (`overviewLevel`), kept in this browser and set on the root (the editor's, the read view's, the board's canvas) as
 * `data-overview-<kind>`. A page or a board may hold several overviews of a kind: they all read and set that one state.
 * A new kind is one `registerOverviewKind` call at module load (requirementsPlugin.ts registers `requirements`):
 * - `kind`       the attribute value, lowercase (`requirements`, `contacts`, `issues`);
 * - `label`      the overview's name in Czech, as the Markdown writes it (`**label:** …`, the server the same): content, never translated;
 * - `title`      optional: the name as the tool shows it, in its language (the slash menu „Přehled: …“, the loading label); `label` when absent;
 * - `collect`    the kind's components of a document, what the card shows and the actions work on: a page's, or a board's as one
 *                document (`boardDocument`: its blocks, then `boardNodes`); one implementation for both;
 * - `boardNodes` optional: the nodes a board's other items stand for (a live contact card, a product card's requirements);
 * - `boardReads` optional: the board's items the kind reads (default all), so a change of another (a note) collects nothing again;
 * - `card`       the Vue card in a page's editor, given `editor`, `host` (the overview node's `host` option) and `actions`;
 * - `readCard`   the card over collected `items`, the level's `scope` (a page or board id) and `actions`, where no editor holds the
 *                overview (a read page, a board). Every card has one layout: its name with the level slider on the right in its head
 *                (OverviewLevelSlider.vue), its content, the `actions` in its foot (OverviewActions.vue);
 * - `text`       optional one line for Markdown (`**label:** text`); the server writes the same line. Without it the overview is
 *                written as the dialect's fence ```a-overview (`kind: …`), which the editor and the server read back;
 * - `levels`     optional levels in their order (less → more); `defaultLevel` the one a page starts at (else the first). A change
 *                of level plays the shared motion over the elements `itemSelector` finds under the root (default
 *                `[data-overview-item]`, overviewMotion.ts); `animate` replaces it only for a kind that needs its own;
 * - `actions`    optional bulk actions, buttons under the card.
 * An overview of a kind not registered here shows a quiet placeholder.
 */
export interface OverviewKind<Item = unknown> {
  readonly kind: string;
  readonly label: string;
  readonly title?: string;
  collect(document: OverviewDocument): readonly Item[];
  boardNodes?(items: readonly OverviewBoardItem[]): Promise<readonly OverviewJson[]>;
  boardReads?(item: OverviewBoardItem): boolean;
  readonly card: Component;
  readonly readCard: Component;
  text?(document: PMNode): string;
  readonly levels?: readonly OverviewLevel[];
  readonly defaultLevel?: string;
  readonly itemSelector?: string;
  animate?(root: HTMLElement, change: () => void): void;
  readonly actions?: readonly OverviewAction<Item>[];
}

const kinds = new Map<string, OverviewKind>();

/** Registers a kind of overview; registering the same kind again replaces it (a hot reload). */
export function registerOverviewKind<Item>(kind: OverviewKind<Item>) { kinds.set(kind.kind, kind as OverviewKind); }
export const overviewKind = (kind: string): OverviewKind | undefined => kinds.get(kind);
export const overviewKinds = (): readonly OverviewKind[] => [...kinds.values()];

/** A board as one document for the kind's `collect`: the nodes of its blocks, then what its other items stand for (`boardNodes`). */
export async function boardDocument(kind: OverviewKind, all: readonly OverviewBoardItem[]): Promise<OverviewJson> {
  const items = all.filter(item => !item.deleted);
  const blocks = items.filter(item => item.kind === "block" && item.props?.block).map(item => item.props!.block as OverviewJson);
  return { type: "doc", content: [...blocks, ...(await kind.boardNodes?.(items) ?? [])] };
}

/** The change of a kind's level played under `root`: the kind's own motion, else the shared one (overviewMotion.ts). */
export function playKindLevel(kind: OverviewKind, root: HTMLElement, change: () => void) {
  if (kind.animate) kind.animate(root, change); else playLevelChange(root, change, kind.itemSelector);
}

/** Atlas 3.3's deployment summary block, read as the requirements overview until the content repair renames it (legacyDocument.ts). */
export const legacyOverviews: Readonly<Record<string, string>> = { requirementSummary: "requirements" };
/** The kind of an overview node (stored JSON or ProseMirror), undefined for any other node. */
export function kindOfNode(type: string | undefined, attrs: Readonly<Record<string, unknown>> | undefined): string | undefined {
  return type === "overview" ? String(attrs?.kind ?? "") : type ? legacyOverviews[type] : undefined;
}

// The reader's level of a kind on a page or a board (its id), kept in this browser: never the content.
// The requirements' choice moved to an entry of its own with the 3.5 levels (plan 3.5 B): there a 3.4 „s“ (the table alone) is no 3.5 one.
const storageKey = (kind: string, pageId: string) => kind === "requirements" ? `atlas.overview.requirements.v2.${pageId}` : `atlas.overview.${kind}.${pageId}`;
const levels = new Map<string, ShallowRef<string>>();
const defaultLevel = (kind: string) => overviewKind(kind)?.defaultLevel ?? overviewKind(kind)?.levels?.[0]?.id ?? "";
/**
 * An older requirements choice as the 3.6 level (model §2: NONE, S, M): Atlas 3.3's densities (each → M) and the 3.4 levels (S, the
 * table alone → NONE; M, L, XL → M). A 3.5 L or XL in the newer entry is no level any more and reads as the default, M.
 */
const renamedLevels: Readonly<Record<string, string>> = { compact: "m", row: "m", full: "m", s: "none", m: "m", l: "m", xl: "m" };

function stored(kind: string, pageId: string): string {
  try {
    let value = localStorage.getItem(storageKey(kind, pageId));
    // An older requirements choice moves to the 3.5 entry, as its 3.5 level: the 3.4 entry's, else Atlas 3.3's density kept with the
    // sort and filter (requirementViewMode.ts).
    if (value === null && kind === "requirements") {
      const older = `atlas.overview.requirements.${pageId}`, legacyKey = `atlas.requirementView.${pageId}`;
      value = localStorage.getItem(older);
      localStorage.removeItem(older);
      if (value === null) {
        const legacy = JSON.parse(localStorage.getItem(legacyKey) ?? "null") as { density?: string } | null;
        value = legacy?.density ?? null;
        if (value) { delete legacy!.density; localStorage.setItem(legacyKey, JSON.stringify(legacy)); }
      }
      if (value) { value = renamedLevels[value] ?? value; localStorage.setItem(storageKey(kind, pageId), value); }
    }
    return overviewKind(kind)?.levels?.some(level => level.id === value) ? value! : defaultLevel(kind);
  } catch { return defaultLevel(kind); }
}

/**
 * The level of the kind on a page or board (`pageId`: its id): one state shared by its editor or canvas, its read view and the switch
 * of every overview of the kind there, in this browser tab.
 */
export function overviewLevel(kind: string, pageId = ""): ShallowRef<string> {
  const key = storageKey(kind, pageId);
  let found = levels.get(key);
  if (!found) levels.set(key, found = shallowRef(stored(kind, pageId)));
  return found;
}

/** Sets the one state; the same level again changes nothing (no motion, no storage). */
export function setOverviewLevel(kind: string, pageId: string, level: string) {
  const current = overviewLevel(kind, pageId);
  if (current.value === level) return;
  current.value = level;
  try {
    if (level === defaultLevel(kind)) localStorage.removeItem(storageKey(kind, pageId));
    else localStorage.setItem(storageKey(kind, pageId), level);
  } catch { /* the view still changes, only for this visit */ }
}

/**
 * The root's attributes of the levels: `data-overview-<kind>` for every kind with levels that `shown` accepts (all by default); none
 * without a page or board (an editor of one board block keeps its full look).
 */
export function overviewLevelAttributes(pageId = "", shown: (kind: string) => boolean = () => true): Record<string, string> {
  const attributes: Record<string, string> = {};
  for (const kind of kinds.values()) {
    const level = pageId && kind.levels?.length && shown(kind.kind) ? overviewLevel(kind.kind, pageId).value : "";
    if (level) attributes[`data-overview-${kind.kind}`] = level;
  }
  return attributes;
}

/**
 * The same attributes on a root without an editor (a read view's container, a board's canvas), following the one state of each kind:
 * one watcher per kind and root, so a change sets the attribute and plays its motion once, however many overviews show the switch.
 * `shown` limits the kinds (a board: those it holds an overview of). Returns the stop, which takes the attributes off. It touches only
 * the attributes it set itself: another watcher on the same root (a read page's requirements and its other overviews) keeps its own.
 */
export function watchOverviewLevels(container: HTMLElement, pageId = "", shown?: (kind: string) => boolean): () => void {
  const owned = new Set<string>();
  const stops = [...kinds.values()].map(kind => {
    const name = `data-overview-${kind.kind}`;
    return watch(() => overviewLevelAttributes(pageId, shown)[name], (level, before) => {
      const set = () => { if (level) { container.setAttribute(name, level); owned.add(name); } else if (owned.delete(name)) container.removeAttribute(name); };
      if (before && level && container.isConnected) playKindLevel(kind, container, set); else set();
    }, { immediate: true });
  });
  return () => { stops.forEach(stop => stop()); owned.forEach(name => container.removeAttribute(name)); owned.clear(); };
}
