import { registerMarkdownBlockKeyboardHook, type MarkdownIntention, type MarkdownPlugin } from "@atlas/markdown/core";
import { Extension, type Editor } from "@tiptap/core";
import type { Node as PMNode } from "@tiptap/pm/model";
import { Plugin, PluginKey, TextSelection, type EditorState, type Transaction } from "@tiptap/pm/state";
import { Decoration, DecorationSet, type EditorView } from "@tiptap/pm/view";
import { FOLDED, foldAll, foldRoot, foldedBy, hasSection, headingKey, isFolded, levelOf, onFoldChange, sectionsAround, toggleFold } from "./headingFold";
import "./headingFold.css";
import t from "../i18n/shell-more";

/**
 * Sbalení sekce podle nadpisu (plan 3.4, U1): a chevron at the end of each page heading with something under it folds everything up to
 * the next heading of the same or a higher level. The reader's view (headingFold.ts, foldState.ts), in reading and while editing:
 * a node decoration over the section's range, so a block a colleague inserts there is folded too, and the document, Yjs and
 * awareness never change (J10.1, J10.5). Only top-level headings with a `headingId`: the page's sections.
 */

const TOGGLE = "atlas-heading-fold";
// The chevron sits in its heading (its place, its fold): the heading names itself, so its name is its text, not „Sbalit sekci: …“ too.
const LABEL = "aria-label";

function drawToggle(toggle: HTMLButtonElement, collapsed: boolean, title: string) {
  toggle.setAttribute("aria-expanded", String(!collapsed));
  toggle.setAttribute("aria-label", t.fold.named(collapsed ? t.fold.expand : t.fold.collapse, title || t.fold.heading));
  toggle.title = t.fold.hint(collapsed ? t.fold.expand : t.fold.collapse, collapsed ? "↓" : "↑");
}
/**
 * A reader's Tab stop; out of the writer's Tab order (Esc, Tab leaves the editor), where Alt+F10, then Tab reaches it (headingControls).
 * A requirement heading's stop is its first chip (requirementHeadingView.ts chipTabIndex): its chevron is one more control, reached by ←/→ (rove).
 */
const toggleTabIndex = (view: EditorView, requirement: boolean) => view.editable || requirement ? -1 : 0;
function createToggle(doc: Document, collapsed: boolean, title: string, key: string) {
  const toggle = doc.createElement("button");
  toggle.type = "button";
  toggle.className = TOGGLE;
  toggle.dataset.key = key;
  toggle.contentEditable = "false";
  drawToggle(toggle, collapsed, title);
  return toggle;
}
/** One click handler per document: the toggle folds its heading; pressing it never moves the caret (`clicked` may, J10.4, F3). */
function listen(root: HTMLElement, signal: AbortSignal, clicked?: (heading: HTMLElement) => void) {
  root.addEventListener("mousedown", event => { if ((event.target as Element).closest?.(`.${TOGGLE}`)) event.preventDefault(); }, { signal });
  root.addEventListener("click", event => {
    const toggle = (event.target as Element).closest?.<HTMLElement>(`.${TOGGLE}`);
    if (!toggle?.parentElement) return;
    event.preventDefault();
    clicked?.(toggle.parentElement);
    toggleFold(toggle.parentElement, undefined, toggle.dataset.key);
  }, { signal });
}

/** The page's blocks, their levels, keys (null: no heading, or one without an id) and which folded heading hides each. */
function layout(doc: PMNode, page?: string | null) {
  const blocks: { node: PMNode; pos: number; key: string | null }[] = [];
  doc.forEach((node, pos) => blocks.push({ node, pos, key: node.type.name === "heading" ? headingKey(node.attrs.headingId, page) : null }));
  const levels = blocks.map(block => block.node.type.name === "heading" ? Number(block.node.attrs.level) || 1 : 0);
  return { blocks, levels, by: foldedBy(levels, blocks.map(block => isFolded(block.key))) };
}

function decorations(state: EditorState, page?: string | null) {
  const { blocks, levels, by } = layout(state.doc, page), found: Decoration[] = [];
  blocks.forEach(({ node, pos, key }, index) => {
    if (by[index]! >= 0) found.push(Decoration.node(pos, pos + node.nodeSize, { class: FOLDED }, { folded: true }));
    else if (key !== null && hasSection(levels, index)) {
      const collapsed = isFolded(key);
      found.push(Decoration.node(pos, pos + node.nodeSize, { [LABEL]: node.textContent }));
      // At the heading's end, after a requirement's chips (side 1): the DOM, and so Tab, goes as drawn: checkbox, chips, chevron.
      found.push(Decoration.widget(pos + node.nodeSize - 1, view => Object.assign(createToggle(view.dom.ownerDocument, collapsed, node.textContent, key), { tabIndex: toggleTabIndex(view, node.attrs.state != null) }), { side: 10, key: `${TOGGLE}:${key}:${node.textContent}`, ignoreSelection: true, stopEvent: () => true }));
    }
  });
  return DecorationSet.create(state.doc, found);
}

/**
 * The section of the block holding the caret: its own heading when that has something under it, else the nearest heading above
 * (a caret in a paragraph folds the paragraph's section). Null without a heading that can fold.
 */
function caretSection(state: EditorState, page?: string | null) {
  const { $head } = state.selection;
  if ($head.depth < 1) return null;
  const { levels, blocks } = layout(state.doc, page), index = $head.index(0);
  const at = [...(levels[index] && hasSection(levels, index) ? [index] : []), ...sectionsAround(levels, index)].find(candidate => blocks[candidate]!.key !== null);
  return at === undefined ? null : blocks[at]!;
}

/** Explicit fold (J10.4): a caret inside the section goes to the end of its heading at once, before the blocks fold away. */
function caretToHeading(view: EditorView, headingPos: number) {
  const { doc, selection } = view.state, index = doc.resolve(headingPos).index(0), caret = selection.$head.depth ? selection.$head.index(0) : -1;
  if (caret <= index || !sectionsAround(layout(doc).levels, caret).includes(index)) return;
  view.dispatch(view.state.tr.setSelection(TextSelection.create(doc, headingPos + doc.child(index).nodeSize - 1)).setMeta("addToHistory", false));
}

/**
 * Typing inside one top-level block that is no heading (a paragraph, a list): the sections stay as they are and the decorations
 * only shift, O(1) blocks per keystroke (J12.6). A step without ranges (an attribute, a mark) may change a heading's id: rebuilt.
 */
function typedInBlock(tr: Transaction) {
  if (tr.before.childCount !== tr.doc.childCount) return false;
  return tr.steps.every((step, i) => {
    let ranges = 0, inside = true;
    step.getMap().forEach((from, to) => {
      const doc = tr.docs[i]!, $from = doc.resolve(from), $to = doc.resolve(to);
      ranges++;
      inside &&= $from.depth > 0 && $to.depth > 0 && $from.index(0) === $to.index(0) && $from.node(1).type.name !== "heading";
    });
    return ranges > 0 && inside;
  });
}

const pluginKey = new PluginKey<DecorationSet>("atlasHeadingFold");

function editorPlugin(editor: Editor, pageId?: () => string | null | undefined) {
  return new Plugin<DecorationSet>({
    key: pluginKey,
    // Rebuilt when the sections or a fold change, mapped for typing inside a block, kept for a caret that only moves.
    state: {
      init: (_, state) => decorations(state, pageId?.()),
      apply: (tr, set, _previous, state) => tr.getMeta(pluginKey) || (tr.docChanged && !typedInBlock(tr)) ? decorations(state, pageId?.()) : tr.docChanged ? set.map(tr.mapping, tr.doc) : set,
    },
    props: { decorations: state => pluginKey.getState(state) },
    view(view: EditorView) {
      const controller = new AbortController();
      foldRoot(view.dom, pageId);
      const unhook = registerMarkdownBlockKeyboardHook(editor, { id: "atlas-heading-controls", controls: block => headingControls(editor, block) });
      view.dom.addEventListener("keydown", event => rove(view, event), { signal: controller.signal });
      listen(view.dom, controller.signal, heading => {
        const headingPos = view.state.doc.resolve(view.posAtDOM(heading, 0)).before(1);
        // A click before the text had the focus (the title field holds it after the page opened): the caret goes to the heading's end
        // and the editor takes the focus, so the keys that follow go to the text (F3); a caret in the text stays (J10.4 below).
        if (view.editable && !view.hasFocus()) {
          view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, headingPos + view.state.doc.nodeAt(headingPos)!.nodeSize - 1)).setMeta("addToHistory", false));
          view.focus();
        }
        caretToHeading(view, headingPos);
      });
      // A fold redraws the decorations; a caret that a fold hid (another surface, „Sbalit vše“) goes to the end of its heading.
      const stop = onFoldChange(() => {
        if (view.isDestroyed) return;
        const tr = view.state.tr.setMeta(pluginKey, true).setMeta("addToHistory", false);
        const { $head } = view.state.selection, { blocks, by } = layout(view.state.doc, pageId?.()), at = $head.depth ? by[$head.index(0)]! : -1;
        if (at >= 0) { const heading = blocks[at]!; tr.setSelection(TextSelection.create(tr.doc, heading.pos + heading.node.nodeSize - 1)).scrollIntoView(); }
        view.dispatch(tr);
        // The chevrons stay (their key holds no fold state): only those whose fold changed say so, not 150 new buttons per level switch.
        view.dom.querySelectorAll<HTMLButtonElement>(`.${TOGGLE}`).forEach(toggle => {
          const collapsed = isFolded(toggle.dataset.key);
          if (toggle.getAttribute("aria-expanded") !== String(!collapsed)) drawToggle(toggle, collapsed, toggle.parentElement?.getAttribute(LABEL) ?? "");
        });
      });
      let editable = view.editable;
      return {
        // A caret that arrives in a folded section (arrows, a search, a collaborator's change, the reveal command) opens it.
        update(next, previous) {
          if (next.editable !== editable) {
            editable = next.editable;
            next.dom.querySelectorAll<HTMLElement>(`.${TOGGLE}`).forEach(toggle => { toggle.tabIndex = toggleTabIndex(next, !!toggle.parentElement?.querySelector(":scope > .rq-chips")); });
          }
          if (next.state.selection.eq(previous.selection) && next.state.doc === previous.doc) return;
          const { $head } = next.state.selection;
          if (!$head.depth) return;
          // O(1) for a caret in the open: only a block a fold hides carries the `folded` decoration.
          const at = $head.before(1);
          if (!pluginKey.getState(next.state)?.find(at, at + 1, spec => spec.folded === true).length) return;
          const { blocks, levels } = layout(next.state.doc, pageId?.()), index = $head.index(0);
          queueMicrotask(() => foldAll(sectionsAround(levels, index).map(heading => blocks[heading]!.key), false));
        },
        destroy() { controller.abort(); stop(); unhook(); },
      };
    },
  });
}

/** Folds (`collapsed`) or opens the section of the block holding the caret; the caret goes to the end of its heading when folding (J10.4). */
function foldSection(editor: Editor, collapsed: boolean, pageId?: () => string | null | undefined) {
  const { view } = editor, found = caretSection(view.state, pageId?.()), dom = found && view.nodeDOM(found.pos);
  if (!found || !(dom instanceof HTMLElement)) return false;
  if (collapsed) caretToHeading(view, found.pos);
  toggleFold(dom, collapsed, found.key!);
  return true;
}

/** „Sbalit sekci“ / „Rozbalit sekci“ in the intentions of a heading with something under it (Ctrl+., the block menu): plain and requirement headings alike. */
function foldIntention(editor: Editor, pageId?: () => string | null | undefined): MarkdownIntention[] {
  const { $head } = editor.state.selection, found = $head.depth === 1 && $head.parent.type.name === "heading" ? caretSection(editor.state, pageId?.()) : null;
  if (!found || found.pos !== $head.before(1)) return [];
  const collapsed = isFolded(found.key);
  return [{ id: "heading.fold", label: collapsed ? t.fold.expand : t.fold.collapse, description: collapsed ? "Alt+↓" : "Alt+↑", icon: `svg:${collapsed ? "chevrons-up-down" : "chevrons-down-up"}`,
    group: "heading", keywords: ["sbalit", "rozbalit", "sekce", "nadpis"], run: () => { foldSection(editor, !collapsed, pageId); } }];
}

/**
 * Alt+↑ folds the section of the block holding the caret (the caret goes to the end of its heading), Alt+↓ opens it (J10.4): a plain
 * heading and a requirement heading alike (a requirement is a heading, requirementHeadingView.ts).
 */
const foldKeys = (pageId?: () => string | null | undefined) => Extension.create({
  name: "atlasHeadingFold",
  priority: 50,
  addProseMirrorPlugins() { return [editorPlugin(this.editor, pageId)]; },
  addKeyboardShortcuts() {
    return { "Alt-ArrowUp": () => foldSection(this.editor, true, pageId), "Alt-ArrowDown": () => foldSection(this.editor, false, pageId) };
  },
});

/**
 * The grip's keyboard path (Alt+F10, then Tab) for a heading, in the order they are drawn (the chevron stands at the line's end): a
 * requirement's checkbox, its chips, the chevron. All are widgets of the heading (contentEditable=false, out of the caret's reach), in
 * this order in the DOM too, so Tab after the chevron leaves the heading.
 */
function headingControls(editor: Editor, block: { pos: number; node: PMNode }) {
  const dom = block.node.type.name === "heading" ? editor.view.nodeDOM(block.pos) : null;
  return dom instanceof HTMLElement ? controlsOf(dom) : [];
}
function controlsOf(dom: Element) {
  const all = (selector: string) => [...dom.querySelectorAll<HTMLElement>(selector)];
  return [...all(":scope > .rq-check"), ...all(":scope > .rq-chips > button"), ...all(":scope > .atlas-heading-fold")];
}

/**
 * A reader's heading is one Tab stop (a roving tabindex): ←/→, Home and End walk its controls (chips, chevron; the checkbox is no
 * reader's control), the one reached becomes the heading's stop. 80 requirements were 340 Tab stops.
 */
function rove(view: EditorView, event: KeyboardEvent) {
  if (view.editable || event.altKey || event.ctrlKey || event.metaKey || !["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
  const target = event.target as HTMLElement, heading = target.closest?.(":is(h1, h2, h3, h4, h5, h6)");
  const controls = heading ? controlsOf(heading).filter(control => control.getAttribute("aria-disabled") !== "true") : [], index = controls.indexOf(target);
  if (index < 0) return;
  const next = controls[event.key === "Home" ? 0 : event.key === "End" ? controls.length - 1 : Math.max(0, Math.min(controls.length - 1, index + (event.key === "ArrowLeft" ? -1 : 1)))]!;
  event.preventDefault();
  controls.forEach(control => { control.tabIndex = control === next ? 0 : -1; });
  next.focus();
}

/** Reading: the toggles go into the rendered headings and the hidden blocks get their class, again after every fold. */
function renderFolds(container: HTMLElement, doc: Document, signal: AbortSignal, pageId?: () => string | null | undefined) {
  foldRoot(container, pageId);
  listen(container, signal);
  const draw = () => {
    const blocks = [...container.children] as HTMLElement[], levels = blocks.map(levelOf);
    const keys = blocks.map((block, index) => levels[index] ? headingKey(block.dataset.headingId, pageId?.()) : null);
    const by = foldedBy(levels, keys.map(isFolded));
    blocks.forEach((block, index) => {
      block.classList.toggle(FOLDED, by[index]! >= 0);
      const key = keys[index], existing = block.querySelector<HTMLButtonElement>(`:scope > .${TOGGLE}`);
      if (!key || !hasSection(levels, index)) { if (existing) { existing.remove(); block.removeAttribute(LABEL); } return; }
      const title = (block.textContent ?? "").trim(), collapsed = isFolded(key);
      block.setAttribute(LABEL, title);
      if (!existing) block.prepend(createToggle(doc, collapsed, title, key));
      else { drawToggle(existing, collapsed, title); existing.dataset.key = key; }
    });
  };
  draw();
  const stop = onFoldChange(draw);
  return () => {
    stop();
    container.querySelectorAll(`:scope > .${FOLDED}`).forEach(block => block.classList.remove(FOLDED));
    container.querySelectorAll(`:scope > * > .${TOGGLE}`).forEach(toggle => { toggle.parentElement?.removeAttribute(LABEL); toggle.remove(); });
  };
}

/**
 * The page's heading folds, in its reading and in its live editor. Folds are kept by `page#headingId` (foldState.ts): the same
 * heading id on two pages (a shared template) folds on each page alone.
 */
export function createHeadingFoldPlugin(pageId?: () => string | null | undefined): MarkdownPlugin {
  return {
    id: "atlas.heading-fold", apiVersion: 1, profiles: ["rich"],
    create: () => ({
      extensions: [foldKeys(pageId)],
      intentions: ({ editor }) => foldIntention(editor, pageId),
      onRender: ({ container, ownerDocument, signal }) => renderFolds(container, ownerDocument, signal, pageId),
    }),
  };
}
