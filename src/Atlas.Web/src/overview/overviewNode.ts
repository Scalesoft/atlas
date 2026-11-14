import { Extension, Node, type Editor } from "@tiptap/core";
import type { Node as PMNode } from "@tiptap/pm/model";
import { NodeSelection, Plugin, PluginKey, TextSelection, type Transaction } from "@tiptap/pm/state";
import { Decoration, DecorationSet } from "@tiptap/pm/view";
import { VueNodeViewRenderer } from "@tiptap/vue-3";
import { defineMarkdownAttributeValidator, defineMarkdownCommand, dispatchMarkdownHistoryOperation, MARKDOWN_BLOCK_REMOVAL_META,
  type MarkdownValueConstraint } from "@atlas/markdown/core";
import { effectScope, watch } from "vue";
import { afterPaint } from "../idle";
import { revealBlock } from "../motion/aiMotion";
import { kindOfNode, overviewKind, overviewKinds, overviewLevel, overviewLevelAttributes, playKindLevel, type OverviewHost } from "./overviewKinds";
import OverviewView from "./OverviewView.vue";
import t from "../i18n/overview";

/** A flat attribute with its declared rule (the manifest publishes it, the server checks it), stored in HTML as data-<name>. */
function attribute(name: string, fallback: string | null, constraint: MarkdownValueConstraint & { kind: "text" }) {
  const pattern = new RegExp(constraint.pattern!);
  return {
    default: fallback,
    validate: defineMarkdownAttributeValidator(constraint, value => {
      if (!(value === null && constraint.nullable) && !(typeof value === "string" && pattern.test(value))) throw new TypeError(t.invalidAttribute);
    }),
    // Absent: the rule's own attrs (a legacy summary's kind) or the default.
    parseHTML: (element: HTMLElement) => element.getAttribute(`data-${name}`),
    renderHTML: () => ({}),
  };
}

// The document the projection is writing: its lossy check names it first (requirementsPlugin.ts), the serializer does not.
let serialized: WeakRef<PMNode> | undefined;
export function noteSerialized(document: PMNode) { serialized = new WeakRef(document); }

/** Whether `node` is `document` itself or one of its descendants, by identity. */
function holds(document: PMNode, node: PMNode) {
  let found = document === node;
  document.descendants(child => { found ||= child === node; return !found && !child.isTextblock; });
  return found;
}

/** The document an overview inside `parent` belongs to: the one being projected that holds it, else the editor's. */
function documentOf(parent: PMNode, editor: Editor | undefined) {
  if (parent.type === parent.type.schema.topNodeType) return parent;
  const candidates = [serialized?.deref(), editor?.state.doc].filter((document): document is PMNode => !!document);
  return candidates.find(document => holds(document, parent)) ?? parent;
}

/** The document's overview blocks with their kinds, in document order. */
export function overviewPositions(document: PMNode): { pos: number; kind: string }[] {
  const found: { pos: number; kind: string }[] = [];
  document.descendants((node, pos) => {
    const kind = kindOfNode(node.type.name, node.attrs);
    if (kind !== undefined) found.push({ pos, kind });
    return !node.isTextblock;
  });
  return found;
}

/** „Souhrn nasazení“ and the automatic overview's pin bring the page's first overview of the kind into view instead of a second one. Returns whether the page had one. */
export function revealOverview(editor: Editor, kind: string): boolean {
  const found = overviewPositions(editor.state.doc).find(overview => overview.kind === kind);
  if (!found) return false;
  const dom = editor.view.nodeDOM(found.pos);
  if (dom instanceof HTMLElement) revealBlock(dom);
  return true;
}

/** Selects the overview at `pos` whole (a click on its card, the arrows): the selected component's outline, Delete removes it. */
export function selectOverview(editor: Editor, pos: number) {
  editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, pos)).scrollIntoView());
  editor.view.focus();
}

/** „Odebrat přehled“ of a card's own menu: the overview whose card holds `element` goes as Delete takes a selected one (its toast with Zpět). */
export function removeOverview(editor: Editor, element: Element): boolean {
  const found = editor.isEditable && overviewPositions(editor.state.doc).find(({ pos }) => editor.view.nodeDOM(pos)?.contains(element));
  if (!found) return false;
  const tr = editor.state.tr.delete(found.pos, found.pos + editor.state.doc.nodeAt(found.pos)!.nodeSize);
  dispatchMarkdownHistoryOperation(editor, tr.setMeta(MARKDOWN_BLOCK_REMOVAL_META, 1).scrollIntoView());
  editor.view.focus();
  return true;
}

/** The overview's insert: a new overview of the kind, also beside one already there (they share the kind's level, overviewKinds.ts). */
export const insertOverview = defineMarkdownCommand<{ kind: string }>({ id: "overview.insert", apply: ({ kind }) => ({ commands, dispatch }) =>
  !dispatch || commands.insertContent({ type: "overview", attrs: { kind } }) });

/** „Přehled: …“ of the slash menu, one per registered kind the editor offers (the menu preselects the first, Enter inserts it). */
export const overviewSlashActions = (offered: (kind: string) => boolean) => overviewKinds().filter(kind => offered(kind.kind)).map(kind => ({
  id: `overview.${kind.kind}`, label: t.slash.label(kind.title ?? kind.label), description: t.slash.description,
  // The Czech words stay searchable in every language: the slash menu is typed into from habit.
  icon: "svg:activity", group: t.slash.group, keywords: ["přehled", "overview", kind.kind, kind.label, ...(kind.title ? [kind.title] : [])], command: insertOverview({ kind: kind.kind }) }));

const levelKey = new PluginKey("overviewLevels");

// The kinds the page shows an overview of, whose level the root carries. The requirements' level is no attribute on a page of
// requirement headings: it folds them (requirementViewMode.ts), and the attribute styles only blocks of the old shape, whose rules
// would restyle the whole page on every switch (3.6 budgets). So it stays only while the page holds such a block.
// shortcut: read when an overview changes, not when an old block comes or goes (the conversion rewrites pages on the server).
const kindsShown = (document: PMNode): ReadonlySet<string> => {
  const kinds = new Set(overviewPositions(document).map(({ kind }) => kind));
  let old = false;
  document.descendants(node => { old ||= node.type.name === "requirement"; return !old && !node.isTextblock; });
  if (old) kinds.add("requirements"); else kinds.delete("requirements");
  return kinds;
};
const holdsOverview = (document: PMNode, from: number, to: number) => {
  let found = false;
  document.nodesBetween(from, to, node => { found ||= kindOfNode(node.type.name, node.attrs) !== undefined; return !found && !node.isTextblock; });
  return found;
};
/** Whether a transaction added, removed or changed an overview: typing elsewhere does not walk the page again. */
export function touchesOverview(tr: Transaction): boolean {
  return tr.steps.some((step, index) => {
    const before = tr.docs[index]!, after = tr.docs[index + 1] ?? tr.doc, at = (step as { pos?: unknown }).pos;
    // A step on one node's attributes (AttrStep) changes no range.
    if (typeof at === "number" && [before.nodeAt(at), after.nodeAt(at)].some(node => node && kindOfNode(node.type.name, node.attrs) !== undefined)) return true;
    let found = false;
    step.getMap().forEach((oldStart, oldEnd, newStart, newEnd) => { found ||= holdsOverview(before, oldStart, oldEnd) || holdsOverview(after, newStart, newEnd); });
    return found;
  });
}

/**
 * The reader's levels of the kinds the page shows an overview of as `data-overview-<kind>` on the editor's root (a removed overview
 * takes its attribute along). One plugin per editor, one watcher per kind:
 * a change of the kind's one state is one dispatch and one motion, however many overviews of the kind the page holds; the same
 * level set again does not change the state (setOverviewLevel), so nothing runs. `shown` leaves kinds out (a requirement catalog page).
 */
export const levelPlugin = (pageId: () => string | undefined, shown?: (kind: string) => boolean) => new Plugin<ReadonlySet<string>>({
  key: levelKey,
  state: { init: (_config, state) => kindsShown(state.doc), apply: (tr, kinds) => tr.docChanged && touchesOverview(tr) ? kindsShown(tr.doc) : kinds },
  props: { attributes(state) { const kinds = levelKey.getState(state) as ReadonlySet<string>; return overviewLevelAttributes(pageId() ?? "", kind => kinds.has(kind) && (shown?.(kind) ?? true)); } },
  view: editorView => {
    // The level, and whether the kind shows (a page marked or unmarked as a catalog): either changes the root at once, without waiting for a transaction.
    // In a scope of its own, as the view outlives the component rendering when it was made (a read-only switch remounts that one).
    const scope = effectScope(true);
    scope.run(() => overviewKinds().filter(kind => kind.levels?.length).forEach(kind => watch([() => overviewLevel(kind.kind, pageId() ?? "").value, () => shown?.(kind.kind) ?? true], () => {
      if (editorView.isDestroyed) return;
      const apply = () => { if (!editorView.isDestroyed) editorView.dispatch(editorView.state.tr.setMeta(levelKey, true).setMeta("addToHistory", false)); };
      // Warming up hidden under the read page (DocumentEditor's `.editor-hydrating`): no motion, and once the read page's switch has
      // painted, so the reader's press restyles one page, not two (3.6 perf).
      if (editorView.dom.closest(".editor-hydrating")) afterPaint(apply);
      else playKindLevel(kind, editorView.dom, apply);
    })));
    return { destroy: () => scope.stop() };
  },
});

/**
 * The overview in the editor's Markdown dialect (MarkdownDialect.Overview on the server): ```a-overview with an a-YAML map of its one
 * key, `kind: contacts`. Comments and blank lines are allowed; anything else is no overview (it stays a code block).
 */
export const overviewFence = "a-overview";
export const overviewFenceText = (kind: string) => `\`\`\`${overviewFence}\nkind: ${kind}\n\`\`\``;
export function readOverviewFence(source: string): string | undefined {
  const lines = source.split("\n").map(line => line.trim()).filter(line => line && !line.startsWith("#"));
  return lines.length === 1 ? /^kind:\s*(["']?)([a-z][a-z0-9-]{0,39})\1(?:\s+#.*)?$/.exec(lines[0]!)?.[2] : undefined;
}

const placeholder = "Přehled";
const escapeHtml = (text: string) => text.replace(/</g, "&lt;").replace(/>/g, "&gt;");
const lineOf = (node: PMNode, document: PMNode) => {
  const kind = overviewKind(kindOfNode(node.type.name, node.attrs) ?? "");
  return kind ? (kind.text ? `${kind.label}: ${kind.text(document)}` : kind.label) : placeholder;
};

const isOverview = (node: PMNode) => node.type.name === "overview";
const FOCUSABLE = "button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), [role=slider], [tabindex]";

/**
 * The arrows still reach the card (keyboard-first: selected, Backspace or Delete removes it as any block), although no selection ProseMirror
 * makes by itself lands on it (`selectable: false`): an arrow that would cross it from the edge of a top-level block, a gap or a selected
 * block selects it. Ahead of the gap cursor, which skips a block that is not selectable. Backspace at the start of the text below it and
 * Delete at the end of the text above select it first, as for any component (the editor's J4.3; the core keymap removed the card at once),
 * an empty line between going with that key. Enter on the selected card goes to its first control; Esc there comes back (SelectedElementEscape).
 */
const OverviewArrows = Extension.create({
  name: "overviewArrows",
  priority: 101,
  // A card inside a range selected across it shows it is in it: the card takes no text selection of its own (user-select: none), so the
  // range looked as if it stopped at the card, although Delete, cut and Ctrl+Z take the card along.
  addProseMirrorPlugins: () => [new Plugin({ props: { decorations: ({ selection, doc }) => {
    if (!(selection instanceof TextSelection) || selection.empty) return null;
    const cards: Decoration[] = [];
    doc.nodesBetween(selection.from, selection.to, (node, pos) => {
      if (isOverview(node) && pos >= selection.from && pos + node.nodeSize <= selection.to) cards.push(Decoration.node(pos, pos + node.nodeSize, { "data-in-selection": "" }));
      return !node.isTextblock;
    });
    return cards.length ? DecorationSet.create(doc, cards) : null;
  } } })],
  addKeyboardShortcuts() {
    const toCard = (dir: -1 | 1, side: "up" | "down" | "left" | "right") => () => {
      const { state, view } = this.editor, { selection } = state;
      if (selection instanceof NodeSelection && selection.node.type.name === "overview") return false;
      let $edge = dir < 0 ? selection.$from : selection.$to;
      if ($edge.depth === 1 && selection.empty && view.endOfTextblock(side)) $edge = state.doc.resolve(dir < 0 ? $edge.before() : $edge.after());
      const card = $edge.depth ? null : dir < 0 ? $edge.nodeBefore : $edge.nodeAfter;
      if (card?.type.name !== "overview") return false;
      selectOverview(this.editor, dir < 0 ? $edge.pos - card.nodeSize : $edge.pos);
      return true;
    };
    const besideCard = (dir: -1 | 1) => () => {
      const { state, view } = this.editor, { selection } = state, { $from } = selection;
      if (!selection.empty || $from.depth !== 1 || !$from.parent.isTextblock || $from.parentOffset !== (dir < 0 ? 0 : $from.parent.content.size)) return false;
      const card = dir < 0 ? state.doc.resolve($from.before()).nodeBefore : state.doc.resolve($from.after()).nodeAfter;
      if (!card || !isOverview(card)) return false;
      const tr = state.tr, at = dir < 0 ? $from.before() - card.nodeSize : $from.after();
      if (!$from.parent.content.size) tr.delete($from.before(), $from.after());
      view.dispatch(tr.setSelection(NodeSelection.create(tr.doc, tr.mapping.map(at, -1))).scrollIntoView());
      return true;
    };
    const intoControls = () => {
      const { selection } = this.editor.state;
      if (!(selection instanceof NodeSelection) || !isOverview(selection.node)) return false;
      const card = this.editor.view.nodeDOM(selection.from) as HTMLElement | null;
      const control = [...card?.querySelectorAll<HTMLElement>(FOCUSABLE) ?? []].find(element => element.tabIndex >= 0);
      control?.focus();
      return !!control;
    };
    return { ArrowUp: toCard(-1, "up"), ArrowDown: toCard(1, "down"), ArrowLeft: toCard(-1, "left"), ArrowRight: toCard(1, "right"),
      Backspace: besideCard(-1), Delete: besideCard(1), Enter: intoControls };
  },
});

/**
 * The page overview (plan 3.4 R6): one block per kind that sums up the page's components of the kind (overviewKinds.ts). It stores
 * its id and kind only; the card (OverviewView.vue, in an editor with a Vue host) and the Markdown line are computed from the
 * document. A headless editor (projection, warm-up) shows the line as text.
 */
export const OverviewNode = Node.create<{ host?: OverviewHost }>({
  name: "overview",
  group: "block",
  atom: true,
  // Never a selection ProseMirror makes by itself: a page opening on it (Selection.atStart) or a caret mapped or clamped onto it selected
  // the card unseen, and the next Backspace or Ctrl+Shift+Backspace removed it. A click on the card (OverviewView.vue), the grip and the
  // arrows still select it (OverviewArrows).
  selectable: false,
  draggable: true,
  addOptions: () => ({ host: undefined }),
  addAttributes: () => ({
    id: attribute("id", null, { kind: "text", nullable: true, pattern: "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$" }),
    kind: attribute("kind", "", { kind: "text", maxLength: 40, singleLine: true, pattern: "^(?:[a-z][a-z0-9-]{0,39})?$" }),
  }),
  // A pasted Atlas 3.3 summary is the requirements overview; the dialect's ```a-overview fence (Markdown) is the overview of its kind.
  parseHTML: () => [{ tag: "div[data-overview]" }, { tag: "div[data-requirement-summary]", attrs: { kind: "requirements" } },
    { tag: `div[data-md-fence="${overviewFence}"]`, getAttrs: element => { const kind = readOverviewFence(element.getAttribute("data-md-source") ?? ""); return kind ? { kind } : false; } }],
  renderHTML: ({ node, HTMLAttributes }) => ["div", { ...HTMLAttributes, "data-id": node.attrs.id ?? undefined, "data-overview": "",
    "data-kind": kindOfNode(node.type.name, node.attrs), class: "atlas-overview" }, overviewKind(kindOfNode(node.type.name, node.attrs) ?? "")?.label ?? placeholder],
  addNodeView() {
    // Every press in the card is the card's: tiptap would hand ProseMirror a press on an icon or the card's background, which puts the
    // caret somewhere (a click selects the card itself, OverviewView.vue). Drags, drops and the right button stay the editor's (the block
    // handle, its menu).
    const vue = VueNodeViewRenderer(OverviewView, { stopEvent: ({ event }) => !event.type.startsWith("drag") && event.type !== "drop" && event.type !== "contextmenu" });
    return props => {
      if ((props.editor as { contentComponent?: unknown }).contentComponent) return vue(props);
      const dom = document.createElement("div"), editor = props.editor;
      dom.className = "atlas-overview";
      dom.dataset.overview = "";
      const refresh = () => { dom.textContent = lineOf(props.node, editor.state.doc); };
      refresh();
      editor.on("update", refresh);
      return { dom, update: node => node.type === props.node.type && node.attrs.kind === props.node.attrs.kind, destroy: () => { editor.off("update", refresh); } };
    };
  },
  addExtensions: () => [OverviewArrows],
  addProseMirrorPlugins() { return [levelPlugin(() => this.options.host?.pageId())]; },
  addStorage: () => ({ label: t.nodeLabel, markdown: { serialize(this: { editor?: Editor }, state: { write(text: string): void; text(text: string, escape?: boolean): void; closeBlock(node: PMNode): void },
    node: PMNode, parent: PMNode) {
    // A kind with a line (requirements) writes its readable line; any other the dialect's fence, which reads back. As the server (EditorMarkdownWriter).
    const name = kindOfNode(node.type.name, node.attrs) ?? "", kind = overviewKind(name);
    if (kind?.text) {
      state.write(`**${kind.label}:** `);
      state.text(escapeHtml(kind.text(documentOf(parent, this.editor))));
    } else if (name) state.text(overviewFenceText(name), false);
    else return;
    state.closeBlock(node);
  } } }),
});

/**
 * Atlas 3.3's `requirementSummary` block, kept in the schema so a live shared document that still holds one opens (y-prosemirror would
 * drop a node it cannot build); it reads and shows as the requirements overview, and the content repair renames it (legacyDocument.ts,
 * the snapshotter's migrateLiveContent and LegacyDocumentRepair.cs on the server). Nothing inserts it.
 */
export const LegacyOverviewNode = OverviewNode.extend({
  name: "requirementSummary",
  addAttributes: () => ({ id: attribute("id", null, { kind: "text", nullable: true, pattern: "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$" }) }),
  parseHTML: () => [],
  addProseMirrorPlugins: () => [],
  // OverviewArrows comes with OverviewNode once; inherited here it was a duplicate extension and no editor opened.
  addExtensions: () => [],
});
