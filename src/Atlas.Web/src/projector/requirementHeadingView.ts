import { Extension, type Editor } from "@tiptap/core";
import { Fragment, Slice, type Node as PMNode } from "@tiptap/pm/model";
import { Plugin, PluginKey, Selection, TextSelection, type EditorState, type Transaction } from "@tiptap/pm/state";
import { Mapping, ReplaceAroundStep, ReplaceStep } from "@tiptap/pm/transform";
import { Decoration, DecorationSet, type EditorView } from "@tiptap/pm/view";
import { createMarkdownChip, createSuggestionList, dispatchMarkdownHistoryOperation, historyStampText, openMarkdownContextPopover, openMarkdownModuleForm, openMarkdownRowMenu,
  formatMarkdownShortcut, pragueNow, type MarkdownIntention, type MarkdownPopoverAnchor, type MarkdownRowMenuRow } from "@atlas/markdown/core";
import { navigateToHeading } from "@atlas/markdown/plugins/heading-anchors";
import { pageSectionSpec, type MarkdownPageSection } from "@atlas/markdown/plugins/pages";
import { watch } from "vue";
import { headingKey, isFolded, setFolded, unfoldTo } from "../editor/headingFold";
import t from "../i18n/projector";
import { lazyVueComponent, openVuePopover } from "./openVuePopover";
import { partySource } from "./partySource";
import { isRemote } from "./remoteChange";
import { historyStamp, historyText } from "./requirementHistory";
import { directionIcons, stateIcons } from "./requirementLook";
import { awaitedBy, keyed, parseDate, requiredKeys, satisfied, type PageRequirement } from "./requirementModel";
import type { RemoteRequirement } from "./requirementLinks";
import { blockedReason, historyList, isReasonNote, touchesOutline } from "./requirementOverview";
import { runtimeOf, storageOf } from "./requirementRuntime";
import { stateText } from "./requirementSummary";
import { requirementDirections, type RequirementDirection, type RequirementState } from "./requirementTypes";
import { headingIdPattern, headingRequirements, isDependencyLine, isFactsLine, isRequirementHeading, requirementSections, sectionAt, sectionTarget, sectionUrl, type RequirementSection } from "./requirementHeading";
import "./requirementHeading.css";

/*
 * The editor side of a requirement heading (model §1b, §3; owner 2026-11-04: no card, no node view): the heading's own fold chevron,
 * a task checkbox before the title (checked = hotovo) and the editor's chips after it (state · kind · service · „čeká na N“), all
 * decoration widgets of the existing heading; pickers on the chips; the dependency line's rules; Enter and Backspace as a heading.
 * A reader gets the same boxes: the checkbox inert (aria-disabled, no Tab stop), the value chips open a read-only card, „čeká na N“ its list.
 */

/** The kinds' words as the tool shows them (getters: read where shown, after the language has arrived). */
export const kindWords: Record<RequirementDirection, string> = {
  get internal() { return t.heading.kind.internal; }, get inbound() { return t.heading.kind.inbound; }, get outbound() { return t.heading.kind.outbound; },
};
const stateMenu = lazyVueComponent(() => import("./RequirementStateMenu.vue"));
/** The state chip's menu is a chunk of its own: an editable editor fetches it when the page idles, so the first chip opens at once. */
export const warmStateMenu = () => stateMenu.prefetchWhenIdle();

const sectionOf = (doc: PMNode, id: string) => requirementSections(doc).find(section => section.id === id);
/** The page this editor shows, for the section links („#/pages/<id>?heading=…“); none in a gallery without a host. */
const pageOf = (editor: Editor) => storageOf(editor)?.host?.pageId() ?? undefined;

/** Prerequisites of other pages as the server read them for this reader (requirementLinks.ts), by `headingId@pageId`. */
const remoteOf = (editor: Editor): ReadonlyMap<string, RemoteRequirement> => storageOf(editor)?.links.value.remote ?? new Map();
/** Whether a section link points to this page: its page id, else (no host page) a requirement heading of this document has its id. */
const onThisPage = (target: { pageId: string; headingId: string }, ids: ReadonlyMap<string, unknown>, pageId?: string) =>
  pageId ? target.pageId.toLowerCase() === pageId.toLowerCase() : ids.has(target.headingId) || !target.pageId;

interface Awaited { id: string; title: string; state?: RequirementState; pageId?: string; pageTitle?: string; unknown?: boolean }
/**
 * What each requirement waits for, by the index's own rule (pageRequirements' `requires`, keyed, awaitedBy; model §4): a dependency on a
 * requirement of this page or a readable one of another page that is not finished. A target that is no requirement (a plain heading,
 * gone, not read yet, not readable) is „neznámé“: listed, never counted in „čeká na N“ nor asked about at „Hotovo“ (`count`). The same
 * answer whether the page id is known yet or not, so the chips do not change on the first keystroke (review of G1).
 */
function awaited(doc: PMNode, pageId?: string, remote: ReadonlyMap<string, RemoteRequirement> = new Map()) {
  const sections = requirementSections(doc), items = headingRequirements(doc, pageId);
  const byKey = new Map<string, PageRequirement>([...keyed(items), ...[...remote].filter(([, entry]) => entry.remote.readable)]);
  return new Map(sections.map((section, index) => {
    const item = items[index]!, counted = new Set<PageRequirement>(awaitedBy(item, byKey)), keys = requiredKeys(item.attrs);
    const list = satisfied(item.attrs.state) ? [] : section.facts.dependencies.flatMap((dependency, at): Awaited[] => {
      const entry = byKey.get(keys[at] ?? ""), other = remote.get(`${dependency.headingId}@${dependency.pageId}`);
      const where = entry && other ? { pageId: dependency.pageId, pageTitle: other.remote.pageTitle ?? "" } : {};
      if (!entry) return [{ id: dependency.headingId, title: dependency.title, unknown: true, ...(other ? { pageId: dependency.pageId } : {}) }];
      return counted.has(entry) ? [{ id: dependency.headingId, title: entry.title, state: entry.attrs.state, ...where }] : [];
    });
    return [section.id, { list, count: list.filter(entry => !entry.unknown).length }] as const;
  }));
}

// ——— Writing: every confirmed action is one transaction and one undo step ———

function change(editor: Editor, id: string, build: (tr: Transaction, section: RequirementSection) => void) {
  const section = editor.isEditable ? sectionOf(editor.state.doc, id) : undefined;
  if (!section) return false;
  const tr = editor.state.tr;
  build(tr, section);
  if (!tr.docChanged) return false;
  dispatchMarkdownHistoryOperation(editor, tr);
  return true;
}
const setAttrs = (tr: Transaction, section: RequirementSection, attrs: Record<string, unknown>) => {
  Object.entries(attrs).forEach(([name, value]) => { if (tr.doc.nodeAt(section.headingPos)!.attrs[name] !== value) tr.setNodeAttribute(section.headingPos, name, value); });
  // 3.5's „hotovo“ day: a state change to done dates the facts line (beside the deadline), any other state takes it off; the same step.
  if (typeof attrs.state === "string") {
    const completed = attrs.state === "done" ? section.facts.completed || pragueNow().date : "";
    if (completed !== section.facts.completed) writeFacts(tr, section, section.facts.deadline, completed);
  }
};

/** What the checkbox replaced, per editor and requirement, so unchecking brings it back (this session only). */
const previousStates = new WeakMap<Editor, Map<string, { state: RequirementState; stateLabel: string }>>();
/** The checkbox the user just ticked („done“) or unticked („undone“): its redrawn box plays that once (requirementHeading.css `data-rq-toggled`). */
const justToggled = new Map<string, "done" | "undone">();

/** How many prerequisites requirement `id` still waits for (the state menu's and the checkbox's question). */
const missingOf = (editor: Editor, id: string) => awaited(editor.state.doc, pageOf(editor), remoteOf(editor)).get(id)?.count ?? 0;

/**
 * The checkbox: hotovo, and back to the state it had (else neřešeno). Checking with missing prerequisites asks first, as the state menu
 * does („Ještě chybí N — označit přesto?“, `confirmed` once answered); unchecking never asks.
 */
export function toggleDone(editor: Editor, id: string, confirmed = false) {
  const memory = previousStates.get(editor) ?? previousStates.set(editor, new Map()).get(editor)!;
  const section = sectionOf(editor.state.doc, id);
  if (section && section.facts.state !== "done" && !confirmed && editor.isEditable && missingOf(editor, id)) { openStateMenu(editor, id, false, undefined, true); return true; }
  return change(editor, id, (tr, section) => {
    const { state, stateLabel } = section.facts;
    if (state === "done") { setAttrs(tr, section, memory.get(id) ?? { state: "planned", stateLabel: "" }); memory.delete(id); justToggled.set(id, "undone"); return; }
    memory.set(id, { state, stateLabel });
    justToggled.set(id, "done");
    setAttrs(tr, section, { state: "done", stateLabel: "" });
  });
}

export const setHeadingState = (editor: Editor, id: string, state: RequirementState, stateLabel = "") =>
  change(editor, id, (tr, section) => { previousStates.get(editor)?.delete(id); setAttrs(tr, section, { state, stateLabel }); if (state === "done" && section.facts.state !== "done") justToggled.set(id, "done"); });

/**
 * The facts line („do 15. 11. 2026 · hotovo 5. 11. 2026“, either part alone too) written: an empty `date` / `completed` leaves that part
 * out, and the line goes with both. `completed` defaults to the one the line has, so a deadline edit keeps it.
 */
function writeFacts(tr: Transaction, section: RequirementSection, date: string, completed = section.facts.completed) {
  const schema = tr.doc.type.schema, line = section.factsPos === undefined ? null : tr.doc.nodeAt(section.factsPos);
  const text = [date && `do ${historyStampText({ date })}`, completed && `hotovo ${historyStampText({ date: completed })}`].filter(Boolean).join(" · ");
  const content = text ? [schema.text(text)] : [];
  if (line && section.factsPos !== undefined) {
    if (content.length) tr.replaceWith(section.factsPos + 1, section.factsPos + line.nodeSize - 1, Fragment.from(content));
    else tr.delete(section.factsPos, section.factsPos + line.nodeSize);
  } else if (content.length) {
    const deps = section.depsPos === undefined ? null : tr.doc.nodeAt(section.depsPos);
    const at = deps ? section.depsPos! + deps.nodeSize : section.headingPos + tr.doc.nodeAt(section.headingPos)!.nodeSize;
    tr.insert(at, schema.nodes.paragraph!.create(null, content));
  }
}

/** The party (and a state with it) of requirement `id` written into `tr` as the heading's attributes (the typed „čekáme na X“ too, requirementInline.ts). */
export function writeHeadingParty(tr: Transaction, id: string, party: string, contactId = "", state?: RequirementState) {
  const section = sectionOf(tr.doc, id);
  if (!section) return false;
  const name = party.trim().slice(0, 200);
  setAttrs(tr, section, { ...(state ? { state, stateLabel: "" } : {}), party: name, partyContactId: name ? contactId : "" });
  return true;
}
/** The deadline of requirement `id` as the text „do 15. 11. 2026“ in its facts line, written into `tr`; "" removes it. */
export function writeHeadingDeadline(tr: Transaction, id: string, date: string) {
  const section = sectionOf(tr.doc, id);
  if (section) writeFacts(tr, section, date);
  return !!section;
}
export const setHeadingParty = (editor: Editor, id: string, party: string, contactId = "", state?: RequirementState) =>
  change(editor, id, tr => { writeHeadingParty(tr, id, party, contactId, state); });
export const setHeadingDeadline = (editor: Editor, id: string, date: string) => change(editor, id, tr => { writeHeadingDeadline(tr, id, date); });

/** A dependency: a section link at the end of the dependency line, the line made under the heading when it is missing. */
export function addHeadingDependency(editor: Editor, id: string, target: { headingId: string; title: string; pageId?: string }) {
  const pageId = target.pageId ?? pageOf(editor);
  if (!pageId) return false;
  return change(editor, id, (tr, section) => {
    const schema = tr.doc.type.schema, link = schema.nodes.pageReference?.create({ id: pageId, title: target.title || target.headingId, url: sectionUrl(pageId, target.headingId) });
    if (!link || section.facts.dependencies.some(dependency => dependency.headingId === target.headingId && dependency.pageId === pageId)) return;
    const deps = section.depsPos === undefined ? null : tr.doc.nodeAt(section.depsPos);
    if (deps) tr.insert(section.depsPos! + deps.nodeSize - 1, deps.childCount ? [schema.text(", "), link] : [link]);
    else tr.insert(section.headingPos + tr.doc.nodeAt(section.headingPos)!.nodeSize, schema.nodes.paragraph!.create({ dependencies: true }, link));
  });
}

/**
 * A new ID (model §4): the heading takes it and keeps the former one in `aliases`; the dependency lines and `?heading=` links of this
 * page that named it follow, all in one step. Throws a Czech message for an ID the page cannot take.
 */
export function renameHeadingId(editor: Editor, id: string, next: string) {
  next = next.trim();
  if (next === id) return false;
  if (!headingIdPattern.test(next)) throw new TypeError(t.heading.idRule);
  let taken = false;
  editor.state.doc.descendants(node => { taken ||= node.type.name === "heading" && node.attrs.headingId === next; return !taken && !node.isTextblock; });
  if (taken) throw new TypeError(t.heading.idTaken(next));
  const pageId = pageOf(editor), ours = (target: { pageId: string } | null) => !!target && (!pageId || target.pageId === pageId);
  const renamed = change(editor, id, (tr, section) => {
    const aliases = [...new Set([...String(tr.doc.nodeAt(section.headingPos)!.attrs.aliases ?? "").split(",").filter(Boolean), id])].join(",");
    setAttrs(tr, section, { headingId: next, aliases: aliases.length > 1000 ? aliases.slice(aliases.length - 1000).replace(/^[^,]*,/, "") : aliases });
    tr.doc.descendants((node, pos) => {
      const target = node.type.name === "pageReference" ? sectionTarget(node.attrs.url) : null;
      if (target?.headingId === id && ours(target)) tr.setNodeAttribute(pos, "url", sectionUrl(target.pageId, next));
      const link = linkMark(node), href = String(link?.attrs.href ?? "");
      const linked = link && (sectionTarget(href) ?? (/^[?#]heading=/.test(href) ? { pageId: pageId ?? "", headingId: href.slice(9) } : null));
      if (link && linked?.headingId === id && (ours(linked) || /^[?#]heading=/.test(href)))
        tr.removeMark(pos, pos + node.nodeSize, link).addMark(pos, pos + node.nodeSize, link.type.create({ ...link.attrs, href: href.replace(`heading=${id}`, `heading=${next}`) }));
    });
  });
  // The reader's fold is kept by page and id (foldState.ts): the section stays folded under its new one.
  const [was, now] = [headingKey(id, pageId), headingKey(next, pageId)];
  if (renamed && isFolded(was)) { setFolded(now, true); setFolded(was, false); }
  return renamed;
}
const linkMark = (node: PMNode) => node.isText ? node.marks.find(mark => mark.type.name === "link") : undefined;

/** A PM's record (plan 3.4 §3) as the old block's `record`: the state and a dated line at the top of the section's history list. */
export function recordHeading(editor: Editor, id: string, action: "asked" | "urged" | "delivered" | "blocked", reason = "", asked?: { party: string; contactId: string }) {
  const section = sectionOf(editor.state.doc, id);
  if (!section || !editor.isEditable) return false;
  if (action === "asked" && !asked && !section.facts.party) { openStateMenu(editor, id, true, (party, contactId) => recordHeading(editor, id, "asked", "", { party, contactId })); return true; }
  const party = asked?.party ?? section.facts.party, named = (word: string) => party ? `${word}: ${party}` : word;
  const [state, line] = ({ asked: ["waiting_for_input", named("Vyzváno")], urged: ["waiting_for_input", named("Urgováno")], delivered: ["done", named("Dodáno")],
    blocked: ["blocked", reason ? `Blokováno: ${reason}` : "Blokováno"] } as const)[action];
  return change(editor, id, tr => {
    setAttrs(tr, sectionOf(tr.doc, id)!, { state, stateLabel: "" });
    if (asked) writeHeadingParty(tr, id, asked.party, asked.contactId);
    addHistoryLine(tr, sectionOf(tr.doc, id)!, historyText(line));
  });
}

/**
 * A dated line at the top of the section's history list (the list made after the section's last block that is not an empty paragraph
 * when it has none, so it is not left below the blank lines the writer keeps before the next heading); the end of its text.
 */
function addHistoryLine(tr: Transaction, section: RequirementSection, text: string) {
  const schema = tr.doc.type.schema, { nodes } = schema;
  let list: PMNode | undefined, at = -1, after = section.pos;
  tr.doc.nodesBetween(section.pos, section.end, (node, pos) => {
    if (at < 0 && historyList(node.type.name) && historyStamp(node.firstChild?.firstChild?.textContent ?? "")) { list = node; at = pos; }
    if (node.type.name !== "paragraph" || node.content.size) after = pos + node.nodeSize;
    return false;
  });
  // The history is the editor's timed list (3.6, as a loaded page reads a dated bullet list); an older bullet list takes its own items.
  // Looking for bullet lists only, a second „Komentář“ on a page read from the server started a second list.
  const timed = (list ? list.type.name === "timedList" : !!nodes.timedList) && !!nodes.timedItem;
  const item = (timed ? nodes.timedItem! : nodes.listItem!).create(null, nodes.paragraph!.create(null, schema.text(text)));
  if (at >= 0) tr.insert(at + 1, item); else tr.insert(after, (timed ? nodes.timedList! : nodes.bulletList!).create(null, item));
  return (at >= 0 ? at + 1 : after + 1) + item.nodeSize - 2;
}

/** Ctrl+Alt+M in a requirement's section (the old block's comment, plan 3.4 §3): a dated line in its history list, the caret after the stamp. */
export function noteHeading(editor: Editor) {
  const { $head } = editor.state.selection, section = editor.isEditable && $head.depth ? sectionAt(editor.state.doc, $head.before(1)) : undefined;
  if (!section) return false;
  const tr = editor.state.tr, end = addHistoryLine(tr, section, historyText(""));
  dispatchMarkdownHistoryOperation(editor, tr.setSelection(TextSelection.create(tr.doc, end)).scrollIntoView());
  return true;
}

// ——— Pickers ———

/** The chip whose popover was opened last, per editor: the popover marks the editor (its anchor) expanded, the chip shows it (`aria-expanded`). */
const opened = new WeakMap<Editor, { id: string; chip: string }>();
const openFrom = (editor: Editor, id: string, chip: string) => editor.view.dom.getAttribute("aria-expanded") === "true" && opened.get(editor)?.id === id && opened.get(editor)?.chip === chip;
function syncChips(editor: Editor) {
  // Once the popover has closed the record is spent: another popover anchored on the editor must not mark the last chip.
  if (editor.view.dom.getAttribute("aria-expanded") !== "true") opened.delete(editor);
  editor.view.dom.querySelectorAll<HTMLElement>(".rq-chips > [data-rq-chip]").forEach(chip =>
    chip.setAttribute("aria-expanded", String(openFrom(editor, (chip.parentElement as HTMLElement).dataset.rqId!, chip.dataset.rqChip!))));
}

/** Where a picker of a heading's chip stands: the chip as drawn now (a redraw replaces it), else the heading. */
function chipAnchor(editor: Editor, id: string, chip: string): MarkdownPopoverAnchor {
  opened.set(editor, { id, chip });
  const find = () => editor.view.dom.querySelector(`[data-rq-id="${CSS.escape(id)}"] [data-rq-chip="${chip}"]`) ?? editor.view.dom.querySelector(`[data-heading-id="${CSS.escape(id)}"]`) ?? editor.view.dom;
  return { contextElement: editor.view.dom, getBoundingClientRect: () => find().getBoundingClientRect() };
}

/**
 * The state menu (RequirementStateMenu), the current state focused: „Čeká na vstup“ asks on whom, „Hotovo“ with open prerequisites asks
 * first; `askDone` opens it on that question (the checkbox), its „Označit přesto“ then checks as the checkbox does.
 */
export function openStateMenu(editor: Editor, id: string, asking = false, onParty?: (party: string, contactId: string) => void, askDone = false) {
  const section = sectionOf(editor.state.doc, id);
  if (!section || !editor.isEditable) return;
  const { facts } = section, positionAnchor = chipAnchor(editor, id, "state");
  openVuePopover({ component: stateMenu, stub: { rows: 7, rowHeight: 32 }, editor, anchor: editor.view.dom, positionAnchor, title: asking ? t.summary.waitingOn : t.heading.stateTitle, variant: asking ? "panel" : "menu",
    props: { state: facts.state, party: facts.party, partyContactId: facts.partyContactId, stateLabel: facts.stateLabel, missing: missingOf(editor, id),
      asking, askDone, focus: facts.state, suggest: asking ? partySource(editor) : undefined,
      pick: (next: RequirementState, who?: string, contact?: string) => onParty ? who && onParty(who, contact ?? "")
        : who !== undefined ? setHeadingParty(editor, id, who, contact ?? "", next) : askDone && next === "done" ? toggleDone(editor, id, true) : setHeadingState(editor, id, next),
      label: (text: string, next: RequirementState) => setHeadingState(editor, id, next, text),
      ask: () => openStateMenu(editor, id, true, onParty), back: () => openStateMenu(editor, id) } });
}

/** A menu of rows (the editor's context menu: arrows walk them), the current one checked and focused, else the first. */
type Row = MarkdownRowMenuRow;
const openRows = (editor: Editor, id: string, chip: string, title: string, rows: Row[], readonly = false) =>
  openMarkdownRowMenu({ editor, anchor: editor.view.dom, positionAnchor: chipAnchor(editor, id, chip), title, rows, readonly, className: "atlas-requirement-popover" });

/** What the requirement waits for, each row a jump to it (another page's opens that page at the section): the reader's too. */
export function openAwaited(editor: Editor, id: string) {
  const open = awaited(editor.state.doc, pageOf(editor), remoteOf(editor)).get(id)?.list ?? [];
  openRows(editor, id, "ready", t.heading.awaitedTitle, open.map(entry => ({ label: entry.title || entry.id, icon: entry.state ? stateIcons[entry.state] : "circle-help",
    detail: [entry.state ? stateText({ state: entry.state, stateLabel: "" }) : t.heading.unknown, entry.pageTitle].filter(Boolean).join(" · "),
    run: () => entry.pageId ? editor.view.dom.ownerDocument.defaultView?.location.assign(sectionUrl(entry.pageId, entry.id)) : goToHeading(editor, entry.id) })), true);
}

/** A reader's value chip (contract J3.5): the requirement's values as a card without controls. */
function openFacts(editor: Editor, id: string, chip: string) {
  const facts = sectionOf(editor.state.doc, id)?.facts;
  if (!facts) return;
  const kind = kindWords[facts.direction], rows: Omit<Row, "run">[] = [{ label: stateText(facts), icon: stateIcons[facts.state], detail: t.heading.stateDetail },
    { label: facts.party ? `${kind} · ${facts.party}` : kind, icon: directionIcons[facts.direction], detail: t.heading.kindDetail },
    ...(facts.system ? [{ label: facts.system, icon: "plug", detail: t.heading.serviceDetail }] : []),
    ...(facts.deadline ? [{ label: historyStampText({ date: facts.deadline }), icon: "calendar", detail: t.heading.deadlineDetail }] : [])];
  openRows(editor, id, chip, t.model.label, rows.map(row => ({ ...row, run: () => undefined })), true);
}

/** To a heading of this page, its folded sections opened: the caret at a requirement's end while writing, else the heading focused (navigateToHeading). */
export function goToHeading(editor: Editor, id: string) {
  const element = editor.view.dom.querySelector(`[data-heading-id="${CSS.escape(id)}"]`);
  if (element) unfoldTo(element);
  const section = sectionOf(editor.state.doc, id);
  if (!section || !editor.isEditable) { navigateToHeading(editor.view.dom, id); return; }
  const end = section.headingPos + editor.state.doc.nodeAt(section.headingPos)!.nodeSize - 1;
  editor.chain().focus().setTextSelection(end).scrollIntoView().run();
}

type Picker = { chip: string; title: string } & ({ rows: (editor: Editor, id: string, section: RequirementSection) => Row[] }
  | { label: string; value: (section: RequirementSection) => string; save: (editor: Editor, id: string, value: string) => void });
/** A heading's pickers by name (its chips, Ctrl+., the PM's B): a menu of rows, or one text field with „Použít“ whose `save` throws a Czech message to keep it open. */
const pickers: Record<"kind" | "service" | "deadline" | "id" | "blocked", Picker> = {
  kind: { chip: "kind", get title() { return t.heading.kindTitle; }, rows: (editor, id, section) => requirementDirections.map(direction => ({ label: kindWords[direction], icon: directionIcons[direction],
    checked: direction === section.facts.direction, run: () => change(editor, id, (tr, now) => setAttrs(tr, now, { direction })) })) },
  service: { chip: "service", get title() { return t.heading.serviceTitle; }, get label() { return t.heading.serviceLabel; }, value: section => section.facts.system,
    save: (editor, id, value) => { change(editor, id, (tr, now) => setAttrs(tr, now, { system: value.trim() })); } },
  deadline: { chip: "state", get title() { return t.heading.deadlineTitle; }, get label() { return t.heading.deadlineLabel; },
    value: section => section.facts.deadline ? historyStampText({ date: section.facts.deadline }) : "",
    save: (editor, id, value) => {
      const date = parseDate(value, pragueNow().date);
      if (date === null) throw new TypeError(t.heading.deadlineRule);
      setHeadingDeadline(editor, id, date);
    } },
  id: { chip: "state", get title() { return t.heading.idTitle; }, get label() { return t.heading.idLabel; }, value: section => section.id,
    save: (editor, id, value) => { renameHeadingId(editor, id, value); } },
  blocked: { chip: "state", get title() { return t.heading.blockedTitle; }, get label() { return t.heading.blockedLabel; }, value: () => "",
    save: (editor, id, value) => { recordHeading(editor, id, "blocked", value.trim()); } },
};

export function openPicker(editor: Editor, id: string, name: keyof typeof pickers) {
  const section = sectionOf(editor.state.doc, id), picker = pickers[name];
  if (!section || !editor.isEditable) return;
  if ("rows" in picker) { openRows(editor, id, picker.chip, picker.title, picker.rows(editor, id, section)); return; }
  // The editor's module form (as the timed stamp's): the label, one field, „Použít“; a `save` that throws keeps it open with the message.
  openMarkdownModuleForm({ editor, anchor: editor.view.dom, positionAnchor: chipAnchor(editor, id, picker.chip), title: picker.title,
    fields: [{ key: "value", label: picker.label }], values: { value: picker.value(section) },
    save({ value = "" }) {
      if (value.length > 200) throw new TypeError(t.heading.maxLength);
      picker.save(editor, id, value);
    } });
}

/** How well a title fits the typed text (lower case), lower first: the same, starting with it, holding it, matched only word by word or by key. */
export function matchRank(title: string, query: string) {
  const text = title.toLocaleLowerCase("cs");
  return !query ? 0 : text === query ? 0 : text.startsWith(query) ? 1 : text.includes(query) ? 2 : 3;
}

/**
 * „+ závislost“: the page's other requirements, typing filters, the first preselected, Enter takes it (createSuggestionList); typed,
 * also the space's other pages' requirements (the server's search the old block's picker asks, requirementInline.ts) after them.
 */
export function openDependencyPicker(editor: Editor, id: string) {
  const section = sectionOf(editor.state.doc, id), pageId = pageOf(editor), host = storageOf(editor)?.host;
  if (!section || !editor.isEditable || !pageId) return;
  const taken = new Set(section.facts.dependencies.map(dependency => `${dependency.headingId}@${dependency.pageId}`));
  const candidates = requirementSections(editor.state.doc).filter(other => other.id !== id && !taken.has(`${other.id}@${pageId}`)).map(other => other.facts);
  let others: { id: string; headingId: string; title: string; pageId: string; pageTitle: string; state: RequirementState }[] = [];
  let timer: ReturnType<typeof setTimeout> | undefined, abort: AbortController | undefined;
  openMarkdownContextPopover({ editor, anchor: editor.view.dom, positionAnchor: chipAnchor(editor, id, "state"), title: t.heading.addDependency, className: "atlas-requirement-popover",
    render(container, close) {
      const doc = container.ownerDocument, input = doc.createElement("input");
      input.className = "md-input"; input.placeholder = t.heading.searchRequirement; input.setAttribute("aria-label", t.heading.dependsOn);
      const list = createSuggestionList({ document: doc, label: t.heading.requirements, emptyText: t.heading.noRequirement, onSelect: picked => {
        const target = candidates.find(item => item.id === picked), other = others.find(item => item.id === picked);
        close();
        if (target) addHeadingDependency(editor, id, { headingId: target.id, title: target.title });
        else if (other) addHeadingDependency(editor, id, { headingId: other.headingId, title: other.title, pageId: other.pageId });
      } });
      const show = () => {
        const query = input.value.trim().toLocaleLowerCase("cs"), words = query.split(/\s+/).filter(Boolean);
        list.update([...candidates.filter(item => words.every(word => `${item.title} ${item.id}`.toLocaleLowerCase("cs").includes(word)))
          .sort((a, b) => matchRank(a.title, query) - matchRank(b.title, query)).map(item => ({ id: item.id, title: item.title || item.id, description: stateText(item), icon: `svg:${stateIcons[item.state]}` })),
        ...others.map(item => ({ id: item.id, title: item.title, description: t.heading.otherPage(item.pageTitle), icon: `svg:${stateIcons[item.state] ?? "link"}` }))]);
      };
      const search = () => {
        clearTimeout(timer); abort?.abort(); others = [];
        const query = input.value.trim(), spaceId = host?.spaceId();
        if (!query || !spaceId || !host?.api.searchRequirements) return;
        const signal = (abort = new AbortController()).signal;
        timer = setTimeout(() => void host.api.searchRequirements!(spaceId, query, signal, pageId).then(rows => {
          if (signal.aborted) return;
          // The server lists by page title: the eight shown are the best matches, so the page's own name does not decide what fits.
          const typed = query.toLocaleLowerCase("cs");
          others = rows.filter(row => row.pageId !== pageId && row.requirement.key && !taken.has(`${row.requirement.key}@${row.pageId}`))
            .sort((a, b) => matchRank(a.requirement.title, typed) - matchRank(b.requirement.title, typed)).slice(0, 8)
            .map(row => ({ id: `${row.requirement.key}@${row.pageId}`, headingId: row.requirement.key, title: row.requirement.title || row.requirement.key, pageId: row.pageId,
              pageTitle: row.pageTitle, state: row.requirement.state as RequirementState }));
          show();
        }, () => undefined), 150);
      };
      input.addEventListener("input", () => { search(); show(); });
      input.addEventListener("keydown", event => { if (["ArrowDown", "ArrowUp", "Enter"].includes(event.key) && list.onKeyDown(event)) event.preventDefault(); });
      container.append(input, list.element);
      show();
      queueMicrotask(() => input.focus({ preventScroll: true }));
      return () => { clearTimeout(timer); abort?.abort(); list.destroy(); };
    } });
}

/** The PM's actions, a letter on the state chip each (as in the old detail card) and a row of Ctrl+.: V vyzvali, U urgovali, H dodáno, B blokuje. */
const actions = [
  { letter: "v", key: "asked", get label() { return t.heading.actions.asked; }, icon: "hourglass", run: (editor: Editor, id: string) => recordHeading(editor, id, "asked") },
  { letter: "u", key: "urged", get label() { return t.heading.actions.urged; }, icon: "clock", run: (editor: Editor, id: string) => recordHeading(editor, id, "urged") },
  { letter: "h", key: "delivered", get label() { return t.heading.actions.delivered; }, icon: "circle-check", run: (editor: Editor, id: string) => recordHeading(editor, id, "delivered") },
  { letter: "b", key: "blocked", get label() { return t.heading.actions.blocked; }, icon: "octagon-x", run: (editor: Editor, id: string) => openPicker(editor, id, "blocked") },
];

/** Ctrl+. in a requirement heading: its pickers and the PM's actions. */
export function headingIntentions(editor: Editor): MarkdownIntention[] {
  const { $head } = editor.state.selection, heading = $head.depth === 1 ? $head.parent : null;
  if (!editor.isEditable || !heading || !isRequirementHeading(heading)) return [];
  const id = String(heading.attrs.headingId ?? "");
  if (!id) return [];
  const row = (key: string, label: string, icon: string, run: () => void, description?: string): MarkdownIntention =>
    ({ id: `requirement-heading.${key}`, label, icon: `svg:${icon}`, group: "requirement", keywords: ["pozadavek", "requirement"], ...(description ? { description } : {}), run });
  return [
    row("state", t.heading.menu.state, "circle-check", () => openStateMenu(editor, id)),
    row("kind", t.heading.menu.kind, directionIcons[heading.attrs.direction as RequirementDirection] ?? "user", () => openPicker(editor, id, "kind")),
    row("service", t.heading.menu.service, "plug", () => openPicker(editor, id, "service")),
    row("waiting", t.heading.menu.waiting, "hourglass", () => openStateMenu(editor, id, true)),
    row("date", t.heading.menu.date, "calendar", () => openPicker(editor, id, "deadline")),
    ...(pageOf(editor) ? [row("requires", t.heading.menu.requires, "link", () => openDependencyPicker(editor, id))] : []),
    row("comment", t.heading.menu.comment, "pencil", () => noteHeading(editor), formatMarkdownShortcut(["Mod", "Alt", "M"])),
    row("id", "ID…", "hash", () => openPicker(editor, id, "id"), id),
    ...actions.map(action => row(action.key, action.label, action.icon, () => action.run(editor, id), t.heading.onStateChip(action.letter.toUpperCase()))),
  ];
}

// ——— The marks: decoration widgets on the heading ———

/**
 * A reader's heading is one Tab stop, its first chip (it opens the read-only card); ←/→ reach the others and the chevron (headingFoldPlugin.ts
 * rove). Out of the writer's Tab order (Alt+F10, then Tab reaches them).
 */
const chipTabIndex = (editable: boolean, first: boolean) => editable || !first ? -1 : 0;

/**
 * A click on a heading's control before the text had the focus (the page just opened, the title field holds it): the caret goes to the
 * title's end first, so Ctrl+Z undoes what the control changed and typing goes on in the text (F3). A caret already in the text stays.
 */
function caretToTitle(view: EditorView, id: string) {
  if (view.hasFocus()) return;
  const section = sectionOf(view.state.doc, id);
  if (section) view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, section.headingPos + view.state.doc.nodeAt(section.headingPos)!.nodeSize - 1)));
  view.focus();
}

function checkbox(editor: Editor, id: string, title: string, done: boolean) {
  return (view: EditorView) => {
    const input = view.dom.ownerDocument.createElement("input");
    input.type = "checkbox"; input.className = "rq-check md-task-check"; input.checked = done; input.dataset.rqCheck = id; input.tabIndex = -1;
    input.setAttribute("aria-label", t.heading.doneAria(title || t.heading.requirementWord));
    // A reader's checkbox: in its place (J7.1), aria-disabled. No Tab stop for anyone (J6.1, Esc then Tab leaves the editor): Alt+F10, then Tab.
    if (!view.editable) input.setAttribute("aria-disabled", "true");
    input.addEventListener("mousedown", event => { if (view.editable) event.preventDefault(); });
    input.addEventListener("click", event => {
      event.preventDefault();
      if (!view.editable) return;
      // Space on the checkbox: the redrawn one keeps the focus (fourth review P3-1), unless the toggle opened its question.
      const focused = view.dom.ownerDocument.activeElement === input;
      if (!focused) caretToTitle(view, id);
      toggleDone(editor, id);
      if (focused) queueMicrotask(() => {
        const doc = view.dom.ownerDocument;
        if (!doc.activeElement || doc.activeElement === doc.body) view.dom.querySelector<HTMLElement>(`[data-rq-check="${CSS.escape(id)}"]`)?.focus({ preventScroll: true });
      });
    });
    // CSS plays it on the new box in place: popStatus's .8 → 1.06 scale, started a frame after the redraw, read as a jump (owner 2026-11-06).
    const toggled = justToggled.get(id);
    if (toggled) { input.dataset.rqToggled = toggled; justToggled.delete(id); }
    return input;
  };
}

/** A chip's label as 3.5.2 wrote it: the word muted, the value (a party, a count, a title) bold in the text's colour. */
const valued = (word: string, value: string) => [{ text: word }, { text: value, className: "rq-chip-value" }];

/**
 * The chips after the title, in the order the grip's keyboard path walks them (headingFoldPlugin.ts headingControls): the kind with its
 * party („↓ od DODAVATEL“, „pro …“, an internal task's people), the service, what it waits for („čeká na blokované: …“, „čeká na N
 * prerekvizity“), a blocked one's reason („Blokuje: …“, its newest „Blokováno:“ line, else its first note; text, no control), and the state last, a pill CSS floats to the
 * line's end (before the fold chevron). A reader's value chip (kind, service, state) opens the read-only card.
 */
function chips(editor: Editor, section: RequirementSection, open: number, blockedBy: string, reason: string) {
  const { facts, id } = section;
  return (view: EditorView) => {
    const doc = view.dom.ownerDocument, box = doc.createElement("span");
    box.className = "rq-chips"; box.contentEditable = "false"; box.dataset.rqId = id;
    const chip = (name: string, writes: boolean, options: Parameters<typeof createMarkdownChip>[1], run: () => void) => {
      const element = createMarkdownChip(doc, { tag: "button", ...options, className: `rq-chip ${options.className ?? ""}`.trim() }).element as HTMLButtonElement;
      element.type = "button"; element.dataset.rqChip = name; element.tabIndex = chipTabIndex(view.editable, name === "kind");
      // Like the contact chips: it opens a menu (the service: a field), and says so while that is open.
      element.setAttribute("aria-haspopup", name === "service" ? "dialog" : "menu");
      element.setAttribute("aria-expanded", String(openFrom(editor, id, name)));
      element.addEventListener("mousedown", event => event.preventDefault());
      element.addEventListener("click", event => {
        event.preventDefault();
        // The pick is then one undo step of the text, as the checkbox's (F3); a chip the keys reached keeps the focus.
        if (editor.isEditable && doc.activeElement !== element) caretToTitle(view, id);
        if (!writes || editor.isEditable) run(); else openFacts(editor, id, name);
      });
      if (name === "state") element.addEventListener("keydown", event => {
        const action = !event.ctrlKey && !event.metaKey && !event.altKey && view.editable ? actions.find(item => item.letter === event.key.toLowerCase()) : undefined;
        if (action) { event.preventDefault(); action.run(editor, id); }
      });
      box.append(element);
      return element;
    };
    const kind = kindWords[facts.direction], party = facts.party;
    const partyLabel = !party ? kind : facts.direction === "internal" ? valued("", party) : valued(facts.direction === "outbound" ? t.heading.partyTo : t.heading.partyFrom, party);
    chip("kind", true, { label: partyLabel, icon: party && facts.direction === "internal" ? "users" : directionIcons[facts.direction],
      ariaLabel: typeof partyLabel === "string" ? t.heading.kindAria(kind) : t.heading.kindAria(`${kind}, ${partyLabel.map(part => part.text).join("")}`), title: t.heading.kindTitle }, () => openPicker(editor, id, "kind"));
    if (facts.system) chip("service", true, { label: facts.system, icon: "plug", ariaLabel: t.heading.serviceAria(facts.system), title: t.heading.serviceTitle }, () => openPicker(editor, id, "service"));
    if (blockedBy) chip("blocked-by", false, { label: valued(t.heading.waitingForBlocked, blockedBy), icon: "hourglass", ariaLabel: t.heading.ariaWaitingForBlocked(blockedBy), title: t.heading.awaitedTitle },
      () => openAwaited(editor, id)).dataset.tone = "waiting_for_input";
    if (open) chip("ready", false, { label: valued(t.heading.waitingFor, t.counts.prerequisitesAcc(open)), icon: "hourglass",
      ariaLabel: t.heading.ariaWaitingForPrerequisites(open), title: t.heading.awaitedTitle }, () => openAwaited(editor, id)).dataset.tone = "waiting_for_input";
    if (reason) {
      const element = createMarkdownChip(doc, { label: valued(t.heading.blocking, reason), icon: "octagon-x", title: `${t.heading.blocking}${reason}`, className: "rq-chip" }).element;
      element.dataset.rqChip = "reason"; element.dataset.tone = "blocked";
      box.append(element);
    }
    const word = stateText(facts);
    chip("state", true, { label: word, icon: stateIcons[facts.state], ariaLabel: t.heading.stateAria(word), title: t.heading.stateChipTitle }, () => openStateMenu(editor, id))
      .dataset.tone = facts.state;
    return box;
  };
}

/**
 * The dependency line's section links as the pages plugin's section (MarkdownPageSection): the target's title now (this page: its
 * heading; another page: what the server read, else the stored title), and a jump to it on this page; another page's opens by its url.
 */
function sectionLinks(editor: Editor, doc: PMNode, sections: readonly RequirementSection[], remote: ReadonlyMap<string, RemoteRequirement>, pageId?: string) {
  const byId = new Map(sections.map(section => [section.id, section])), found: Decoration[] = [];
  for (const { depsPos } of sections) {
    if (depsPos === undefined) continue;
    doc.nodeAt(depsPos)!.forEach((child, offset) => {
      const target = child.type.name === "pageReference" ? sectionTarget(child.attrs.url) : null;
      if (!target) return;
      const here = onThisPage(target, byId, pageId), other = here ? undefined : remote.get(`${target.headingId}@${target.pageId}`);
      const own = here ? byId.get(target.headingId) : undefined, stored = String(child.attrs.title ?? "");
      // A link stores its target's title, or only its id when it was made without one: an id is no name to show for a requirement gone.
      const title = (own ? own.facts.title : other?.remote.readable ? other.title : "") || (here && stored === target.headingId ? t.heading.missing : stored);
      const section: MarkdownPageSection = own ? { title, open: () => goToHeading(editor, target.headingId) }
        : here ? { title, missing: t.heading.gone(target.headingId) }
        : { title, page: other?.remote.pageTitle || storageOf(editor)?.host?.titleOf?.(target.pageId) || t.heading.anotherPage };
      found.push(Decoration.node(depsPos + 1 + offset, depsPos + 1 + offset + child.nodeSize, {}, { [pageSectionSpec]: section }));
    });
  }
  return found;
}

/** A blocked requirement's reason, the Přehled's rule (requirementOverview.ts blockedReason): its newest „Blokováno: …“ line, else its first note; "" otherwise. */
function reasonOf(doc: PMNode, section: RequirementSection) {
  if (section.facts.state !== "blocked") return "";
  let note = "";
  const records: string[] = [];
  doc.nodesBetween(section.headingPos + doc.nodeAt(section.headingPos)!.nodeSize, section.end, node => {
    const text = !note && node.type.name === "paragraph" && !isDependencyLine(node) ? node.textContent.trim() : "";
    if (isReasonNote(text)) note = text;
    if (historyList(node.type.name)) node.forEach(item => { if (item.firstChild) records.push(item.firstChild.textContent.trim()); });
    return false;
  });
  return blockedReason(note, records);
}
const reasonsOf = (doc: PMNode, sections: readonly RequirementSection[]) => sections.map(section => reasonOf(doc, section)).join("\n");

const marksKey = new PluginKey<DecorationSet>("requirementHeadingMarks");
const widget = { ignoreSelection: true, stopEvent: () => true };
const titlePlaceholder = (view: EditorView) => Object.assign(view.dom.ownerDocument.createElement("span"), { className: "rq-title-placeholder", textContent: t.heading.titlePlaceholder });
function marks(editor: Editor, state: EditorState) {
  const pageId = pageOf(editor), remote = remoteOf(editor), sections = requirementSections(state.doc);
  const open = awaited(state.doc, pageId, remote), found: Decoration[] = sectionLinks(editor, state.doc, sections, remote, pageId);
  for (const section of sections) {
    const { facts, headingPos } = section, end = headingPos + state.doc.nodeAt(headingPos)!.nodeSize - 1, awaits = open.get(section.id);
    const waits = awaits?.count ?? 0, blockedBy = awaits?.list.find(entry => entry.state === "blocked")?.title ?? "", reason = reasonOf(state.doc, section);
    found.push(Decoration.widget(headingPos + 1, checkbox(editor, section.id, facts.title, facts.state === "done"), { ...widget, side: -1, key: `rq-check:${section.id}:${facts.state === "done"}:${facts.title}` }));
    // An empty title's placeholder after the checkbox, the caret before it (side > 0), the chips after it (side 1): the heading's own
    // placeholder (core kit) would stand at the line's start, under the checkbox (requirementHeading.css hides it).
    if (end === headingPos + 1) found.push(Decoration.widget(end, titlePlaceholder, { ...widget, side: 0.5, key: "rq-title-placeholder" }));
    if (section.factsPos !== undefined) found.push(Decoration.node(section.factsPos, section.factsPos + state.doc.nodeAt(section.factsPos)!.nodeSize, { "data-rq-facts": "" }));
    found.push(Decoration.widget(end, chips(editor, section, waits, blockedBy, reason), { ...widget, side: 1,
      key: `rq-chips:${section.id}:${facts.state}:${facts.stateLabel}:${facts.direction}:${facts.system}:${facts.party}:${waits}:${blockedBy}:${reason}` }));
  }
  return DecorationSet.create(state.doc, found);
}

/**
 * The marks, redrawn when the document changes or other pages' prerequisites arrive (requirementLinks.ts, a step without changes); a
 * change of the right marks the drawn ones, the boxes aria-disabled for a reader (J7.1).
 */
const Marks = Extension.create({
  name: "requirementHeadingMarks",
  addProseMirrorPlugins() {
    const editor = this.editor;
    return [new Plugin<DecorationSet>({
      key: marksKey,
      // Typing elsewhere (a note, another paragraph) leaves every requirement's facts the same objects (the per-node cache): the marks
      // only move along; a heading, a dependency line, a facts line or a blocked one's reason that changed draws them again.
      state: { init: (_, state) => marks(editor, state), apply: (tr, set, old, state) => {
        if (tr.getMeta(marksKey)) return marks(editor, state);
        if (!tr.docChanged) return set;
        const before = requirementSections(old.doc), after = requirementSections(state.doc);
        return before.length === after.length && before.every((section, index) => section.facts === after[index]!.facts) && reasonsOf(old.doc, before) === reasonsOf(state.doc, after)
          ? set.map(tr.mapping, tr.doc) : marks(editor, state);
      } },
      props: { decorations: state => marksKey.getState(state) },
      view: view => {
        let editable = view.editable;
        const stop = watch(storageOf(editor).links, () => { if (!editor.isDestroyed) view.dispatch(view.state.tr.setMeta(marksKey, true)); });
        // The index once the editor stands, so a page of headings only asks for its other pages' prerequisites without waiting for a step.
        queueMicrotask(() => { if (!editor.isDestroyed) { runtimeOf(editor); storageOf(editor).linkLoader?.sync(); } });
        // A chip's popover marks the editor (its anchor) expanded and collapsed; the chip that opened it follows.
        const observer = new (view.dom.ownerDocument.defaultView?.MutationObserver ?? MutationObserver)(() => { if (!editor.isDestroyed) syncChips(editor); });
        observer.observe(view.dom, { attributes: true, attributeFilter: ["aria-expanded"] });
        return { update(view) {
          if (view.editable === editable) return;
          editable = view.editable;
          // The marks stay: only what the right changes on them is set (as the task checkboxes, core kit), not a redraw of every
          // requirement's widgets (a lock toggle on 150 requirements: 2.8 s at 4× CPU). Widgets made later read `view.editable`.
          view.dom.querySelectorAll<HTMLElement>("[data-rq-check]").forEach(input => editable ? input.removeAttribute("aria-disabled") : input.setAttribute("aria-disabled", "true"));
          view.dom.querySelectorAll<HTMLElement>(".rq-chips > button[data-rq-chip]").forEach(chip => { const tabIndex = chipTabIndex(editable, chip.dataset.rqChip === "kind"); if (chip.tabIndex !== tabIndex) chip.tabIndex = tabIndex; });
        }, destroy() { stop(); observer.disconnect(); } };
      },
    })];
  },
});

/** The states whose section is tinted (requirementHeading.css): what is going on or stuck; neřešeno, hotovo and netýká se stay calm. */
const tinted = new Set<RequirementState>(["blocked", "waiting_for_input", "in_progress"]);
const tintKey = new PluginKey<DecorationSet>("requirementHeadingTint");

const tintWidget = (view: EditorView) => {
  const card = view.dom.ownerDocument.createElement("span");
  card.className = "rq-tint";
  card.setAttribute("aria-hidden", "true");
  return card;
};

/**
 * The section card (model §1: a requirement's section is its heading and the blocks up to the next heading of any level, as the Přehled
 * and the filter read it): one card element in each tinted requirement heading (`rq-tint`, drawn by requirementHeading.css from the
 * heading's `data-state`, sized by `sizeTints`). The blocked reason is a chip of the heading line (`chips`).
 */
function tints(doc: PMNode) {
  const found: Decoration[] = [];
  doc.forEach((node, pos) => {
    if (isRequirementHeading(node) && tinted.has(node.attrs.state)) found.push(Decoration.widget(pos + 1, tintWidget, { side: -1, key: "rq-tint", ignoreSelection: true }));
  });
  const set = DecorationSet.create(doc, found);
  rebuilt.add(set);
  return set;
}
const rebuilt = new WeakSet<DecorationSet>();

/**
 * How tall each card is: from its heading to the bottom of the section's last block that shows (a folded section: the heading alone),
 * as `--rq-section`. Read first, then written; run when the cards change and when the page's size changes (typing that wraps a line,
 * a fold, a narrower window), never per keystroke.
 */
export function sizeTints(root: HTMLElement) {
  const sizes = [...root.querySelectorAll<HTMLElement>(":scope > * > .rq-tint")].map(card => {
    const heading = card.parentElement!;
    let last: HTMLElement = heading;
    for (let block = heading.nextElementSibling; block instanceof HTMLElement && !/^H[1-6]$/.test(block.tagName); block = block.nextElementSibling)
      if (block.offsetParent) last = block;
    return [card, last.offsetTop + last.offsetHeight - heading.offsetTop] as const;
  });
  // Only a size that changed: every write after the editor's own update makes its DOM observer flush and force a layout.
  sizes.forEach(([card, height]) => { if (card.style.getPropertyValue("--rq-section") !== `${height}px`) card.style.setProperty("--rq-section", `${height}px`); });
}

/** The cards: rebuilt when the outline can have changed (requirementOverview's rule), else moved along. */
const Tint = Extension.create({
  name: "requirementHeadingTint",
  addProseMirrorPlugins() {
    return [new Plugin<DecorationSet>({
      key: tintKey,
      state: {
        init: (_, state) => tints(state.doc),
        apply(tr, set) {
          if (!tr.docChanged) return set;
          const mapped = set.map(tr.mapping, tr.doc);
          if (!touchesOutline(tr)) return mapped;
          // A title typed into moves no card: the mapped set, so neither the view nor sizeTints go over every section per key.
          const next = tints(tr.doc), starts = (cards: DecorationSet) => cards.find().map(card => card.from).sort((a, b) => a - b).join();
          return starts(next) === starts(mapped) ? mapped : next;
        },
      },
      props: { decorations: state => tintKey.getState(state) },
      view: view => {
        const win = view.dom.ownerDocument.defaultView;
        let frame = 0, drawn: DecorationSet | undefined;
        const size = () => { frame = 0; if (!view.isDestroyed) sizeTints(view.dom); };
        const schedule = () => { if (!frame && win) frame = win.requestAnimationFrame(size); };
        const observer = win?.ResizeObserver ? new win.ResizeObserver(schedule) : undefined;
        observer?.observe(view.dom);
        schedule();
        return {
          // A rebuilt set (a section added, removed, re-stated) may have new cards or moved ends: measured once, a frame later.
          update: next => { const set = tintKey.getState(next.state); if (set !== drawn && rebuilt.has(set!)) schedule(); drawn = set; },
          destroy: () => { observer?.disconnect(); if (frame) win?.cancelAnimationFrame(frame); },
        };
      },
    })];
  },
});

// ——— Text rules ———

/** The attributes that make a heading a requirement (headingId and aliases are the heading's own). */
const requirementOnly = { state: null, stateLabel: "", direction: "internal", system: "", party: "", partyContactId: "", from: "" } as const;
/** What a dependency line may hold: its links and the „, “ between them. */
const isLinkOrSeparator = (node: PMNode) => node.type.name === "pageReference" || (node.isText && /^[\s,]*$/.test(node.text!));
/** An undo or redo of the editor's history: it restores a state these rules already made, so they never act on it. */
const fromHistory = (tr: Transaction) => tr.getMeta("history$") !== undefined;

/**
 * A delete (or a cut) from a requirement title's start past its end: what stays of the merged line is the line the delete ended in, its
 * type and attributes (selecting a whole section up to the next title deletes that section, not the next requirement's identity); a
 * requirement left without a title is a paragraph (also when the delete ended in a list, a table or a quote). `join` is where the
 * deleted range closed, in `tr.doc`. Everything else a delete does is the editor's own; heal() puts the lines back in place.
 */
function fromTitleStart(tr: Transaction, before: PMNode, step: ReplaceStep | ReplaceAroundStep, join: number) {
  const { from, to } = step as unknown as { from: number; to: number }, $from = before.resolve(from), $to = before.resolve(to);
  if ($from.depth !== 1 || to <= $from.end()) return;
  const $join = tr.doc.resolve(join), merged = $join.depth ? $join.node(1) : null;
  if (!merged) return;
  // From another line's middle into a dependency line's: the line's rest stays a line of links, its requirement's own line when the
  // delete began in that requirement's title, else a note (joined into text, its links were no one's dependencies any more though the
  // delete took none of them: fuzz seeds 20292780, 20554107). Typed text stays where it was typed, also from a title's start (seed 20269023).
  const typed = step instanceof ReplaceStep ? step.slice.size : 0;
  if (($from.parentOffset || typed) && $to.depth === 1 && isDependencyLine($to.parent) && $to.parentOffset < $to.parent.content.size) {
    const own = isRequirementHeading($from.parent) && $to.index(0) === $from.index(0) + 1;
    tr.split(join + typed, 1, [{ type: tr.doc.type.schema.nodes.paragraph!, attrs: own ? $to.parent.attrs : null }]);
    return;
  }
  if ($from.parentOffset || !isRequirementHeading($from.parent) || !isRequirementHeading(merged)) return;
  const end = $to.depth === 1 && $to.parent.isTextblock && $to.parent.type.validContent(merged.content) && !(isRequirementHeading($to.parent) && !merged.content.size) ? $to.parent : null;
  if (end || !merged.content.size) tr.setNodeMarkup($join.before(1), end?.type ?? tr.doc.type.schema.nodes.paragraph, end?.attrs ?? null, merged.marks);
}

/**
 * The fixed places put back after the editor's own commands (Enter, a paste, a block moved or copied, a split): a requirement that still
 * exists and had a dependency line or a facts line before this step but has none at its place now gets it back: its former dependency
 * line moves to child 1, its former facts line (or, when a split left it empty, the first facts paragraph of its first three lines) to
 * the next place. A line whose requirement is gone stays where it is (it is text under whatever heading it now follows).
 */
function heal(tr: Transaction, old: PMNode, mapping: Mapping) {
  const place = (doc: PMNode, id: string) => requirementSections(doc).find(section => section.id === id);
  const forward = (pos: number) => tr.mapping.map(mapping.map(pos, 1), 1);
  for (const before of requirementSections(old)) for (const kind of ["deps", "facts"] as const) {
    const was = kind === "deps" ? before.depsPos : before.factsPos, now = place(tr.doc, before.id);
    if (was === undefined || !now || (kind === "deps" ? now.depsPos : now.factsPos) !== undefined) continue;
    const heading = tr.doc.nodeAt(now.headingPos)!, under = now.headingPos + heading.nodeSize;
    const fits = (pos: number) => { const node = pos < tr.doc.content.size ? tr.doc.nodeAt(pos) : null; return kind === "deps" ? isDependencyLine(node) : !!node && isFactsLine(node) && tr.doc.resolve(pos).depth === 0; };
    let from = forward(was);
    if (!fits(from) && kind === "facts") {
      // Only a piece of the former facts line (a split moved it down); a note that merely reads like facts stays a note (sixth review P2-1).
      const back = new Mapping([...mapping.maps, ...tr.mapping.maps]).invert(), end = was + old.nodeAt(was)!.nodeSize, ours = (pos: number) => { const origin = back.map(pos, 1); return origin > was && origin < end; };
      from = -1;
      let at = under;
      for (let index = 0; index < 3 && at < now.end && from < 0; index++, at += tr.doc.nodeAt(at)!.nodeSize) if (at !== now.depsPos && fits(at) && ours(at)) from = at;
    }
    if (from < 0 || !fits(from)) continue;
    const node = tr.doc.nodeAt(from)!, target = kind === "deps" || now.depsPos === undefined ? under : now.depsPos + tr.doc.nodeAt(now.depsPos)!.nodeSize;
    if (from === target) continue;
    // A move keeps the caret where it was in the line.
    const head = tr.selection.head, inside = head > from && head < from + node.nodeSize ? head - from : -1;
    tr.delete(from, from + node.nodeSize);
    const at = tr.mapping.slice(tr.steps.length - 1).map(target, from < target ? -1 : 1);
    tr.insert(at, node);
    if (inside >= 0) tr.setSelection(TextSelection.create(tr.doc, at + inside));
  }
}

/**
 * The shape's rules, applied in the user's own step (an appended transaction joins its undo step), never for a remote change, an undo or a
 * redo (shape §1, model §1, §3):
 * - a delete across lines never makes one requirement's lines another's (keepLinesApart);
 * - `dependencies` lives only on the paragraph directly under a requirement heading; anywhere else it is cleared (the links stay, as
 *   mentions). A dependency line this step emptied goes in this step (the caret in it to the title's end: an empty paragraph in its
 *   place would hide the facts line); one this step left with text but no links is a paragraph again;
 * - requirement attributes live only on top-level headings: one pasted into a list or a quote is a plain heading.
 * - a heading copied as it is (Alt+Shift+↓) gets a fresh id.
 */
const Rules = Extension.create({
  name: "requirementDependencyLine",
  addProseMirrorPlugins() { const editor = this.editor; return [new Plugin({
    appendTransaction(transactions, old, state) {
      if (!transactions.some(tr => tr.docChanged) || transactions.some(tr => isRemote(tr) || fromHistory(tr))) return null;
      const tr = state.tr, mapping = new Mapping(transactions.flatMap(step => step.mapping.maps)), touched: [number, number][] = [];
      mapping.maps.forEach((map, index) => map.forEach((_from, _to, start, end) => { const after = mapping.slice(index + 1); touched.push([after.map(start, -1), after.map(end, 1)]); }));
      // Positions of the user's steps' document carried into tr.doc as this rule adds its own steps.
      const now = (pos: number, assoc = 1) => tr.mapping.map(pos, assoc), changed = (from: number, to: number) => touched.some(([start, stop]) => now(start, -1) <= to && now(stop) >= from);
      let global = 0;
      for (const transaction of transactions) transaction.steps.forEach((step, index) => {
        const at = global++;
        // A delete, typing over a range, or a delete that closes an emptied block (into a table: one empty textblock, open); never a paste of blocks.
        const { content, openStart, openEnd } = step instanceof ReplaceStep ? step.slice : { content: null, openStart: 0, openEnd: 0 };
        const inline = !!content && (!content.size || (openStart || openEnd ? content.childCount === 1 && content.firstChild!.isTextblock && !content.firstChild!.content.size : !!content.firstChild?.isInline));
        if ((inline && step instanceof ReplaceStep) || step instanceof ReplaceAroundStep) fromTitleStart(tr, transaction.docs[index]!, step, now(mapping.slice(at + 1).map((step as unknown as { from: number }).from, -1), -1));
      });
      // A dependency line this step left without links or text goes first (the caret in it to the title's end: an empty paragraph in its
      // place would hide the facts line), so heal() sees the line that holds the links.
      let caret: { pos: number; step: number } | undefined;
      const emptied: number[] = [], head = now(state.selection.head);
      tr.doc.forEach((node, pos) => {
        let links = false;
        node.forEach(child => { links ||= child.type.name === "pageReference"; });
        if (isDependencyLine(node) && !links && !node.textContent.replace(/[\s,]/g, "") && changed(pos, pos + node.nodeSize)) emptied.push(pos);
      });
      for (const pos of emptied.reverse()) {
        const size = tr.doc.nodeAt(pos)!.nodeSize;
        if (head > pos && head < pos + size) caret = { pos: pos - 1, step: tr.steps.length + 1 };
        tr.delete(pos, pos + size);
      }
      heal(tr, old.doc, mapping);
      const doc = tr.doc, headings = new Map<string, number[]>(), edits = new Map<number, [PMNode, "clear" | "plain" | "split" | "extract"]>();
      // A link to the requirement's own heading (a join of two titles brought it): the index reads it as a cycle (sixth review P2-2).
      const page = pageOf(editor)?.toLowerCase(), self = (child: PMNode, own: unknown) => child.type.name === "pageReference" && !!page
        && String(child.attrs.id ?? "").toLowerCase() === page && sectionTarget(child.attrs.url)?.headingId === own;
      const back = new Mapping([...mapping.maps, ...tr.mapping.maps]).invert(), pasted = transactions.some(item => ["paste", "drop"].includes(item.getMeta("uiEvent")));
      doc.forEach((node, pos) => { if (node.type.name === "heading" && node.attrs.headingId) headings.set(node.attrs.headingId, [...headings.get(node.attrs.headingId) ?? [], pos]); });
      let previous: PMNode | null = null;
      doc.forEach((node, pos) => {
        // A requirement heading a paste leaves without a title (one brought opened inside it, or one split before its title, which the
        // slice's end took) is a plain heading: no requirement is without a title (fuzz seeds 20356132, 21741957). The slash's new
        // requirement is empty on purpose, but no paste.
        if (pasted && isRequirementHeading(node) && !node.content.size && changed(pos, pos + node.nodeSize)) edits.set(pos, [node, "plain"]);
        // The second half of a split title (Enter, a paste of blocks into it) came from inside a requirement's title while the first half
        // keeps its id: a plain heading (its own id from the heading anchors); state, kind, service and provenance stay with the first half.
        if (isRequirementHeading(node)) {
          const $was = old.doc.resolve(Math.min(back.map(pos, 1), old.doc.content.size)), id = String($was.parent.attrs.headingId ?? "");
          if ($was.depth === 1 && $was.parentOffset > 0 && isRequirementHeading($was.parent) && headings.get(id)?.some(other => other !== pos)) edits.set(pos, [node, "split"]);
        }
        if (isDependencyLine(node)) {
          let links = false, rest = false, own = false;
          node.forEach(child => { links ||= child.type.name === "pageReference"; rest ||= !isLinkOrSeparator(child); own ||= self(child, previous?.attrs.headingId); });
          if (!isRequirementHeading(previous) || (!links && rest && changed(pos, pos + node.nodeSize))) edits.set(pos, [node, "clear"]);
          // Text that got into the line (typed, a paste's open end) goes to a note after the lines, a link to itself goes: links only.
          // A link to itself also when the step changed the heading above (two titles joined), not only the line.
          else if ((rest && changed(pos, pos + node.nodeSize)) || (own && changed(pos - previous!.nodeSize, pos + node.nodeSize))) edits.set(pos, [node, "extract"]);
        }
        previous = ["plain", "split"].includes(edits.get(pos)?.[1] ?? "") ? null : node;
      });
      for (const [start, stop] of touched) doc.nodesBetween(Math.max(0, now(start, -1)), Math.min(doc.content.size, now(stop)), (node, pos, parent) => {
        if (parent !== doc && isRequirementHeading(node)) edits.set(pos, [node, "plain"]);
        else if (parent !== doc && isDependencyLine(node)) edits.set(pos, [node, "clear"]);
        return !node.isTextblock;
      });
      // A block copied as it is (Alt+Shift+↓, KeyboardFirst) keeps its heading's id, which the heading anchors do not catch: the copy, the
      // one these steps inserted, gets a fresh id and no aliases. shortcut: top-level headings only, where requirements are.
      for (const positions of headings.values()) {
        const copies = positions.length > 1 ? positions.filter(pos => back.mapResult(pos).deleted) : [];
        for (const pos of copies.length === positions.length ? copies.slice(1) : copies) tr.setNodeMarkup(pos, undefined, { ...doc.nodeAt(pos)!.attrs, headingId: globalThis.crypto.randomUUID(), aliases: "" });
      }
      const typingAt = now(state.selection.head);
      for (const [pos, [node, edit]] of [...edits].sort(([a], [b]) => b - a)) {
        if (edit === "clear") {
          tr.setNodeAttribute(pos, "dependencies", false);
          // A line of links split in the middle: the note starts with its link, not with the „, “ between them (sixth review P3-2).
          const first = node.firstChild;
          if (first?.isText && /^[\s,]+$/.test(first.text!) && node.childCount > 1) tr.delete(pos + 1, pos + 1 + first.nodeSize);
        }
        else if (edit === "extract") {
          const links: PMNode[] = [], rest: PMNode[] = [], schema = tr.doc.type.schema, own = doc.childBefore(pos).node?.attrs.headingId;
          // Text typed next to a „, “ joins it in one text node: the note takes the text without the separator.
          node.forEach(child => {
            const text = child.isText ? child.text!.replace(/^[\s,]+|[\s,]+$/g, "") : "";
            if (child.type.name === "pageReference") { if (!self(child, own)) links.push(child); } else if (!isLinkOrSeparator(child)) rest.push(child.isText ? schema.text(text, child.marks) : child);
          });
          // After the facts line when one follows, so the facts keep their place.
          const after = pos + node.nodeSize, next = tr.doc.nodeAt(after), at = next && isFactsLine(next) ? after + next.nodeSize : after;
          const note = schema.nodes.paragraph!.create(null, rest), typing = typingAt > pos && typingAt < after;
          if (rest.length) tr.insert(at, note);
          if (links.length) tr.replaceWith(pos + 1, after - 1, links.flatMap((link, index) => index ? [schema.text(", "), link] : [link]));
          else tr.delete(pos, after);
          // Typing in the line goes on in the note.
          if (typing && rest.length) caret = { pos: tr.mapping.slice(tr.steps.length - 1).map(at) + note.nodeSize - 1, step: tr.steps.length };
        }
        else tr.setNodeMarkup(pos, undefined, { ...node.attrs, ...requirementOnly, ...(edit === "split" ? { aliases: "" } : {}) });
      }
      if (caret) tr.setSelection(TextSelection.create(tr.doc, tr.mapping.slice(caret.step).map(caret.pos)));
      return tr.docChanged ? tr : null;
    },
    props: {
      // A paste the editor's own cannot place without moving a requirement's data (fourth and fifth review): a slice with a heading pasted
      // into a requirement's title or lines goes after its notes (else the heading took them); into the dependency line anything but
      // links, and blocks into the facts line, go after the lines (the paste's end took the links, a list absorbed the facts). Text alone
      // and links stay where they are pasted; blocks into a title are the editor's own, heal() puts the lines back.
      handlePaste(view, _event, slice) {
        const { doc, selection: { $from, $to } } = view.state, line = $from.depth === 1 && $to.parent === $from.parent ? $from.before(1) : -1;
        const section = line < 0 ? undefined : sectionAt(doc, line);
        if (!section || ![section.headingPos, section.depsPos, section.factsPos].includes(line)) return false;
        let links = true, heading = false;
        slice.content.descendants(node => { links &&= node.isInline ? isLinkOrSeparator(node) : node.type.name === "paragraph"; return links; });
        slice.content.forEach(node => { heading ||= node.type.name === "heading"; });
        const inline = slice.content.childCount === 1 && !!slice.content.firstChild?.isTextblock;
        if (!heading && (line === section.headingPos || (line === section.depsPos ? links : inline))) {
          if (line !== section.factsPos || slice.openStart > 0) return false;
          // A whole block copied (openStart 0) of text alone: its text into the line, never a split of it.
          view.dispatch(view.state.tr.replaceSelection(new Slice(slice.content.firstChild!.content, 0, 0)).setMeta("uiEvent", "paste").scrollIntoView());
          return true;
        }
        const tr = view.state.tr.deleteSelection().setMeta("uiEvent", "paste"), now = sectionAt(tr.doc, line)!;
        const at = heading ? now.end : linesEnd(tr.doc, now), base = tr.steps.length;
        tr.replaceRange(at, at, new Slice(slice.content, 0, 0));
        view.dispatch(tr.setSelection(Selection.near(tr.doc.resolve(tr.mapping.slice(base).map(at, 1)), -1)).scrollIntoView());
        return true;
      },
    },
  })]; },
});

// ——— Copies: fresh ids, and the links between the copied headings follow them (model §4) ———

/** Maps a slice's nodes, descending into every non-text node. */
function mapSlice(slice: Slice, map: (node: PMNode) => PMNode): Slice {
  const walk = (fragment: Fragment): Fragment => {
    const nodes: PMNode[] = [];
    fragment.forEach(node => nodes.push(map(node.isText || node.isLeaf ? node : node.copy(walk(node.content)))));
    return Fragment.from(nodes);
  };
  return new Slice(walk(slice.content), slice.openStart, slice.openEnd);
}
const headingsOf = (slice: Slice) => { const found: PMNode[] = []; slice.content.descendants(node => { if (node.type.name === "heading") found.push(node); }); return found; };

/** The pasted headings' ids and aliases, in order, read before the heading anchors take the ids away (their paste rule). */
const pastedIds = new WeakMap<EditorView, string[][]>();
const PastedIds = Extension.create({
  // Before the heading anchors' UniqueID (priority 10 000); CopiedIds runs after it.
  name: "requirementPastedIds", priority: 10_001,
  addProseMirrorPlugins() {
    const editor = this.editor;
    return [new Plugin<ReadonlySet<string>>({
    // The heading ids the last cut took (ProseMirror's cut marks its step uiEvent „cut“): pasting them back is a move.
    key: cutKey,
    state: { init: () => new Set(), apply: (tr, cut, old) => tr.getMeta("uiEvent") === "cut" ? new Set(headingsOf(old.selection.content()).map(node => String(node.attrs.headingId ?? "")).filter(Boolean)) : cut },
    props: {
      transformPasted(slice, view) {
        pastedIds.set(view, headingsOf(slice).map(node => [String(node.attrs.headingId ?? ""), ...String(node.attrs.aliases ?? "").split(",")].filter(Boolean)));
        return slice;
      },
      // The page a copy comes from, so its links to its own headings follow the copy and links to other pages stay (third review P3-2).
      transformCopied(slice) {
        lastCopy = { pageId: pageOf(editor), ids: new Set(headingsOf(slice).map(node => String(node.attrs.headingId ?? "")).filter(Boolean)) };
        return slice;
      },
    },
  })];
  },
});
let lastCopy: { pageId: string | undefined; ids: ReadonlySet<string> } | undefined;
const cutKey = new PluginKey<ReadonlySet<string>>("requirementCutIds");

/**
 * After the heading anchors: a heading whose id they took away (a paste, a copy dropped) is a copy and gets its fresh id here, its
 * aliases cleared (they are the original's), and the dependency lines and `?heading=` links of the slice that named it (by its id or
 * an alias) follow, retargeted to this page: links to this page or to the page the copy came from (the last copy in this tab, else the
 * one other page the slice's links to its headings name); a link to another page's heading of the same id stays. A move (a drag within
 * the page, a cut pasted back) keeps its ids.
 */
const CopiedIds = Extension.create({
  name: "requirementCopiedIds", priority: 1,
  addProseMirrorPlugins() {
    const editor = this.editor;
    return [new Plugin({ props: { transformPasted(pasted, view) {
      // A section pasted (a requirement heading and more) goes in as blocks, as in Notion: open at its start, the title joined the paragraph
      // at the caret. It ends in a top-level line the text after the caret joins: ending in its history list, the caret stayed in the list
      // and the next paste nested a copy of the section in its last item.
      const section = (pasted.openStart || pasted.openEnd) && pasted.content.childCount > 1 && isRequirementHeading(pasted.content.firstChild!);
      const slice = !section ? pasted : new Slice(pasted.content.lastChild!.isTextblock ? pasted.content : pasted.content.addToEnd(view.state.schema.nodes.paragraph!.create()), 0, 1);
      const before = pastedIds.get(view) ?? [], renamed = new Map<string, string>(), present = new Set<string>(), cut = cutKey.getState(view.state);
      pastedIds.delete(view);
      view.state.doc.forEach(node => { if (node.type.name === "heading" && node.attrs.headingId) present.add(String(node.attrs.headingId)); });
      let index = 0;
      const fresh = mapSlice(slice, node => {
        if (node.type.name !== "heading") return node;
        const was = before[index++];
        if (node.attrs.headingId != null || !was?.length) return node;
        // A heading the last cut took, whose id the page no longer has: pasting it is a move, it keeps its id and aliases (links stay valid).
        if (cut?.has(was[0]!) && !present.has(was[0]!)) return node.type.create({ ...node.attrs, headingId: was[0], aliases: was.slice(1).join(",") }, node.content, node.marks);
        const next = globalThis.crypto.randomUUID();
        was.forEach(id => renamed.set(id, next));
        return node.type.create({ ...node.attrs, headingId: next, aliases: "" }, node.content, node.marks);
      });
      if (!renamed.size) return fresh;
      const page = pageOf(editor), lower = (id: string | undefined) => id?.toLowerCase();
      const others = new Set<string>();
      slice.content.descendants(node => { const target = node.type.name === "pageReference" ? sectionTarget(node.attrs.url) : null; if (target && renamed.has(target.headingId) && lower(target.pageId) !== lower(page)) others.add(lower(target.pageId)!); });
      const copied = before.flat().filter(id => renamed.has(id)), source = lastCopy && copied.every(id => lastCopy!.ids.has(id)) ? lower(lastCopy.pageId) : others.size === 1 ? [...others][0] : undefined;
      // A link to this page, the copy's page or no page at all (a bare ?heading= link) is a link inside the copy.
      const inside = (pageId: string | undefined) => !pageId || lower(pageId) === lower(page) || lower(pageId) === source;
      const retarget = (href: string, old: string) =>
        (page ? href.replace(/pages\/[^/?#]+/, `pages/${encodeURIComponent(page)}`) : href).replace(`heading=${old}`, `heading=${renamed.get(old)}`);
      return mapSlice(fresh, node => {
        const target = node.type.name === "pageReference" ? sectionTarget(node.attrs.url) : null;
        if (target && renamed.has(target.headingId) && inside(target.pageId)) return node.type.create({ ...node.attrs, id: page ?? target.pageId, url: retarget(String(node.attrs.url), target.headingId) }, null, node.marks);
        const link = linkMark(node), href = String(link?.attrs.href ?? ""), linked = link && (sectionTarget(href) ? inside(sectionTarget(href)!.pageId) ? sectionTarget(href)!.headingId : ""
          : /^[?#]heading=/.test(href) ? href.slice(9) : "");
        return link && linked && renamed.has(linked) ? node.mark(node.marks.map(mark => mark === link ? link.type.create({ ...link.attrs, href: retarget(href, linked) }) : mark)) : node;
      });
    } } })];
  },
});

/** The level a new requirement takes: a sibling of the requirement above, else one under the nearest heading above (at most 6). */
export function requirementLevelAt(doc: PMNode, pos: number) {
  let above: PMNode | null = null;
  doc.forEach((node, offset) => { if (offset < pos && node.type.name === "heading") above = node; });
  const heading = above as PMNode | null;
  return !heading ? 2 : isRequirementHeading(heading) ? Number(heading.attrs.level) : Math.min(6, Number(heading.attrs.level) + 1);
}

/** „Požadavek“ (slash, palette): a requirement heading (neřešeno) at the caret, the caret in it; written into the command's `tr`. */
export function insertRequirementHeading(tr: Transaction) {
  const { $from } = tr.selection, heading = tr.doc.type.schema.nodes.heading;
  if (!heading) return false;
  const top = $from.depth ? $from.before(1) : $from.pos, block = $from.depth ? $from.node(1) : null;
  const node = heading.create({ level: requirementLevelAt(tr.doc, top), state: "planned" });
  const empty = block?.type.name === "paragraph" && !block.content.size && !isDependencyLine(block);
  const at = empty ? top : $from.depth ? $from.after(1) : $from.pos;
  if (empty) tr.replaceWith(top, top + block.nodeSize, node); else tr.insert(at, node);
  tr.setSelection(TextSelection.create(tr.doc, at + 1)).scrollIntoView();
  return true;
}

/** The keys a heading's default cannot do for a requirement: an empty title's Enter, and joins that would merge its lines into its title. */
const Keys = Extension.create({
  name: "requirementHeadingKeys",
  priority: 200,
  addKeyboardShortcuts: () => ({
    // Enter in an empty title makes it a paragraph (as the old block did): Enter never leaves a requirement without a title. Every other
    // Enter is the editor's own; heal() puts the lines back, the split rule makes a second half plain.
    Enter: ({ editor }) => {
      const { selection } = editor.state;
      return selection.empty && selection.$head.depth === 1 && isRequirementHeading(selection.$head.parent) && !selection.$head.parent.content.size && editor.isEditable
        && editor.commands.setNode("paragraph");
    },
    // A join that would move a requirement's lines: the caret goes to the other block instead (blockedJoin). Backspace at a heading's
    // start is the editor's own (J4.3, core kit: the heading becomes text first): a requirement heading does the same, its state goes and
    // its lines are notes (the rules clear the dependency line), never the requirement above's.
    Backspace: ({ editor }) => {
      const { selection } = editor.state, { $head } = selection;
      if (!selection.empty || $head.depth !== 1 || $head.parentOffset !== 0 || !editor.isEditable || $head.parent.type.name === "heading") return false;
      const index = $head.index(0);
      if (!index || !blockedJoin(editor.state.doc, index)) return false;
      editor.commands.setTextSelection($head.before(1) - 1);
      return true;
    },
    Delete: ({ editor }) => {
      const { selection } = editor.state, { $head } = selection;
      if (!selection.empty || !$head.depth || $head.parentOffset !== $head.parent.content.size || !editor.isEditable) return false;
      const index = $head.index(0) + 1;
      if (index >= editor.state.doc.childCount) return false;
      // At the very end of a list (a quote, a table) a requirement heading after it does not join into the last item (fifth review P3).
      const nested = $head.depth > 1 && $head.after(1) === $head.pos + $head.depth && isRequirementHeading(editor.state.doc.child(index));
      if (!nested && ($head.depth !== 1 || !blockedJoin(editor.state.doc, index))) return false;
      editor.commands.setTextSelection($head.after(1) + 1);
      return true;
    },
    // A dated line in the section's history list (the old block's comment field).
    "Mod-Alt-m": ({ editor }) => noteHeading(editor),
    // Before KeyboardFirst's move: a fixed line does not move, nor a block over one. heal() could put the lines back, but the move's
    // caret (set after it) would land in the wrong line and the history would hold a move that did nothing.
    "Mod-Shift-ArrowUp": ({ editor }) => blockedMove(editor.state, -1),
    "Mod-Shift-ArrowDown": ({ editor }) => blockedMove(editor.state, 1),
  }),
});

/** Whether the top-level block at `pos` is a requirement's dependency or facts line (a fixed place). */
const fixedLine = (doc: PMNode, pos: number) => { const section = sectionAt(doc, pos); return !!section && (section.depsPos === pos || section.factsPos === pos); };

/**
 * Whether moving the caret's block (Ctrl+Shift+↑/↓) would take a requirement's line from its place: a dependency or facts line itself,
 * or a block moved up over one. A heading moved up goes with its section before a heading of its level or higher whose section ends at
 * it (moveBlock's own rule); anywhere else it is refused too. Moving down jumps over a whole requirement section.
 */
function blockedMove(state: EditorState, direction: -1 | 1) {
  const { selection, doc } = state;
  if (!(selection instanceof TextSelection) || !selection.$from.depth) return false;
  const index = selection.$from.index(0), pos = selection.$from.before(1), node = doc.child(index);
  if (fixedLine(doc, pos)) return true;
  if (direction > 0 || !index || !fixedLine(doc, pos - doc.child(index - 1).nodeSize)) return false;
  if (node.type.name !== "heading") return true;
  let fits = false;
  for (let earlier = 0; earlier < index && !fits; earlier++) {
    const candidate = doc.child(earlier);
    if (candidate.type.name !== "heading" || candidate.attrs.level > node.attrs.level) continue;
    let end = earlier + 1;
    while (end < doc.childCount && !(doc.child(end).type.name === "heading" && doc.child(end).attrs.level <= candidate.attrs.level)) end++;
    fits = end === index;
  }
  return !fits;
}

/** Where a requirement's heading, dependency line and facts line end. */
function linesEnd(doc: PMNode, section: RequirementSection) {
  return Math.max(...[section.headingPos, section.depsPos, section.factsPos].filter((pos): pos is number => pos !== undefined).map(pos => pos + doc.nodeAt(pos)!.nodeSize));
}

/**
 * Whether joining top-level block `index` into the one before it would move a requirement's lines: a note into the dependency line
 * (it holds links only), the dependency line into its heading, or a requirement with a dependency or facts line into a heading above
 * (they would become that requirement's). Joining into a paragraph is a heading's join (the state goes, the lines become notes).
 */
function blockedJoin(doc: PMNode, index: number) {
  const before = doc.child(index - 1), node = doc.child(index);
  if (!before.isTextblock || !node.isTextblock) return false;
  // The facts line directly under its heading (no dependency line) does not join into the title either (third review P2-2).
  if ((isDependencyLine(before) && node.content.size > 0) || (isRequirementHeading(before) && (isDependencyLine(node) || isFactsLine(node)))) return true;
  if (!isRequirementHeading(node) || before.type.name !== "heading") return false;
  let pos = 0;
  for (let at = 0; at < index; at++) pos += doc.child(at).nodeSize;
  const section = sectionAt(doc, pos);
  return section?.depsPos !== undefined || section?.factsPos !== undefined;
}

/** The editor side of requirement headings, for the requirements plugin's extensions. */
export const requirementHeadingExtensions = [Marks, Tint, Rules, PastedIds, CopiedIds, Keys];
