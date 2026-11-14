import { Node, type Editor } from "@tiptap/core";
import type { Node as PMNode } from "@tiptap/pm/model";
import { defineMarkdownAttributeValidator, type MarkdownValueConstraint } from "@atlas/markdown/core";
import t from "../i18n/projector";
import { dependencyNames, markdownText, requirementCheckbox, requirementMeta } from "./requirementSummary";
import type { PageRequirement } from "./requirementModel";
import { headingRequirements } from "./requirementHeading";
import { requirementDefaults, requirementDirections, requirementStates, type RequirementAttributes } from "./requirementTypes";

export const requirementAttributesOf = (node: PMNode) => ({ ...requirementDefaults, ...node.attrs }) as RequirementAttributes;

/**
 * A block's title: the text of its first paragraph, a line break read as a space, a YouTrack issue as its key (as its Markdown starts),
 * other inline components left out (as the server reads it, RequirementText.Title).
 */
export function titleOfNode(node: PMNode): string {
  let title = "";
  node.firstChild?.forEach(child => {
    title += child.isText ? child.text! : child.type.name === "hardBreak" ? " " : child.type.name === "youtrackIssue" ? String(child.attrs.key ?? "").trim() : "";
  });
  return title.replace(/^ +| +$/g, "");
}

/**
 * Every requirement block of the document with the blocks it is nested in, in document order, then its requirement headings
 * (requirementHeading.ts; a page holds one shape or the other, the conversion turns the blocks into headings).
 */
export function pageRequirements(document: PMNode, pageId?: string): PageRequirement[] {
  return [...legacyRequirements(document), ...headingRequirements(document, pageId)];
}

function legacyRequirements(document: PMNode): PageRequirement[] {
  const items: PageRequirement[] = [];
  document.forEach(block => { items.push(...legacyOfBlock(block)); });
  return items;
}

/**
 * Per top-level block, once: nodes are immutable and typing replaces only the block it is in, so the page's index (read on every
 * change) does not walk every table cell again (~2 ms a keystroke at 4x CPU on the biggest page, perf 4.0).
 */
const legacyByBlock = new WeakMap<PMNode, PageRequirement[]>();
function legacyOfBlock(block: PMNode): PageRequirement[] {
  const known = legacyByBlock.get(block);
  if (known) return known;
  const found: PageRequirement[] = [];
  const visit = (node: PMNode, ancestors: string[]) => {
    if (node.isTextblock || node.isLeaf) return;
    if (node.type.name !== "requirement") return node.forEach(child => visit(child, ancestors));
    const id = node.attrs.id as string | null ?? "";
    found.push({ id, title: titleOfNode(node), attrs: requirementAttributesOf(node), ancestors });
    node.forEach(child => visit(child, [...ancestors, id]));
  };
  visit(block, []);
  legacyByBlock.set(block, found);
  return found;
}

/** The dependency names of a document (requirementSummary.ts), once per document the serializer writes. */
const names = new WeakMap<PMNode, ReadonlyMap<string, string>>();
function namesOf(document: PMNode): ReadonlyMap<string, string> {
  let found = names.get(document);
  if (!found) names.set(document, found = dependencyNames(pageRequirements(document)));
  return found;
}

const KEY = "[a-z0-9][a-z0-9.-]{0,95}";
export const requirementKeyPattern = new RegExp(`^${KEY}$`);
const UUID = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
const uuid = `^${UUID}$`;
/** A dependency: a key of the page, or `key@pageId` for a block of another page (plan 3.1 §2.3 point 5). */
const REFERENCE = `${KEY}(?:@${UUID})?`;

// The editor validates every attribute of the document on each transaction; each pattern compiles once.
const patterns = new Map<string, RegExp>();
const compiled = (source: string) => patterns.get(source) ?? patterns.set(source, new RegExp(source)).get(source)!;

/** The editor's check of a declared constraint (only the kinds used here). */
function check(constraint: MarkdownValueConstraint, value: unknown): void {
  if (constraint.kind === "enum" && (constraint.values.includes(value as string) || (value === null && constraint.nullable))) return;
  if (constraint.kind === "boolean" && typeof value === "boolean") return;
  if (constraint.kind === "text") {
    if (value === null && constraint.nullable) return;
    if (typeof value === "string" && value.length <= (constraint.maxLength ?? Infinity)
      && !(constraint.singleLine && /[\u0000-\u001f\u007f]/.test(value)) && (!constraint.pattern || compiled(constraint.pattern).test(value))) return;
  }
  throw new TypeError(t.model.invalidAttribute);
}

/** A flat attribute with its declared rule, stored in HTML as data-<name>. */
export function requirementAttribute(name: string, fallback: string | null, constraint: MarkdownValueConstraint) {
  return {
    default: fallback,
    validate: defineMarkdownAttributeValidator(constraint, value => check(constraint, value)),
    parseHTML: (element: HTMLElement) => element.getAttribute(`data-${name}`) ?? fallback,
    renderHTML: (attributes: Record<string, unknown>) => attributes[name] === null ? {} : { [`data-${name}`]: attributes[name] },
  };
}

export const idAttribute = () => requirementAttribute("id", null, { kind: "text", nullable: true, pattern: uuid });

const text = (maxLength: number, pattern?: string): MarkdownValueConstraint => ({ kind: "text", maxLength, singleLine: true, ...(pattern ? { pattern } : {}) });

/** The rule of each attribute besides the id (spec §2.1), as the schema manifest publishes it and the server checks it. */
const constraints: Record<keyof RequirementAttributes, MarkdownValueConstraint> = {
  key: text(96, `^(?:${KEY})?$`),
  direction: { kind: "enum", values: [...requirementDirections] },
  state: { kind: "enum", values: [...requirementStates] },
  stateLabel: text(200),
  date: text(10, "^(?:[0-9]{4}-[0-9]{2}-[0-9]{2})?$"),
  party: text(200),
  partyContactId: text(36, `^(?:${UUID})?$`),
  where: text(200),
  system: text(200),
  requires: text(1000, `^(?:${REFERENCE}(?:,${REFERENCE})*)?$`),
};

/** Whether the document takes this value of the attribute; the panel checks before it writes. */
export function validAttribute(name: keyof RequirementAttributes, value: unknown) {
  try { check(constraints[name], value); return true; } catch { return false; }
}

// Minimal serializer state of prosemirror-markdown (tiptap-markdown's subclass) that the projection uses.
interface SerializerState {
  out: string;
  delim: string;
  inTable?: boolean;
  readonly nodes: unknown;
  readonly marks: unknown;
  readonly options: unknown;
  write(content?: string): void;
  closeBlock(node: PMNode): void;
  text(content: string, escape?: boolean): void;
  renderInline(parent: PMNode, fromBlockStart?: boolean): void;
  render(node: PMNode, parent: PMNode, index: number): void;
  wrapBlock(delim: string, firstDelim: string | null, node: PMNode, render: () => void): void;
  flushClose(size?: number): void;
  ensureNewLine(): void;
}

/**
 * A requirement of a deployment page (spec 2026-10-27 §2.1): the first paragraph is its title, the rest notes and nested requirements.
 * renderHTML is the static and headless look; the editor's view is RequirementBlockView (requirementsPlugin.ts).
 */
export const RequirementNode = Node.create({
  name: "requirement",
  group: "block",
  content: "paragraph block*",
  draggable: true,
  defining: true,
  addAttributes: () => ({
    id: idAttribute(),
    ...Object.fromEntries((Object.keys(constraints) as (keyof RequirementAttributes)[])
      .map(name => [name, requirementAttribute(name, requirementDefaults[name], constraints[name])])),
  }),
  parseHTML: () => [{ tag: "div[data-requirement]", contentElement: ".atlas-requirement__content" }],
  renderHTML: ({ node, HTMLAttributes }) => {
    const attrs = requirementAttributesOf(node);
    return ["div", { ...HTMLAttributes, "data-requirement": "", class: "atlas-requirement" },
      ["div", { class: "atlas-requirement__meta", contenteditable: "false" }, `${attrs.state === "done" ? "☑" : "☐"} ${requirementMeta(attrs)}`],
      ["div", { class: "atlas-requirement__content" }, 0]];
  },
  // `label` names the block's protected region in the page source („Zdroj“): „Chráněný blok: Požadavek „SSL certifikát““ (U4-3).
  addStorage: () => ({ label: t.model.label, markdown: { serialize(this: { editor?: Editor }, state: SerializerState, node: PMNode) {
    const attrs = requirementAttributesOf(node);
    // The block is written in a serializer state of its own, then line by line into the page's: inside a wrapped block
    // prosemirror-markdown tests the whole output so far on every write (atBlank), which made a page of hundreds of blocks quadratic.
    const own = new (state.constructor as new (...args: unknown[]) => SerializerState)(state.nodes, state.marks, state.options);
    own.inTable = state.inTable;
    // A task-list line: "- [x] **title** — meta", the rest of the block indented and tight under it (spec §2.3).
    own.wrapBlock("  ", requirementCheckbox(attrs.state), node, () => {
      const title = node.firstChild!;
      if (title.childCount) { own.write("**"); own.renderInline(title, false); own.write("**"); }
      // The serializer is not told which document it writes; dependencies are named from the editor's (as the summary counts it).
      const dependencies = this.editor ? namesOf(this.editor.state.doc) : undefined;
      own.text(markdownText((title.childCount ? " — " : "") + requirementMeta(attrs, dependencies)));
      node.forEach((child, _offset, index) => {
        if (index === 0) return;
        // The title line closed no block; later children follow tightly, as in a tight list.
        if (index === 1) own.ensureNewLine(); else own.flushClose(1);
        own.render(child, node, index);
      });
    });
    const lines = own.out.split("\n");
    lines.forEach((line, index) => {
      // A blank line takes the delimiter without its trailing space, as flushClose writes it.
      if (line) { state.write(); state.out += line; } else state.out += state.delim.replace(/\s+$/, "");
      if (index < lines.length - 1) state.out += "\n";
    });
    state.closeBlock(node);
  } } }),
});
