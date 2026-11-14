import { Extension } from "@tiptap/core";
import type { Node as PMNode } from "@tiptap/pm/model";
import { defineMarkdownAttributeValidator, type MarkdownValueConstraint } from "@atlas/markdown/core";
import t from "../i18n/projector";
import { headingRead, installHeadingBlocks, serializeHeading } from "./requirementHeadingMarkdown";
import type { PageRequirement } from "./requirementModel";
import { requirementDefaults, requirementDirections, requirementStates, type RequirementDirection, type RequirementState } from "./requirementTypes";

/*
 * A requirement is a heading with a state: the heading's own
 * attributes, the „Závislosti:“ line (a paragraph with `dependencies`) and the facts line („do 15. 11. 2026“) under it; the party is an attribute (shape §6). This file
 * is the shape and its reading, no view: the Přehled, the index and the editor's marks (requirementHeadingView.ts) all read it here.
 */

/** A heading id (the shape's charset: the dot is new, `portal.vpn`). */
export const headingIdPattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;

// The editor validates every attribute on each transaction: a small check of the declared rule (enum, text, boolean).
function check(constraint: MarkdownValueConstraint, value: unknown) {
  if (value === null && "nullable" in constraint && constraint.nullable) return;
  if (constraint.kind === "enum" && constraint.values.includes(value as string)) return;
  if (constraint.kind === "boolean" && typeof value === "boolean") return;
  if (constraint.kind === "text" && typeof value === "string" && value.length <= (constraint.maxLength ?? Infinity) && !/[\u0000-\u001f\u007f]/.test(value)
    && (!constraint.pattern || new RegExp(constraint.pattern, "u").test(value))) return;
  throw new TypeError(t.model.invalidAttribute);
}

const dataName = (name: string) => `data-${name.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`)}`;
/**
 * A flat attribute in HTML as data-<name>, written only when it is not its default (a plain heading's HTML stays as it was). Not kept
 * on split: Enter at a heading's end makes a plain paragraph (tiptap's splitBlock), the middle is requirementHeadingView.ts's rule.
 */
function attribute(name: string, fallback: string | boolean | null, constraint: MarkdownValueConstraint) {
  return {
    default: fallback,
    keepOnSplit: false,
    validate: defineMarkdownAttributeValidator(constraint, value => check(constraint, value)),
    // A value the rule refuses (pasted HTML, data-state="bogus") reads as the default: the rest of the paste stays (third review P3-3).
    parseHTML: (element: HTMLElement) => {
      const value = typeof fallback === "boolean" ? element.hasAttribute(dataName(name)) : element.getAttribute(dataName(name)) ?? fallback;
      try { check(constraint, value); return value; } catch { return fallback; }
    },
    renderHTML: (attributes: Record<string, unknown>) => attributes[name] === fallback ? {} : { [dataName(name)]: attributes[name] === true ? "" : attributes[name] },
  };
}
const text = (maxLength: number): MarkdownValueConstraint => ({ kind: "text", maxLength, singleLine: true });

/** The heading's requirement attributes and the dependency line's flag (the shape §1); `aliases` belongs to heading-anchors (schema 2). */
export const RequirementHeadingAttributes = Extension.create({
  name: "requirementHeading",
  // The heading's Markdown is the server's (requirementHeadingMarkdown.ts): the editor's heading writes its line with `heading`, the
  // parser reads the block with the rule `setup` installs.
  addStorage: () => ({ markdown: { heading: serializeHeading, headingRead, parse: { setup: installHeadingBlocks } } }),
  // The dependency line's drawn label (requirementHeading.css) in the language of the tool.
  onCreate() { this.editor.view.dom.style.setProperty("--rq-dependencies-label", JSON.stringify(t.heading.dependencies)); },
  addGlobalAttributes: () => [
    { types: ["heading"], attributes: {
      state: attribute("state", null, { kind: "enum", values: [...requirementStates], nullable: true }),
      stateLabel: attribute("stateLabel", "", text(200)),
      direction: attribute("direction", "internal", { kind: "enum", values: [...requirementDirections] }),
      system: attribute("system", "", text(200)),
      // The party shown in the kind chip (shape §6): its display name and the contact's objectId; the word od/pro is never stored.
      party: attribute("party", "", text(200)),
      partyContactId: attribute("partyContactId", "", { kind: "text", maxLength: 36, singleLine: true, pattern: "^(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})?$" }),
      from: attribute("from", "", { kind: "text", maxLength: 200, singleLine: true, pattern: "^(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}#[A-Za-z0-9][A-Za-z0-9._-]{0,127})?$" }),
    } },
    { types: ["paragraph"], attributes: { dependencies: attribute("dependencies", false, { kind: "boolean" }) } },
  ],
});

/** What a heading attribute set drops to become a plain heading again (Enter in its middle, „Změnit na odstavec“). */
export const plainHeadingAttrs = { state: null, stateLabel: "", direction: "internal", system: "", party: "", partyContactId: "", from: "", aliases: "" } as const;

export const isRequirementHeading = (node: PMNode | null | undefined): boolean => node?.type.name === "heading" && node.attrs.state != null;
export const isDependencyLine = (node: PMNode | null | undefined): boolean => node?.type.name === "paragraph" && node.attrs.dependencies === true;

/** One dependency: a section link in the dependency line. */
export interface HeadingDependency { readonly pageId: string; readonly headingId: string; readonly title: string }
/** What a requirement heading says, from the fixed places only (the attributes, the dependency line, the facts line's deadline). */
export interface HeadingFacts {
  readonly id: string;
  readonly level: number;
  readonly state: RequirementState;
  readonly stateLabel: string;
  readonly direction: RequirementDirection;
  readonly system: string;
  readonly from: string;
  readonly title: string;
  readonly dependencies: readonly HeadingDependency[];
  readonly party: string;
  readonly partyContactId: string;
  /** yyyy-MM-dd, "" for none. */
  readonly deadline: string;
  /** The day it was checked off, yyyy-MM-dd, "" for none (the facts line's „hotovo“). */
  readonly completed: string;
}

/** A section link's target: `#/pages/<pageId>?heading=<id>` (any prefix before it). */
export function sectionTarget(url: unknown): { pageId: string; headingId: string } | null {
  const match = typeof url === "string" ? /pages\/([^/?#]+)\?(?:[^#]*&)?heading=([A-Za-z0-9._-]+)/.exec(url) : null;
  return match ? { pageId: decodeURIComponent(match[1]!), headingId: match[2]! } : null;
}
export const sectionUrl = (pageId: string, headingId: string) => `#/pages/${encodeURIComponent(pageId)}?heading=${headingId}`;

/**
 * An atom as the server reads it (RequirementHeadings.Text, RequirementText.Title, SQL atlas_projector_text): a line break as a space, a
 * YouTrack issue as its key; in a facts line also a contact as its name and a page link as its title; anything else (and those two in a
 * title) as nothing.
 */
const atomText = (child: PMNode, facts = true) => child.type.name === "hardBreak" ? " " : child.type.name === "youtrackIssue" ? String(child.attrs.key ?? "").replace(/^ +| +$/g, "")
  : !facts ? "" : child.type.name === "contactInline" ? contactOf(child)?.name ?? "" : child.type.name === "pageReference" ? String(child.attrs.title ?? "") : "";
/** The text of an inline run: text as it is (marks do not matter), atoms as atomText reads them. */
function inlineText(node: PMNode, facts = true) {
  let result = "";
  node.forEach(child => { result += child.isText ? child.text! : atomText(child, facts); });
  return result;
}
const contactOf = (node: PMNode) => (node.attrs.data as { contacts?: { name?: string; objectId?: string }[] } | null)?.contacts?.[0];

const trimSpaces = (text: string) => text.replace(/^[  ]+|[  ]+$/g, "");
/** „do 15. 11. 2026“ (spaces or no-break spaces between the parts, nothing after the date) as yyyy-MM-dd for a day in the calendar, else null. */
export function segmentDeadline(segment: string): string | null {
  const match = /^do[  ]+(\d{1,2})\.[  ]*(\d{1,2})\.[  ]*(\d{4})$/.exec(trimSpaces(segment));
  if (!match) return null;
  const [day, month, year] = [Number(match[1]), Number(match[2]), Number(match[3])], date = new Date(0);
  // setUTCFullYear takes the year as written (Date.UTC would read 0001–0099 as 1901–1999); C# DateOnly's calendar, years 1–9999.
  date.setUTCFullYear(year, month - 1, day);
  return year >= 1 && date.getUTCFullYear() === year && date.getUTCDate() === day && date.getUTCMonth() === month - 1 ? date.toISOString().slice(0, 10) : null;
}

const doneWord = /^[\s ]*hotovo(?=[\s ])/;
/** „hotovo 5. 11. 2026“ (the day a requirement was checked off, 3.5's wording) as yyyy-MM-dd, else null. */
export const segmentDone = (segment: string) => doneWord.test(segment) ? segmentDeadline(segment.replace(doneWord, "do")) : null;

/**
 * The facts grammar (shape §1 and §6, one rule with RequirementHeadings.cs; the conversion's SQL reads only the deadline): a facts line
 * is a paragraph whose whole text is „do <d. M. yyyy>“, „hotovo <d. M. yyyy>“ (the day it was checked off) or both, „do … · hotovo …“
 * (days in the calendar); a paragraph with „od “/„pro “ parts is a note now (the legacy shape the conversion job moves).
 */
export function factsOfText(text: string): { deadline: string; completed: string } | null {
  const segments = text.split(" · "), deadlines = segments.map(segmentDeadline).filter(x => x !== null), done = segments.map(segmentDone).filter(x => x !== null);
  if (deadlines.length > 1 || done.length > 1 || deadlines.length + done.length !== segments.length) return null;
  return { deadline: deadlines[0] ?? "", completed: done[0] ?? "" };
}
/** Stored JSON of a node, for the 3→4 move. */
type JsonNode = { type?: string; text?: string; attrs?: Record<string, unknown>; content?: JsonNode[] };
const SEPARATOR = " · ";
const jsonContact = (node: JsonNode) => (node.attrs?.data as { contacts?: { name?: unknown; objectId?: unknown }[] } | null | undefined)?.contacts?.[0];
/** A node's text as RequirementHeadings.Text reads it. */
const jsonText = (node: JsonNode): string => node.type === "text" ? node.text ?? "" : node.type === "hardBreak" ? " "
  : node.type === "contactInline" ? String(jsonContact(node)?.name ?? "") : node.type === "youtrackIssue" ? String(node.attrs?.key ?? "").replace(/^ +| +$/g, "")
  : node.type === "pageReference" ? String(node.attrs?.title ?? "") : "";
const lineText = (paragraph: JsonNode) => (paragraph.content ?? []).map(jsonText).join("");
/** The party of a version-3 segment: the rest after „od “ or „pro “ (RequirementHeadings.Party). */
const segmentParty = (segment: string) => {
  const text = trimSpaces(segment);
  return text.startsWith("od ") ? trimSpaces(text.slice(3)) : text.startsWith("pro ") ? trimSpaces(text.slice(4)) : null;
};
/** A version-3 facts line's party, its contact (only when the party segment is that chip) and deadline (RequirementHeadings.LegacyFactsOf). */
function legacyFacts(paragraph: JsonNode) {
  const segments = lineText(paragraph).split(SEPARATOR);
  if (segmentParty(segments[0]) === null && segmentDeadline(segments[0]) === null) return null;
  const at = segments.findIndex(segment => segmentParty(segment) !== null);
  const party = at < 0 ? null : segmentParty(segments[at]);
  let [contact, text] = ["", ""];
  for (const node of paragraph.content ?? []) {
    if (node.type === "contactInline" && !contact && text.split(SEPARATOR).length - 1 === at && trimSpaces(String(jsonContact(node)?.name ?? "")) === party)
      contact = typeof jsonContact(node)?.objectId === "string" ? String(jsonContact(node)!.objectId) : "";
    text += jsonText(node);
  }
  return { party: party ?? "", contact, date: segments.map(segmentDeadline).find(date => date !== null) ?? "" };
}
/** Whether a version-3 line says only what the move keeps: text and at most one contact, each segment one party or one deadline, at most one of each. */
function cleanLine(paragraph: JsonNode) {
  const nodes = paragraph.content ?? [], segments = lineText(paragraph).split(SEPARATOR);
  return nodes.every(node => node.type === "text" || node.type === "contactInline") && nodes.filter(node => node.type === "contactInline").length <= 1
    && segments.every(segment => segmentParty(segment) !== null || segmentDeadline(segment) !== null)
    && segments.filter(segment => segmentParty(segment) !== null).length <= 1 && segments.filter(segment => segmentDeadline(segment) !== null).length <= 1;
}
const deadlineText = (date: string) => { const [year, month, day] = date.split("-").map(Number); return `do ${day}. ${month}. ${year}`; };
/** The longest party the heading's attribute holds (the schema's limit). */
const MAX_PARTY = 200;

/**
 * The move of a version-3 document (shape §6, atlas-requirements 3 → 4), the same as RequirementHeadings.MoveParty (both held to the
 * `legacy` cases of tests/fixtures/content/requirement-headings.json): each requirement's „od|pro [kontakt] · do d. M. yyyy“ line at the
 * facts line's place becomes the heading's `party` / `partyContactId` and the deadline line; a line that says anything else, or a party
 * over the limit, stays under the deadline line as a note. A heading with a party, or a line that is a deadline line, stays.
 */
export function moveLegacyParty<T extends JsonNode>(document: T): T {
  const content = [...(document.content ?? [])];
  for (let i = content.length - 1; i >= 0; i--) {
    const heading = content[i];
    if (heading.type !== "heading" || typeof heading.attrs?.state !== "string" || trimSpaces(String(heading.attrs.party ?? "")) !== "") continue;
    const place = content[i + 1]?.type === "paragraph" && content[i + 1].attrs?.dependencies === true ? i + 2 : i + 1;
    const line = content[place];
    if (line?.type !== "paragraph" || segmentDeadline(lineText(line)) !== null) continue;
    const facts = legacyFacts(line);
    if (!facts) continue;
    const fits = facts.party.length <= MAX_PARTY;
    if (facts.party && fits) content[i] = { ...heading, attrs: { ...heading.attrs, party: facts.party, ...(facts.contact ? { partyContactId: facts.contact } : {}) } };
    const replaced: JsonNode[] = [];
    if (facts.date) replaced.push({ type: "paragraph", content: [{ type: "text", text: deadlineText(facts.date) }] });
    if (!fits || !cleanLine(line)) replaced.push(line);
    content.splice(place, 1, ...replaced);
  }
  return { ...document, content };
}

const factsCache = new WeakMap<PMNode, { deadline: string; completed: string } | null>();
function factsOf(paragraph: PMNode) {
  if (paragraph.type.name !== "paragraph") return null;
  if (!factsCache.has(paragraph)) factsCache.set(paragraph, factsOfText(inlineText(paragraph)));
  return factsCache.get(paragraph)!;
}
/** Whether a paragraph's text is a facts line („do 15. 11. 2026“); it is the requirement's only at its fixed place (requirementSections). */
export const isFactsLine = (node: PMNode) => factsOf(node) !== null;

/** The deadline of a facts line (the shape §1); empty for a paragraph that is none. */
export function readFacts(paragraph: PMNode | null | undefined): { deadline: string; completed: string } {
  return (paragraph && factsOf(paragraph)) || { deadline: "", completed: "" };
}

const PAGE_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** A pageReference's target as a dependency (RequirementHeadings.Links): a page id (lower case) and a valid `?heading=`, else null. */
export function linkTarget(id: unknown, url: unknown): { pageId: string; headingId: string } | null {
  const pageId = String(id ?? ""), headingId = /[?&]heading=([^&#]+)/.exec(String(url ?? ""))?.[1] ?? "";
  return PAGE_ID.test(pageId) && headingIdPattern.test(headingId) ? { pageId: pageId.toLowerCase(), headingId } : null;
}
/** The dependency line's targets in order, each once (as RequirementHeadings.Links). */
function dependenciesOf(deps: PMNode | null) {
  const found: HeadingDependency[] = [], seen = new Set<string>();
  deps?.forEach(child => {
    const target = child.type.name === "pageReference" ? linkTarget(child.attrs.id, child.attrs.url) : null, key = target && `${target.pageId}#${target.headingId}`;
    if (!target || seen.has(key!)) return;
    seen.add(key!);
    found.push({ ...target, title: String(child.attrs.title ?? "") });
  });
  return found;
}

const facts = new WeakMap<PMNode, { deps: PMNode | null; line: PMNode | null; value: HeadingFacts }>();
/** A requirement heading's facts, cached per heading node and its two lines: a change elsewhere reads nothing again. */
export function headingFacts(heading: PMNode, deps: PMNode | null = null, line: PMNode | null = null): HeadingFacts {
  const known = facts.get(heading);
  if (known && known.deps === deps && known.line === line) return known.value;
  const attrs = heading.attrs, dependencies = dependenciesOf(deps), party = trimSpaces(String(attrs.party ?? ""));
  const value: HeadingFacts = { id: String(attrs.headingId ?? ""), level: Number(attrs.level) || 1, state: attrs.state as RequirementState, stateLabel: String(attrs.stateLabel ?? ""),
    direction: (attrs.direction ?? "internal") as RequirementDirection, system: String(attrs.system ?? ""), from: String(attrs.from ?? ""), party, partyContactId: party ? String(attrs.partyContactId ?? "") : "",
    title: inlineText(heading, false).replace(/^ +| +$/g, ""), dependencies, ...readFacts(line) };
  facts.set(heading, { deps, line, value });
  return value;
}

/** A requirement's section: the heading and the top-level siblings up to the next heading of any level (`end`). */
export interface RequirementSection {
  readonly id: string;
  /** The heading's position (= `headingPos`, the section's start). */
  readonly pos: number;
  readonly end: number;
  readonly headingPos: number;
  readonly depsPos?: number;
  readonly factsPos?: number;
  readonly facts: HeadingFacts;
}

const sections = new WeakMap<PMNode, RequirementSection[]>();
/** The requirement sections of a document in order, once per document; each one's facts from the per-node cache. */
export function requirementSections(doc: PMNode): RequirementSection[] {
  let found = sections.get(doc);
  if (found) return found;
  found = [];
  // Fixed places (shape §1): the dependency line is child 1 of the section, the facts line the next one; nothing further down is read.
  let open: { heading: PMNode; pos: number; index: number; deps?: [PMNode, number]; line?: [PMNode, number] } | null = null;
  const close = (end: number) => {
    if (!open) return;
    const { heading, pos, deps, line } = open, value = headingFacts(heading, deps?.[0] ?? null, line?.[0] ?? null);
    found!.push({ id: value.id, pos, end, headingPos: pos, ...(deps ? { depsPos: deps[1] } : {}), ...(line ? { factsPos: line[1] } : {}), facts: value });
    open = null;
  };
  doc.forEach((node, pos) => {
    if (node.type.name === "heading") { close(pos); if (isRequirementHeading(node)) open = { heading: node, pos, index: 0 }; return; }
    if (!open) return;
    const index = ++open.index;
    if (index === 1 && isDependencyLine(node)) open.deps = [node, pos];
    else if (index === (open.deps ? 2 : 1) && isFactsLine(node)) open.line = [node, pos];
  });
  close(doc.content.size);
  sections.set(doc, found);
  return found;
}

/** The section holding top-level position `pos` (a heading or a line of it), if any. */
export const sectionAt = (doc: PMNode, pos: number) => requirementSections(doc).find(section => section.pos <= pos && pos < section.end);

/**
 * The requirement headings as the page's requirements (`pageRequirements`, the Přehled and the index): key = id = headingId, `requires`
 * the dependency line's ids (`id`, `id@pageId` for another page), the heading's party and the facts line's deadline, never nested. `pageId` is the
 * page the document is; without it a link counts as this page's when its id is a requirement heading here.
 */
export function headingRequirements(doc: PMNode, pageId?: string): PageRequirement[] {
  const found = requirementSections(doc), ids = new Set(found.map(section => section.id));
  const own = (dependency: HeadingDependency) => pageId ? dependency.pageId === pageId.toLowerCase() : ids.has(dependency.headingId);
  return found.map(({ facts: item }) => ({
    id: item.id, title: item.title, ancestors: [],
    attrs: { ...requirementDefaults, key: item.id, state: item.state, stateLabel: item.stateLabel, direction: item.direction, system: item.system,
      date: item.deadline, party: item.party, partyContactId: item.partyContactId,
      requires: item.dependencies.map(dependency => own(dependency) ? dependency.headingId : `${dependency.headingId}@${dependency.pageId}`).join(",") },
  }));
}
