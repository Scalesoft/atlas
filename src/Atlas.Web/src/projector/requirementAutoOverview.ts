import type { Editor } from "@tiptap/core";
import { Plugin, PluginKey, Selection, TextSelection, type Transaction } from "@tiptap/pm/state";
import type { EditorView } from "@tiptap/pm/view";
import { Decoration, DecorationSet } from "@tiptap/pm/view";
import { computed, defineComponent, h, onScopeDispose, render, shallowRef, toRaw, watch } from "vue";
import type { PageRequirement } from "./requirementModel";
import { catalogNames, isCatalogPage } from "./requirementCatalog";
import { cardControls, outlineOfDoc, reshapesSections, showsAutoOverview, touchesOutline, type PageOutline } from "./requirementOverview";
import { runtimeOf } from "./requirementRuntime";
import { revealOverview } from "../overview/overviewNode";
import type { ProjectorHost } from "./requirementsPlugin";
import RequirementSummaryCard from "./RequirementSummaryCard.vue";
import { whenIdle } from "../idle";
import { setFolded } from "../editor/foldState";
import { headingKey } from "../editor/headingFold";
import { goToHeading } from "./requirementHeadingView";

const signature = (outline: PageOutline) => JSON.stringify([outline.entries, [...outline.notes], outline.summary]);

/**
 * What the overview card of an editor shows: the live index's blocks, the page's outline (headings, blocked blocks' notes) kept with
 * the document, and a block brought into view. For the summary block's view and the automatic overview alike.
 */
export function useEditorOverview(editor: Editor, host?: ProjectorHost) {
  const runtime = runtimeOf(editor);
  let known = "";
  const outline = shallowRef<PageOutline>(outlineOfDoc(editor.state.doc));
  const refresh = () => {
    if (editor.isDestroyed) return;
    const next = outlineOfDoc(editor.state.doc), sign = signature(next);
    if (sign !== known) { known = sign; outline.value = next; }
  };
  refresh();
  // Once the typing rests (model §8): the card shows the last outline meanwhile.
  let queued = false;
  const schedule = () => { if (queued) return; queued = true; whenIdle(() => { queued = false; refresh(); }, 150); };
  const later = ({ transaction }: { transaction: Transaction }) => { if (touchesOutline(transaction)) schedule(); };
  editor.on("transaction", later);
  // A document the editor loaded without a transaction reaches the index first (a retitle too: nothing walks while the title is typed).
  watch(() => runtime.index.value, schedule);
  onScopeDispose(() => { editor.off("transaction", later); });
  const show = (item: PageRequirement) => {
    // A requirement heading (model §8): the caret goes to the end of its line, for a reader too, so the view pins its section and a filter
    // or NONE cannot hide it (requirementViewMode.ts); the writer then has the caret there, the reader the focus (goToHeading).
    let at = -1;
    editor.state.doc.forEach((node, pos) => { if (at < 0 && node.type.name === "heading" && node.attrs.headingId === item.id) at = pos; });
    if (at < 0) return;
    // A folded target opens, over the level (S folds every heading): the jump shows what it went to.
    setFolded(headingKey(item.id, host?.pageId()), false);
    const { view } = editor;
    if (!view.editable) view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, at + view.state.doc.nodeAt(at)!.nodeSize - 1)).setMeta("addToHistory", false));
    goToHeading(editor, item.id);
  };
  // A requirement heading has no detail of its own: it comes into view.
  const detail = show;
  /**
   * Out of the card by keyboard into the writer's text (spec §4.2): `next` (Tab from its last control) to the start of the text after it,
   * `back` (Shift+Tab from its first) to the end of the text before it, or past the editor to what precedes it when the card stands
   * first, `caret` (Esc) to the caret as it was. False: a reader, whose browser tabs on.
   */
  const leave = (card: HTMLElement, way: "next" | "back" | "caret") => {
    const { view } = editor;
    if (!view.editable) return false;
    if (way === "caret") { view.focus(); view.dispatch(view.state.tr.scrollIntoView().setMeta("addToHistory", false)); return true; }
    let block: Element | null = card;
    while (block && block.parentElement !== view.dom) block = block.parentElement;
    let beside = way === "next" ? block?.nextElementSibling : block?.previousElementSibling;
    while (beside?.classList.contains("ProseMirror-widget")) beside = way === "next" ? beside.nextElementSibling : beside.previousElementSibling;
    if (!beside) {
      if (way === "next") return false;
      // The card stands first: Shift+Tab goes to what the page has before the editor.
      const before = [...view.dom.ownerDocument.querySelectorAll<HTMLElement>("a[href], button:not([disabled]), input:not([disabled]), select, textarea, [tabindex]")]
        .filter(element => element.tabIndex >= 0 && !view.dom.contains(element) && element.getClientRects().length && (element.compareDocumentPosition(view.dom) & Node.DOCUMENT_POSITION_FOLLOWING));
      before.at(-1)?.focus();
      return !!before.length;
    }
    const pos = view.posAtDOM(beside, 0), $at = view.state.doc.resolve(way === "next" ? pos : pos + (view.state.doc.nodeAt(pos)?.nodeSize ?? 0));
    const target = way === "next" ? Selection.findFrom($at, 1, true) : Selection.findFrom($at, -1, true);
    view.dispatch(view.state.tr.setSelection(target ?? Selection.atStart(view.state.doc)).scrollIntoView());
    view.focus();
    return true;
  };
  return { runtime, outline, show, detail, leave };
}

/** The automatic overview's card: the summary card of the live page, with „Připnout souhrn do stránky“ while it can be written. */
const AutoOverview = defineComponent({
  props: { editor: { type: Object, required: true }, host: { type: Object, default: undefined } },
  setup(props) {
    const editor = toRaw(props.editor) as Editor;
    const host = props.host as ProjectorHost | undefined;
    const { runtime, outline, show, detail, leave } = useEditorOverview(editor, host);
    // One a collaborator pinned meanwhile is the page's summary already: it comes into view instead (W4-8).
    const pin = () => { if (editor.isEditable && !revealOverview(editor, "requirements")) editor.chain().insertContentAt(0, { type: "overview", attrs: { kind: "requirements" } }).run(); };
    // A catalog page's overview counts its definitions, as its summary block would (RequirementSummaryView.vue).
    const catalog = computed(() => !!host && isCatalogPage(editor, host));
    const names = host && (() => catalogNames(editor, host));
    return () => h(RequirementSummaryCard, { items: runtime.index.value.items, outline: outline.value, show, detail, leave, catalogNames: names, auto: true, catalog: catalog.value,
      pin: runtime.editable.value ? pin : undefined,
      viewPage: host?.pageId() });
  },
});

const autoKey = new PluginKey<DecorationSet>("requirementAutoOverview");
/** The Přehled card beside a top-level block of the page (`dir` -1 before it, 1 after it), skipping widgets without one. */
function cardBeside(view: EditorView, index: number, dir: -1 | 1): Element | null {
  let at = index >= 0 && index < view.state.doc.childCount ? view.nodeDOM(view.state.doc.resolve(0).posAtIndex(index)) as Element | null : null;
  for (at = at && (dir < 0 ? at.previousElementSibling : at.nextElementSibling); at; at = dir < 0 ? at.previousElementSibling : at.nextElementSibling) {
    const card = at.matches("[data-requirement-summary-view]") ? at : at.querySelector("[data-requirement-summary-view]");
    if (card) return card;
    if (!at.classList.contains("ProseMirror-widget")) return null;
  }
  return null;
}
const focusCard = (card: Element | null, last: boolean) => {
  const controls = card ? cardControls(card) : [], control = last ? controls.at(-1) : controls[0];
  control?.focus();
  return !!control;
};

/**
 * Into a Přehled card by keyboard (spec §4.2): the editor keeps Tab and the browser does not step into a card inside the editable root.
 * Tab in the paragraph or heading right above a card goes to its first control, Shift+Tab in the one right below it to its last one; Tab from the card's last control comes back below it (useEditorOverview `leave`), so Tab never loops.
 */
function enterCard(view: EditorView, event: KeyboardEvent): boolean {
  if (event.key !== "Tab" || event.altKey || event.ctrlKey || event.metaKey) return false;
  const { selection, doc } = view.state;
  if (!view.editable || !selection.empty || selection.$head.depth < 1) return false;
  const index = selection.$head.index(0);
  // Anywhere in a paragraph or heading right below (Shift+Tab) or above (Tab) a card; a list keeps its own Tab.
  if (!doc.child(index).isTextblock || !focusCard(cardBeside(view, index, event.shiftKey ? -1 : 1), event.shiftKey)) return false;
  event.preventDefault();
  return true;
}

// When Tab was last pressed anywhere: a focus right after it came by keyboard, not by a script (a page opening focuses its editor).
let tabbedAt = -Infinity;
if (typeof window !== "undefined") window.addEventListener("keydown", event => { if (event.key === "Tab" && !event.shiftKey) tabbedAt = performance.now(); }, true);

/** Focus that comes into the editor by Tab from outside, at the page's start, where a card stands first: the card's first control. */
function focusFromOutside(view: EditorView, event: FocusEvent, pressed: boolean) {
  if (pressed || performance.now() - tabbedAt > 100 || !view.editable || (event.relatedTarget instanceof Node && view.dom.contains(event.relatedTarget))) return false;
  if (view.state.selection.from > Selection.atStart(view.state.doc).from) return false;
  const first = view.dom.firstElementChild, card = first?.matches("[data-requirement-summary-view]") ? first : first?.querySelector("[data-requirement-summary-view]");
  // After the browser has placed the caret: the card takes the focus from there.
  if (card) queueMicrotask(() => focusCard(card, false));
  return false;
}

/**
 * The deployment overview at the top of a page with requirement blocks but no summary block (spec §5, owner 27. 10.): the same card
 * as a widget before the document's first block, outside the content; it goes once a summary block is on the page. Only in an
 * editor with a Vue host (a headless one draws nothing); a board hides it (requirements.css).
 */
export function autoOverviewPlugin(editor: Editor, host: ProjectorHost | undefined) {
  const widget = () => Decoration.widget(0, () => {
    const element = document.createElement("div");
    element.className = "atlas-requirement-auto-overview";
    element.setAttribute("contenteditable", "false");
    render(h(AutoOverview, { editor, host }), element);
    return element;
  }, { key: "auto-overview", side: -1, ignoreSelection: true, stopEvent: () => true, destroy: node => render(null, node as HTMLElement) });
  let pressed = false;
  const decorate = (doc: Editor["state"]["doc"]) => showsAutoOverview(outlineOfDoc(doc)) ? DecorationSet.create(doc, [widget()]) : DecorationSet.empty;
  return new Plugin<DecorationSet>({
    key: autoKey,
    state: {
      init: (_config, state) => decorate(state.doc),
      // Only a heading or block added, removed or re-attributed can change whether it shows; typing maps the widget along.
      apply: (tr, set) => !tr.docChanged ? set : reshapesSections(tr) ? decorate(tr.doc) : set.map(tr.mapping, tr.doc),
    },
    // The Vue host comes after the view (it re-creates the node views, which reads this again); a headless editor draws nothing.
    props: {
      decorations: state => (editor as { contentComponent?: unknown }).contentComponent ? autoKey.getState(state) : null,
      // Before every keymap: a heading's own Tab and Shift+Tab (requirementsPlugin.ts) must not take the way into the card.
      handleDOMEvents: {
        keydown: enterCard,
        pointerdown: () => { pressed = true; setTimeout(() => { pressed = false; }, 300); return false; },
        focus: (view, event) => focusFromOutside(view, event, pressed),
      },
    },
  });
}
