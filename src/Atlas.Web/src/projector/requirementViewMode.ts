import type { Node as PMNode } from "@tiptap/pm/model";
import { NodeSelection, Plugin, PluginKey, type EditorState, type Transaction } from "@tiptap/pm/state";
import { Decoration, DecorationSet, type EditorView } from "@tiptap/pm/view";
import { readStored, removeStored, writeStored } from "../lib/browserStorage";
import { effectScope, shallowRef, watch, type ShallowRef } from "vue";
import { animateAtlas, motionMs, travelTiming } from "../motion/aiMotion";
import { overviewLevel, type OverviewLevel } from "../overview/overviewKinds";
import { overviewPositions } from "../overview/overviewNode";
import { foldAll } from "../editor/foldState";
import { headingKey } from "../editor/headingFold";
import { reshapesSections } from "./requirementOverview";
import { useToast } from "../composables/useToast";
import t from "../i18n/projector";
import { isRemote } from "./remoteChange";
import { MARKDOWN_BLOCK_REMOVAL_META } from "@atlas/markdown/core";
import { requirementStates, type RequirementAttributes, type RequirementState } from "./requirementTypes";

// How a reader looks at a page's requirements: how deep each one shows, which states, in which order in the Přehled card. The reader's own
// view of one page, kept in this browser: never the page's content, its document order never changes.

/**
 * How deep each requirement shows (model §2, owner 2026-11-04): the requirements overview's levels (overviewKinds.ts,
 * `data-overview-requirements`), from less to more. NONE the overview card alone (the requirement headings and their sections hidden), S
 * every requirement heading folded (the heading line only), M every one open (the default). One requirement folds or opens on its own over
 * the slider; moving the slider resets those.
 */
export const requirementLevels: readonly OverviewLevel[] = [
  // Getters: the label is read where the slider shows it, after the language of the tool has arrived.
  { id: "none", get label() { return t.levels.none; }, get short() { return t.levels.noneShort; } },
  { id: "s", get label() { return t.levels.s; } },
  { id: "m", get label() { return t.levels.m; } },
];
export const defaultRequirementLevel = "m";
export const requirementSorts = ["", "state", "date", "party", "key"] as const;
/** "" is the document's order. */
export type RequirementSort = typeof requirementSorts[number];

export interface RequirementViewPrefs {
  readonly sort: RequirementSort;
  /** Only requirements in this state (and the plain headings above them); "" every state. */
  readonly state: RequirementState | "";
  /** Only requirements of these parties (who: the heading's party as written, "" for none named); none every party. With `state`, both. */
  readonly parties: readonly string[];
}

export const defaultView: RequirementViewPrefs = { sort: "", state: "", parties: [] };

/** Whether the view keeps a requirement of `party` (as written; „Bez určení“ is ""). */
export const partyShown = (view: Pick<RequirementViewPrefs, "parties">, party: string) => !view.parties.length || view.parties.includes(party.trim());
/** Whether the view filters at all (state or party): what it hides then needs a layout. */
const filters = (view: RequirementViewPrefs) => !!view.state || view.parties.length > 0;

const storageKey = (pageId: string) => `atlas.requirementView.${pageId}`;
const prefs = new Map<string, ShallowRef<RequirementViewPrefs>>();

function stored(pageId: string): RequirementViewPrefs {
  try {
    const value = JSON.parse(readStored(storageKey(pageId)) ?? "null") as Partial<RequirementViewPrefs> | null;
    // What an older or a tampered entry holds is read only where it is one of the choices.
    return {
      sort: requirementSorts.includes(value?.sort as RequirementSort) ? value!.sort! : defaultView.sort,
      state: requirementStates.includes(value?.state as RequirementState) ? value!.state! : defaultView.state,
      parties: Array.isArray(value?.parties) ? value.parties.filter((party): party is string => typeof party === "string" && party.length <= 200).slice(0, 100) : defaultView.parties,
    };
  } catch { return defaultView; }
}

/** The page's view, shared by its editor, its read view and its summary's switch in this browser tab. */
export function viewPrefs(pageId = ""): ShallowRef<RequirementViewPrefs> {
  let found = prefs.get(pageId);
  if (!found) prefs.set(pageId, found = shallowRef(stored(pageId)));
  return found;
}

export function setViewPrefs(pageId: string, change: Partial<RequirementViewPrefs>) {
  const view = viewPrefs(pageId), next = { ...view.value, ...change };
  view.value = next;
  if (next.sort === defaultView.sort && next.state === defaultView.state && !next.parties.length) removeStored(storageKey(pageId));
  else writeStored(storageKey(pageId), JSON.stringify(next));
}

/** What needs attention first: blocked, waiting, in progress, not started, then what is finished. */
const stateRank: Record<RequirementState, number> = { blocked: 0, waiting_for_input: 1, in_progress: 2, planned: 3, done: 4, not_applicable: 5 };
const collator = new Intl.Collator("cs", { sensitivity: "base", numeric: true });
/** An empty value after every filled one. */
const filledFirst = (a: string, b: string) => !a !== !b ? (a ? -1 : 1) : collator.compare(a, b);

export function compareRequirements(sort: RequirementSort, a: RequirementAttributes, b: RequirementAttributes): number {
  if (sort === "state") return stateRank[a.state] - stateRank[b.state];
  if (sort === "date") return filledFirst(a.date, b.date);
  if (sort === "party") return filledFirst(a.party.trim(), b.party.trim());
  if (sort === "key") return filledFirst(a.key, b.key);
  return 0;
}

/**
 * A section of the page: a top-level heading and the blocks after it up to the next heading
 * (its own blocks only: the view hides these ranges, it is a view and not a fold); `state` null for a plain heading. The blocks before
 * the first heading belong to none and always show.
 */
interface Section { readonly from: number; readonly to: number; readonly id: string; readonly level: number; readonly state: RequirementState | null; readonly party: string }

const partyOf = (node: PMNode) => String(node.attrs.party ?? "").trim();

function sectionsOf(doc: PMNode): Section[] {
  const found: { from: number; to: number; id: string; level: number; state: RequirementState | null; party: string }[] = [];
  doc.forEach((node, pos) => {
    if (node.type.name === "heading") found.push({ from: pos, to: pos + node.nodeSize, id: String(node.attrs.headingId ?? ""), level: Number(node.attrs.level) || 1, state: (node.attrs.state as RequirementState | null) ?? null, party: partyOf(node) });
    else if (found.length) found.at(-1)!.to = pos + node.nodeSize;
  });
  return found;
}

/** The requirement headings' ids of the page, for the slider's folds. */
export const requirementIds = (doc: PMNode) => sectionsOf(doc).filter(section => section.state).map(section => section.id);

/**
 * `pinned`: the section the caret is in (or the block selected whole is in), by its heading's id, with the state it had when the caret came
 * (model D1): it keeps its place and its filter's look while it is worked in, whatever is changed in it or by a collaborator; once the
 * caret leaves, the view lays out again.
 * `stale`: sections changed since the layout (`local`, or `remote` to gather a collaborator's burst): the view lays out again once, with motion.
 */
type Pinned = { readonly id: string; readonly state: RequirementState | null; readonly party: string; readonly pos: number };
/** `removed`: what the reader's last own change removed, requirements and how many of them the view hid (model §3), for the notice. */
type ViewState = { prefs: RequirementViewPrefs; level: string; layout: DecorationSet; propped: boolean; pinned: Pinned | null; stale: "" | "local" | "remote";
  removed?: { readonly all: number; readonly hidden: number; readonly blocks: number; readonly cards: number }; ownEdit?: boolean };
/** The reader's own document changes so far: the notice's Zpět undoes only while there was none since (a colleague's do not count). */
const ownEdits = new WeakMap<EditorView, number>();
const viewKey = new PluginKey<ViewState>("requirementViewMode");
const levelMeta = "requirementLevel", refreshMeta = "requirementRelayout";

/** The section around the caret, a section already pinned keeping its state; null before the first heading. */
function caretPin(state: EditorState, before: Pinned | null): Pinned | null {
  const { selection, doc } = state, $at = doc.resolve(selection instanceof NodeSelection ? selection.from : selection.head);
  if (!$at.depth && !$at.nodeAfter) return null;
  for (let index = $at.index(0); index >= 0; index--) {
    const node = doc.child(index);
    if (node.type.name !== "heading") continue;
    const id = String(node.attrs.headingId ?? "");
    const pos = doc.resolve(0).posAtIndex(index);
    return before?.id === id ? { ...before, pos } : { id, state: (node.attrs.state as RequirementState | null) ?? null, party: partyOf(node), pos };
  }
  return null;
}

/** A requirement section the view keeps: its state when one is chosen, its party when any are. */
const matches = (view: RequirementViewPrefs, section: Section) => (!view.state || section.state === view.state) && partyShown(view, section.party);

/**
 * Which sections the view hides: a requirement whose state the filter leaves out (NONE: every requirement), and under the filter a plain
 * heading whose requirements below (up to the next plain heading of its level or a higher one) are all hidden, with its own text; NONE
 * keeps plain headings and their text (model §2). The pinned section, with the state it had, never.
 */
function hiddenSections(sections: readonly Section[], shows: (section: Section) => boolean, groups: boolean, pinned: Pinned | null) {
  const hidden = new Array<boolean>(sections.length).fill(false);
  const open: { index: number; level: number; any: boolean; shown: boolean }[] = [];
  const close = (until: number) => { while (open.length && open.at(-1)!.level >= until) { const group = open.pop()!; hidden[group.index] = groups && group.any && !group.shown; } };
  sections.forEach((section, index) => {
    const own = pinned?.id === section.id;
    if (!section.state) { close(section.level); open.push({ index, level: section.level, any: false, shown: own }); return; }
    const kept = own || shows(section);
    open.forEach(group => { group.any = true; group.shown ||= kept; });
    hidden[index] = !kept;
  });
  close(0);
  return hidden;
}

/**
 * The view's classes on every top-level block of a section it hides; the pinned section shows, quietly marked when the view would hide it
 * without the pin. `propped`: the pin keeps something shown, so the caret leaving lays the view out again.
 */
function layout(doc: PMNode, view: RequirementViewPrefs, level: string, pinned: Pinned | null = null): { set: DecorationSet; propped: boolean } {
  const none = level === "none";
  if (!filters(view) && !none) return { set: DecorationSet.empty, propped: false };
  const shows = (section: Section) => !none && matches(view, section);
  const sections = sectionsOf(doc), hidden = hiddenSections(sections, shows, !none, pinned);
  const bare = pinned ? hiddenSections(sections, shows, !none, null) : hidden;
  const decorations: Decoration[] = [];
  sections.forEach((section, index) => {
    const pin = pinned?.id === section.id && bare[index];
    if (!hidden[index] && !pin) return;
    const look = pin ? { class: "is-pinned" } : { class: "is-filtered-out" };
    doc.nodesBetween(section.from, section.to, (node, pos) => {
      decorations.push(Decoration.node(pos, pos + node.nodeSize, pos === section.from && pin ? { ...look, title: t.levels.outsideFilter } : look, { hidden: !pin }));
      return false;
    });
  });
  return { set: DecorationSet.create(doc, decorations), propped: bare.some((value, index) => value !== hidden[index]) };
}

/**
 * What a change removed that the reader must be told (model §3): more than the one block in view (the block keys took a heading with
 * its section, `blocks` the top-level blocks gone, MARKDOWN_BLOCK_REMOVAL_META), or requirements the view hides (a range selected across
 * a filtered page, or at NONE). Hidden as the view would lay it out without the pin, so a requirement the selection passed (pinned for a
 * moment) counts too. Undefined when there is nothing to tell. Only a change removing top-level blocks walks.
 */
function removedUnseen(tr: Transaction, view: RequirementViewPrefs, level: string) {
  const blocks = Number(tr.getMeta(MARKDOWN_BLOCK_REMOVAL_META) ?? 0), filtered = filters(view) || level === "none";
  if (tr.doc.childCount >= tr.before.childCount) return undefined;
  // A Přehled card the block keys removed (the arrows selected it; no text of its own shows it went) is told as a section is.
  const cards = blocks === 1 ? overviewPositions(tr.before).length - overviewPositions(tr.doc).length : 0;
  if (!filtered && blocks < 2 && !cards) return undefined;
  const none = level === "none", sections = sectionsOf(tr.before), kept = new Set(requirementIds(tr.doc));
  const hidden = filtered ? hiddenSections(sections, section => !none && matches(view, section), !none, null) : [];
  let all = 0, gone = 0;
  sections.forEach((section, index) => {
    if (!section.state || kept.has(section.id)) return;
    all++;
    if (hidden[index]) gone++;
  });
  return gone || blocks > 1 || cards > 0 ? { all, hidden: gone, blocks, cards } : undefined;
}
/** „Odebráno 25 bloků, z toho 3 požadavky (1 skrytý).“, or for requirements alone „Odebráno 3 požadavky, z toho 1 skrytý.“, or „Odebrán přehled.“ */
function removedText({ all, hidden, blocks, cards }: NonNullable<ViewState["removed"]>) {
  return t.levels.removed(all, hidden, blocks, cards);
}

/** The editor of each root, so the level's motion (`playRequirementLevel`) folds the right page's requirements. */
const views = new WeakMap<HTMLElement, EditorView>();

/**
 * A change of the requirements' level (overviewKinds.ts `animate`, `change` sets the root's attribute): S folds every requirement heading
 * (foldState.ts, as „Sbalit“ on each), M opens them all, so moving the slider resets single folds (model §2); NONE hides them as a view.
 * The page's blocks in view travel (playViewChange: only those in view are measured, never the whole page), the top one kept in place.
 */
export function playRequirementLevel(root: HTMLElement, change: () => void) {
  const view = views.get(root), line = view && caretLine(view);
  playViewChange(root, () => {
    // The caret's line where it was (model §3), else the page's top row in view.
    if (line) { change(); relevel(root); pinTo(line.element, line.top); } else keepRowInPlace(root, () => { change(); relevel(root); });
  });
}
/** The top-level block holding the caret and where it stands, while it is in view. */
function caretLine(view: EditorView) {
  const { $head } = view.state.selection;
  if (!$head.depth || view.isDestroyed) return null;
  const element = view.nodeDOM($head.before(1));
  if (!(element instanceof HTMLElement)) return null;
  const rect = element.getBoundingClientRect(), bottom = element.ownerDocument.defaultView?.innerHeight ?? Infinity;
  return rect.height && rect.bottom > 0 && rect.top < bottom ? { element, top: rect.top } : null;
}
function relevel(root: HTMLElement) {
  const view = views.get(root), page = view && pageOf.get(view)?.();
  if (!view || view.isDestroyed || !page) return;
  const level = overviewLevel("requirements", page).value;
  // Every requirement follows the slider; the heading fold's own caret rules (headingFoldPlugin) keep the caret from sitting in a fold.
  const state = viewKey.getState(view.state);
  if (level !== "none") foldAll(requirementIds(view.state.doc).map(id => headingKey(id, page)), level === "s");
  // S and M differ only in folds: no second redraw of the page unless NONE comes or goes.
  if (level === "none" || state?.level === "none") view.dispatch(view.state.tr.setMeta(levelMeta, level).setMeta("addToHistory", false));
}
const pageOf = new WeakMap<EditorView, () => string | undefined>();

/** The element whose scrolling moves `element`: its nearest scrolling ancestor, else the page. */
function scrollerOf(element: HTMLElement): Element {
  for (let at = element.parentElement; at; at = at.parentElement) {
    const { overflowY } = getComputedStyle(at);
    if ((overflowY === "auto" || overflowY === "scroll") && at.scrollHeight > at.clientHeight) return at;
  }
  return element.ownerDocument.scrollingElement ?? element.ownerDocument.documentElement;
}
/** Scrolls by what `anchor` moved since it stood at `top` (a viewport position), so it stands there again; nothing for one gone or hidden. */
function pinTo(anchor: Element | undefined, top: number | undefined) {
  if (!(anchor instanceof HTMLElement) || top === undefined || !anchor.isConnected) return;
  const rect = anchor.getBoundingClientRect(), moved = rect.top - top;
  if (rect.height && Math.abs(moved) > 0.5) scrollerOf(anchor).scrollBy(0, moved);
}

/**
 * The first row whose top is in view stays where it was across `change` (3.5.1 §1.4, §1.5): the page scrolls by what the row moved
 * as the rows above it grew or shrank, so a switch of level keeps the reader's place.
 */
export function keepRowInPlace(root: HTMLElement, change: () => void) {
  // The page's top row in view, whichever root holds it: every root of the page (its editors, a read view) switching keeps the same one,
  // and a root below the view moves none. On NONE the summary is the one in view.
  let anchor: HTMLElement | undefined, top: number | undefined;
  const document = root.ownerDocument, bottom = document.defaultView?.innerHeight ?? Infinity;
  for (const row of document.querySelectorAll<HTMLElement>(".ProseMirror > *, [data-requirement-summary-view]")) {
    const rect = row.getBoundingClientRect();
    if (rect.top >= bottom) break;
    if (!rect.height || rect.top < 0) continue;
    // Seen, not only in the view's box: a row under the page's sticky bar would hold that one and push the first row seen by its own
    // growth (RC: S→M +22 px); the editor warmed up hidden under a read page (DocumentEditor `prewarm`) has none.
    const seen = document.elementFromPoint?.(Math.min(Math.max(rect.left + rect.width / 2, 0), (document.defaultView?.innerWidth ?? Infinity) - 1), rect.top + 1);
    if (seen === undefined || (seen && row.contains(seen))) { anchor = row; top = rect.top; break; }
  }
  change();
  pinTo(anchor, top);
}
const moving = new WeakMap<HTMLElement, number>();
/** `data-overview-moving` on the root while the overview's motion plays: a block hydrated meanwhile plays no reveal wave of its own. */
function markMoving(root: HTMLElement) {
  root.setAttribute("data-overview-moving", "");
  clearTimeout(moving.get(root));
  moving.set(root, window.setTimeout(() => root.removeAttribute("data-overview-moving"), motionMs(root, "--atlas-motion-slow")));
}

/** The page's blocks in view, where they are now: what moves when the view changes. */
function rowsInView(root: HTMLElement) {
  const rows = new Map<HTMLElement, DOMRect>(), bottom = root.ownerDocument.defaultView?.innerHeight ?? Infinity;
  for (const element of root.children) {
    if (!(element instanceof HTMLElement)) continue;
    const rect = element.getBoundingClientRect();
    if (rect.top >= bottom) break;
    if (rect.height && rect.bottom > 0) rows.set(element, rect);
  }
  return rows;
}

/**
 * The switch played: a row still shown travels from where it was, one that came into view fades in. Nothing for a row that went
 * (it is gone at once): the eye follows the ones that stay.
 */
export function playViewChange(root: HTMLElement, change: () => void) {
  markMoving(root);
  const before = rowsInView(root);
  change();
  const after = rowsInView(root), coming: HTMLElement[] = [];
  let moved = false;
  after.forEach((rect, element) => {
    const was = before.get(element);
    if (!was) { coming.push(element); return; }
    const dy = was.top - rect.top;
    moved ||= Math.abs(dy) > 0.5;
    if (dy) animateAtlas(element, [{ transform: `translateY(${dy}px)` }, { transform: "none" }], travelTiming(element, dy));
  });
  if (!coming.length) return;
  // What comes fades in where it ends up once the others have travelled there (travelTiming: at most twice the base), never over them.
  const base = motionMs(root, "--atlas-motion-base");
  coming.forEach(element => animateAtlas(element, [{ opacity: 0 }, { opacity: 1 }], { duration: base, delay: moved ? base * 2 : 0, fill: "backwards" }));
}

/** The view's state after a transaction (requirementViewPlugin). */
function applyView(tr: Transaction, value: ViewState, after: EditorState): ViewState {
  // Only a caret someone placed pins its section (a click, a key, typing), not the selection a page opens with.
  const pinned = tr.selectionSet || value.pinned ? caretPin(after, value.pinned) : null;
  const next = tr.getMeta(viewKey) as RequirementViewPrefs | undefined, prefs = next ?? value.prefs;
  const level = (tr.getMeta(levelMeta) as string | undefined) ?? value.level;
  const laid = (stale: ViewState["stale"]): ViewState => { const { set, propped } = layout(tr.doc, prefs, level, pinned); return { prefs, level, layout: set, propped, pinned, stale }; };
  if (next || level !== value.level || tr.getMeta(refreshMeta)) return laid("");
  // A rule's transaction appended to the removal (e.g. a dependency line losing its flag) keeps its notice.
  const changed = reshapesSections(tr), kept = tr.getMeta("appendedTransaction") ? value.removed : undefined;
  const removed = changed && !isRemote(tr) ? removedUnseen(tr, prefs, level) ?? kept : kept;
  if (!filters(prefs) && level !== "none") return { ...value, pinned, removed };
  const mapped = tr.docChanged ? value.layout.map(tr.mapping, tr.doc) : value.layout;
  // A caret that lands in a hidden section (a jump from the Přehled, Find, undo, a removal joining it) shows it at once, pinned.
  if (pinned && pinned.id !== value.pinned?.id && mapped.find(pinned.pos + 1, pinned.pos + 1, spec => spec.hidden === true).length)
    return { ...laid(value.stale), removed };
  // Typing (in a heading or a note) only maps the layout: no walk of the page per keystroke. A changed section, or the caret leaving
  // a section the pin kept shown or whose state changed while it was worked in, lays the view out again once, with motion.
  const was = value.pinned && sectionsOf(after.doc).find(section => section.id === value.pinned!.id);
  const left = !!value.pinned && pinned?.id !== value.pinned.id && (value.propped || was?.state !== value.pinned.state || was?.party !== value.pinned.party);
  const stale = changed ? (isRemote(tr) && value.stale !== "local" ? "remote" : "local") : left ? "local" : value.stale;
  return { ...value, prefs, level, layout: mapped, pinned, stale, removed };
}

/**
 * The page's view in its editor: the state filter (and NONE) as classes on every block of a section it hides; the level S/M as heading
 * folds (playRequirementLevel). Decorations only: the document and its order stay as written, and everyone else sees the page as they
 * chose. The order is the Přehled card's sorted list, never the document's (owner 2026-11-04).
 */
export function requirementViewPlugin(pageId: () => string | undefined) {
  const levelOf = () => { const page = pageId(); return page ? overviewLevel("requirements", page).value : ""; };
  return new Plugin<ViewState>({
    key: viewKey,
    state: {
      init: (_config, state) => {
        const current = viewPrefs(pageId() ?? "").value, level = levelOf();
        const { set, propped } = layout(state.doc, current, level);
        return { prefs: current, level, layout: set, propped, pinned: null, stale: "" };
      },
      // Whether the reader changed the document (a colleague's change is not theirs): the removal notice's Zpět counts these.
      // A transaction appended to it without a change (the word boundary that closes an undo step) keeps the reader's edit theirs.
      apply: (tr, value, _before, after) => ({ ...applyView(tr, value, after),
        ownEdit: tr.docChanged ? !isRemote(tr) : !!value.ownEdit && !!tr.getMeta("appendedTransaction") }),
    },
    props: {
      // Without a page (a board's block, a catalog) no view: every section shows as written.
      decorations: state => pageId() ? viewKey.getState(state)!.layout : null,
    },
    view: editorView => {
      views.set(editorView.dom, editorView);
      pageOf.set(editorView, pageId);
      // The current page's view: a page marked as a catalog (no page, no view) or unmarked changes it at once. In a scope of its own:
      // the component that happened to be rendering when the view was made (a read-only switch remounts it) must not stop it.
      const scope = effectScope(true), stop = () => scope.stop();
      scope.run(() => watch(() => viewPrefs(pageId() ?? "").value, next => {
        if (editorView.isDestroyed) return;
        playViewChange(editorView.dom, () => editorView.dispatch(editorView.state.tr.setMeta(viewKey, next).setMeta("addToHistory", false)));
      }));
      // A stale layout plays once: a local change on the next frame, a collaborator's after a motion's length, gathering the burst.
      let timer = 0;
      const relayout = () => {
        timer = 0;
        if (editorView.isDestroyed || !viewKey.getState(editorView.state)?.stale) return;
        // The caret's line stays where it is (model §3): what the release or a colleague hides above it does not move it.
        const line = caretLine(editorView);
        playViewChange(editorView.dom, () => {
          editorView.dispatch(editorView.state.tr.setMeta(refreshMeta, true).setMeta("addToHistory", false));
          if (line) pinTo(line.element, line.top);
        });
      };
      return {
        update(view, previous) {
          if (view.state.doc !== previous.doc && viewKey.getState(view.state)?.ownEdit) ownEdits.set(view, (ownEdits.get(view) ?? 0) + 1);
          const removed = viewKey.getState(view.state)?.removed;
          if (removed && removed !== viewKey.getState(previous)?.removed) {
            const editor = (view.dom as HTMLElement & { editor?: { commands: { undo(): boolean } } }).editor;
            // Zpět undoes that removal only while the reader changed nothing since: never a later edit of theirs (as the board's undo
            // toast); a colleague's change in between is no reason not to (the shared undo keeps it).
            const at = ownEdits.get(view) ?? 0;
            useToast().success(removedText(removed),
              editor ? { undo: () => { if (view.isDestroyed) return; if ((ownEdits.get(view) ?? 0) === at) editor.commands.undo(); view.focus(); } } : undefined);
          }
          const stale = viewKey.getState(view.state)?.stale;
          if (!stale || timer) return;
          timer = stale === "remote" ? window.setTimeout(relayout, motionMs(view.dom, "--atlas-motion-base")) : requestAnimationFrame(relayout);
        },
        destroy: () => { stop(); views.delete(editorView.dom); if (timer) { clearTimeout(timer); cancelAnimationFrame(timer); } },
      };
    },
  });
}
