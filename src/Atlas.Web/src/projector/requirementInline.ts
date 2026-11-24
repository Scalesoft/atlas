import { closeMarkdownHistory, createMarkdownSuggestionRenderer, dispatchMarkdownHistoryOperation, historyStampText, markMarkdownAutoConversion, pragueNow,
  shouldShowMarkdownSuggestion } from "@atlas/markdown/core";
import { Extension, InputRule, type Editor, type Range } from "@tiptap/core";
import type { Node as PMNode, ResolvedPos } from "@tiptap/pm/model";
import { Plugin, PluginKey, type EditorState, type Transaction } from "@tiptap/pm/state";
import Suggestion, { type SuggestionOptions } from "@tiptap/suggestion";
import { useToast } from "../composables/useToast";
import t from "../i18n/projector";
import { normalizeContact } from "@atlas/markdown/plugins/contacts";
import ContactPicker from "../components/contacts/ContactPicker.vue";
import { linkContact } from "../contacts/contactLinking";
import { learnParty, newPartyId, newPartyRow, type PartyRow } from "./partySuggestions";
import { openVuePopover } from "./openVuePopover";
import { partySource } from "./partySource";
import { findDeadline, findPartyTrigger, type TriggerMatch } from "./requirementPhrases";
import { storageOf } from "./requirementRuntime";
import { isRemote } from "./remoteChange";
import type { RequirementState } from "./requirementTypes";
import { isRequirementHeading, requirementSections } from "./requirementHeading";
import { setHeadingDeadline, setHeadingParty, writeHeadingDeadline, writeHeadingParty } from "./requirementHeadingView";

/*
 * Phrases typed in a requirement heading's title write its facts line (plan 3.1 §2.3a, owner R1; model §3): „čekáme na “ opens the
 * party picker (first row preselected, Enter or Tab takes it, Esc leaves the text), „do pátku “, „do 15. 11. “ … becomes the deadline
 * at once, with a toast to undo it.
 */

interface InlineState {
  /** The requirement whose typed phrase was just made into a fact (it took its space along): the next word typed gets one again. */
  after: string | null;
}
export const inlineKey = new PluginKey<InlineState>("requirementInline");
const initial: InlineState = { after: null };

/** The requirement heading (Atlas 3.6) whose title holds `$pos`, or null: its phrases write its facts line (writeHeadingParty, writeHeadingDeadline). */
function titleHeading($pos: ResolvedPos): { pos: number; node: PMNode; id: string } | null {
  if ($pos.depth !== 1 || !isRequirementHeading($pos.parent)) return null;
  const id = String($pos.parent.attrs.headingId ?? "");
  return id ? { pos: $pos.before(1), node: $pos.parent, id } : null;
}
const factsOf = (doc: PMNode, id: string) => requirementSections(doc).find(section => section.id === id)?.facts;
/** The requirement heading whose title ends at an empty selection. */
function caretAtTitleEnd(state: EditorState) {
  const { selection } = state, block = selection.empty ? titleHeading(selection.$head) : null;
  return block && selection.$head.parentOffset === selection.$head.parent.content.size ? block : null;
}

/** Deletes a typed phrase, with the space before it when nothing but space follows, so the title does not keep a gap. */
function cutStart(doc: PMNode, from: number, to: number) {
  const $from = doc.resolve(from), parent = $from.parent, start = $from.start();
  const before = parent.textBetween(0, from - start, undefined, "\ufffc"), after = parent.textBetween(to - start, parent.content.size, undefined, "\ufffc");
  return from - (/\s$/.test(before) && (!after || /^\s/.test(after)) ? 1 : 0);
}
function cut(tr: Transaction, from: number, to: number) { tr.delete(cutStart(tr.doc, from, to), to); }

/** „do pátku“ typed at the end of a title and then Enter (not a space): the date too, before Enter goes on (requirementsPlugin.ts). */
function deadlineBeforeEnter(editor: Editor) {
  const { state } = editor, $head = state.selection.$head, heading = state.selection.empty && editor.isEditable ? titleHeading($head) : null;
  if (!heading || $head.parentOffset !== $head.parent.content.size) return;
  const before = $head.parent.textBetween(0, $head.parentOffset, undefined, "\ufffc"), found = findDeadline(before + " ", pragueNow().date);
  if (!found) return;
  const was = factsOf(state.doc, heading.id)?.deadline ?? "";
  dispatchMarkdownHistoryOperation(editor, writeHeadingPhrase(state.tr, heading.id, { from: $head.start() + found.index, to: $head.pos }, tr => writeHeadingDeadline(tr, heading.id, found.value)));
  announceDeadline(editor, heading.id, was, found.value);
}

/** A typed phrase cut from a requirement heading's title and what it says written into its facts line, in one step of the history. */
function writeHeadingPhrase(tr: Transaction, id: string, phrase: Range, write: (tr: Transaction) => void) {
  cut(tr, phrase.from, phrase.to);
  write(tr);
  return closeMarkdownHistory(tr.setMeta(inlineKey, { after: id }));
}

/** Every typed date says so, its „Zpět“ at hand: what read as a date may have meant something else. */
function announceDeadline(editor: Editor, id: string, before: string, date: string) {
  const message = before && before !== date ? t.inline.deadlineReplaced(historyStampText({ date: before }), historyStampText({ date })) : t.inline.deadline(historyStampText({ date }));
  useToast().success(message, { undo: () => { setHeadingDeadline(editor, id, before); editor.commands.focus(); } });
}

/** Matches a trigger phrase in the title up to the caret, for the upstream Suggestion. */
const matcher = (find: (before: string) => TriggerMatch | null) => ({ $position }: { $position: ResolvedPos }) => {
  if (!titleHeading($position)) return null;
  const before = $position.parent.textBetween(0, $position.parentOffset, undefined, "\ufffc"), match = find(before);
  return match ? { range: { from: $position.start() + match.index, to: $position.pos }, query: match.query, text: before.slice(match.index) } : null;
};
/** The shared suggestion list, with Tab taking the row as Enter does (the prototype's and the party field's keys). */
function tabPicks<T>(render: NonNullable<SuggestionOptions<T, T>["render"]>): NonNullable<SuggestionOptions<T, T>["render"]> {
  return () => {
    const base = render();
    return { ...base, onKeyDown: props => props.event.key === "Tab" && !props.event.shiftKey
      ? (base.onKeyDown?.({ ...props, event: new KeyboardEvent("keydown", { key: "Enter" }) }), true) : base.onKeyDown?.(props) ?? false };
  };
}

/** „čekáme na “ → the party picker: the page's names, the space's and the people of the directory (partySource.ts). */
function partySuggestion(editor: Editor) {
  const key = new PluginKey("requirementPartyTrigger");
  return Suggestion<PartyRow>({
    editor, pluginKey: key, char: "čekáme na", allowSpaces: true, allowedPrefixes: null, shouldShow: shouldShowMarkdownSuggestion, items: () => [],
    findSuggestionMatch: matcher(findPartyTrigger) as never,
    allow: ({ state, range }) => editor.isEditable && !!titleHeading(state.doc.resolve(range.from)),
    render: () => {
      const source = partySource(editor);
      return tabPicks<PartyRow>(createMarkdownSuggestionRenderer<PartyRow>({ pluginKey: key, label: t.summary.waitingOn, testId: "requirement-party-trigger",
        emptyText: t.inline.partyEmpty, hintText: t.inline.partyHint,
        // Nothing typed yet: the hint, not „Nikdo takový“.
        // „Nový kontakt“ is always the last row, so a name with no match is one Enter away (owner 2026-10-30).
        source: { search: async (query, { signal }) => { const rows = await source(query, signal); return query.trim() && storageOf(editor).host?.people ? [...rows, newPartyRow(query)] : rows; },
          debounceMs: 120, limit: 9, minQueryLength: 1 },
        mapItem: row => row.id === newPartyId ? { id: row.id, title: t.inline.newContactNamed(row.value), description: row.description, icon: "svg:plus" }
          : { id: row.id, title: row.value, description: row.contactId ? t.party.fromDirectory(row.description) : row.description, icon: "svg:user" },
        command: (row, { range }) => row.id === newPartyId ? newPartyContact(editor, range, row.value) : pickParty(editor, range, row) }))();
    },
  });
}

/**
 * „Nový kontakt“ of the party picker: the contact picker's own path (ContactPicker `create`: the directory match and its „Použít
 * existujícího / Vytvořit nového“ question, contactLinking.ts) over the typed name; the contact it gives is the party, as a picked one is.
 */
function newPartyContact(editor: Editor, range: Range, name: string) {
  const people = storageOf(editor).host?.people;
  if (!people || editor.isDestroyed || !editor.isEditable) return;
  let { from, to } = range;
  const follow = ({ transaction }: { transaction: Transaction }) => { from = transaction.mapping.map(from); to = transaction.mapping.map(to); };
  editor.on("transaction", follow);
  const anchor = () => {
    try { const box = editor.view.coordsAtPos(Math.min(from, editor.state.doc.content.size)); return new DOMRect(box.left, box.top, 0, box.bottom - box.top); }
    catch { return editor.view.dom.getBoundingClientRect(); }
  };
  // After the suggestion list has closed, so its teardown does not take the picker with it.
  setTimeout(() => {
    const close = openVuePopover({ editor, anchor: editor.view.dom, positionAnchor: { getBoundingClientRect: anchor }, title: t.inline.newContact, variant: "panel",
      component: ContactPicker, closed: () => editor.off("transaction", follow),
      props: { search: people, link: linkContact, normalize: normalizeContact, create: name, pick: (contact: { name: string; objectId?: string }) => {
        close();
        if (editor.isDestroyed) return;
        pickParty(editor, { from, to }, { id: newPartyId, value: contact.name, description: "", ...(contact.objectId ? { contactId: contact.objectId } : {}) });
      } } });
  });
}

function pickParty(editor: Editor, range: Range, row: PartyRow) {
  const heading = titleHeading(editor.state.doc.resolve(range.from));
  if (heading && editor.isEditable) pickHeadingParty(editor, range, row, heading.id);
}

/** „čekáme na X“ in a requirement heading: X the party of its facts line, the state čeká na vstup, the phrase gone, one step of the history. */
function pickHeadingParty(editor: Editor, range: Range, row: PartyRow, id: string) {
  const was = factsOf(editor.state.doc, id), state: RequirementState | undefined = was?.state;
  const typed = findPartyTrigger(editor.state.doc.textBetween(range.from, range.to, undefined, "\ufffc"))?.query ?? "";
  dispatchMarkdownHistoryOperation(editor, writeHeadingPhrase(editor.state.tr, id, range, tr => writeHeadingParty(tr, id, row.value, row.contactId ?? "", "waiting_for_input")));
  learnParty(row, typed);
  editor.commands.focus();
  const replaced = was?.party && was.party !== row.value;
  useToast().success(replaced ? t.inline.partyReplaced(was!.party, row.value) : t.inline.partyWaiting(row.value),
    { undo: () => { setHeadingParty(editor, id, was?.party ?? "", was?.partyContactId ?? "", state); editor.commands.focus(); } });
}

/** The typed phrases of requirement headings in an editor (requirementsPlugin.ts registers it). */
export const RequirementInline = Extension.create({
  name: "requirementInline",
  // Before the editor's keys.
  priority: 1001,
  // „… do pátku“ and Enter in a requirement heading: the date first, then the heading's own Enter (requirementHeadingView.ts, priority 200).
  addKeyboardShortcuts: () => ({ Enter: ({ editor }) => { if (titleHeading(editor.state.selection.$head)) deadlineBeforeEnter(editor); return false; } }),
  addInputRules() {
    const editor = this.editor;
    return [
      // „do pátku “: the date at once, the phrase gone; Backspace right after (core chipText: as deleting that space) or Ctrl+Z gives
      // the text back without the date; a space typed again makes the date again.
      new InputRule({
        undoable: false,
        // Enter runs the rules with "\n": that one is deadlineBeforeEnter's, which lets Enter go on.
        find: text => { const found = text.endsWith("\n") ? null : findDeadline(text, pragueNow().date); return found ? { index: found.index, text: found.text, data: { date: found.value } } : null; },
        handler: ({ state, range, match }) => {
          // Tiptap (3.33+) puts the typed space into the document before the rule runs: `range` ends after it, and the document the rule
          // sees is one character longer than the editor's. Any other run (an IME commit, a pasted word) is not that: it stays text.
          const typed = state.doc.content.size - editor.state.doc.content.size === 1 && state.doc.textBetween(range.from, range.to - 1) === match[0].slice(0, -1);
          const heading = editor.isEditable && typed ? titleHeading(state.doc.resolve(range.from)) : null;
          if (!heading) return null;
          const before = factsOf(state.doc, heading.id)?.deadline ?? "", start = cutStart(state.doc, range.from, range.to), phrase = state.doc.textBetween(start, range.to - 1);
          writeHeadingPhrase(state.tr, heading.id, range, tr => writeHeadingDeadline(tr, heading.id, match.data!.date as string));
          markMarkdownAutoConversion(state.tr, { from: start, to: start, text: phrase, revert: tr => { writeHeadingDeadline(tr, heading.id, before); tr.setMeta(inlineKey, { after: null }); } });
          announceDeadline(editor, heading.id, before, match.data!.date as string);
        },
      }),
    ];
  },
  addProseMirrorPlugins() {
    const editor = this.editor;
    return [
      partySuggestion(editor),
      new Plugin<InlineState>({
        key: inlineKey,
        state: {
          init: () => initial,
          apply(tr, previous) {
            const meta = tr.getMeta(inlineKey) as Partial<InlineState> | undefined;
            // Typing, a click or an arrow of this editor ends the made phrase's space; a collaborator's change does not.
            const moved = (tr.docChanged || tr.selectionSet) && !isRemote(tr);
            if (!meta && !moved) return previous;
            const next: InlineState = { ...(moved ? { after: null } : previous), ...meta };
            return next.after === previous.after ? previous : next;
          },
        },
        props: {
          // Typing on after a phrase was made into a fact: it took its space with it, so the next word gets one again.
          handleTextInput: (view, from, to, text) => {
            const inline = inlineKey.getState(view.state)!, at = from === to && inline.after ? caretAtTitleEnd(view.state) : null;
            if (!at || at.id !== inline.after || /^\s/.test(text) || !/\S$/.test(view.state.selection.$head.parent.textContent)) return false;
            view.dispatch(view.state.tr.insertText(` ${text}`, from, to));
            return true;
          },
        },
      }),
    ];
  },
});
