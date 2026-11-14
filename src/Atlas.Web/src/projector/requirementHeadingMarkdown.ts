import type { Node as PMNode } from "@tiptap/pm/model";
import { reportMarkdownSyntaxError } from "@atlas/markdown/core";
import t from "../i18n/projector";
import { requirementDirections, requirementStates } from "./requirementTypes";

/*
 * The Markdown of a requirement heading, as the server writes and reads it (src/Atlas.Server/Platform/Content/RequirementHeadingMarkdown.cs,
 * the reference: every rule here is that file's, checked over tests/fixtures/content/requirement-headings.json by
 * requirementHeadingMarkdown.test.ts): a trailing attribute block on a top-level heading line,
 *
 *     ### Název {#id state=waiting_for_input kind=inbound party="Jan Novák" partyContact=<guid> system=NIA from=<pageId>#<id> stateLabel=ověřit}
 *
 * A value with a space, quote, brace or backslash is quoted, with `\` before `"`, `\` and the braces. A plain heading whose text ends
 * like such a block is written with `\{`. `{#id}` alone names a heading without making it a requirement (the page source writes it for
 * a heading with aliases).
 */

/** The block's keys in the order they are written, and the heading attribute each sets. */
const KEYS = [["state", "state"], ["kind", "direction"], ["party", "party"], ["partyContact", "partyContactId"], ["system", "system"], ["from", "from"], ["stateLabel", "stateLabel"]] as const;
const ID = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/, TEMPORARY_ID = /^v\d+-h\d+$/;
const FROM = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}#[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;
const GUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
// `[^\n]` where the server's patterns say `.`: .NET's dot stops at a line feed only, JavaScript's at other line ends too.
const TOKEN = /[ \t]*(?:#([^\s"{}\\]+)|([A-Za-z]+)=(?:"((?:[^"\\]|\\[^\n])*)"|([^\s"{}\\]+)))/y;
const BARE = /^[^\s"{}\\]+$/;
const blank = (character: string | undefined) => character === " " || character === "\t";

function oddBackslashes(text: string, index: number) {
  let count = 0;
  while (index - count > 0 && text[index - count - 1] === "\\") count++;
  return count % 2 === 1;
}

/** The `#id` (key `#`) and `key=value` tokens of a block's body, unescaped; null when the body is anything else. */
function tokens(body: string): [string, string][] | null {
  const found: [string, string][] = [];
  for (let at = 0; ;) {
    while (blank(body[at])) at++;
    if (at === body.length) break;
    TOKEN.lastIndex = at;
    const match = TOKEN.exec(body);
    if (!match || found.length > KEYS.length * 2) return null;
    found.push(match[1] !== undefined ? ["#", match[1]] : [match[2]!, match[3] !== undefined ? match[3].replace(/\\([^\n])/g, "$1") : match[4]!]);
    at = TOKEN.lastIndex;
    if (at < body.length && !blank(body[at])) return null;
  }
  return found.length ? found : null;
}

/** Where the trailing attribute block of a heading line starts (and its body), or -1. */
export function headingBlockStart(line: string): { start: number; body: string } {
  const none = { start: -1, body: "" };
  let end = line.length;
  while (end > 0 && blank(line[end - 1])) end--;
  if (end < 2 || line[end - 1] !== "}" || oddBackslashes(line, end - 1)) return none;
  for (let start = end - 2; start > 0; start--) {
    if (line[start] === "}" && !oddBackslashes(line, start)) return none;
    if (line[start] !== "{" || oddBackslashes(line, start)) continue;
    if (!blank(line[start - 1]) || !/^#{1,6}(?:[ \t][^\n]*)?$/.test(line.slice(0, start).replace(/[ \t]+$/, ""))) return none;
    const body = line.slice(start + 1, end - 1);
    return tokens(body) ? { start, body } : none;
  }
  return none;
}

/** The attributes of a block's body: `headingId` and the requirement's, or why it is refused. */
export function readHeadingBlock(body: string): { attrs: Record<string, string>; error?: string } {
  const attrs: Record<string, string> = {};
  const m = t.heading.markdown;
  for (const [key, value] of tokens(body)!) {
    const name = key === "#" ? "headingId" : KEYS.find(entry => entry[0] === key)?.[1];
    if (!name) return { attrs, error: m.unknownKey(key) };
    if (name in attrs) return { attrs, error: m.repeatedKey(key) };
    const error = name === "headingId" ? (ID.test(value) ? null : m.id)
      : name === "state" ? ((requirementStates as readonly string[]).includes(value) ? null : m.state)
      : name === "direction" ? ((requirementDirections as readonly string[]).includes(value) ? null : m.kind)
      : name === "from" ? (FROM.test(value) ? null : m.from)
      : name === "partyContactId" ? (GUID.test(value) ? null : m.partyContact)
      : value.length > 200 || /[\u0000-\u001f\u007f]/.test(value) ? m.text(key) : null;
    if (error) return { attrs, error };
    attrs[name] = value;
  }
  return "partyContactId" in attrs && !("party" in attrs) ? { attrs, error: m.partyContactAlone } : { attrs };
}

const written = (value: string) => BARE.test(value) ? value : `"${value.replace(/["\\{}]/g, "\\$&")}"`;
const text = (value: unknown) => typeof value === "string" && value ? value : null;

/** The block a heading writes after its text: `{#id state=…}` for a requirement (state not null), else null. */
export function writeHeadingBlock(attrs: Record<string, unknown>): string | null {
  const state = text(attrs.state);
  if (!state) return null;
  const id = text(attrs.headingId);
  let block = `{${id && ID.test(id) && !TEMPORARY_ID.test(id) ? `#${id} ` : ""}state=${written(state)}`;
  for (const [key, attribute] of KEYS.slice(1)) {
    const value = text(attrs[attribute]);
    if (value && !(attribute === "direction" && value === "internal") && !(attribute === "partyContactId" && !text(attrs.party))) block += ` ${key}=${written(value)}`;
  }
  return `${block}}`;
}

interface SerializerState { out: string; write(text: string): void; renderInline(node: PMNode, fromBlockStart?: boolean): void; closeBlock(node: PMNode): void }
/** The heading's Markdown: its text, a `\` before a brace that would read as a block, and a top-level requirement's block. */
export function serializeHeading(state: SerializerState, node: PMNode, parent: PMNode) {
  state.write(`${"#".repeat(node.attrs.level as number)} `);
  const text = state.out.length;
  state.renderInline(node, false);
  const line = state.out.lastIndexOf("\n") + 1, { start } = headingBlockStart(state.out.slice(line));
  if (start >= 0) state.out = `${state.out.slice(0, line + start)}\\${state.out.slice(line + start)}`;
  // A title ending in `#` would read as the heading's closing sequence: its last run of `#` is escaped (EditorMarkdownWriter does the same).
  const closing = /(?:^|[ \t])(#+)$/.exec(state.out.slice(text));
  if (closing) state.out = `${state.out.slice(0, state.out.length - closing[1]!.length)}\\${closing[1]}`;
  const block = parent.type.name === "doc" ? writeHeadingBlock(node.attrs) : null;
  if (block) state.write(`${state.out.endsWith(" ") ? "" : " "}${block}`);
  state.closeBlock(node);
}

/**
 * A named heading read in the page source against the page's heading of that id: a changed state with the own state word left as it
 * was drops the word, as the state menu and the MCP tools do (a word written anew is kept).
 */
export const headingRead = (attrs: Record<string, unknown>, original: Record<string, unknown>) =>
  attrs.state !== original.state && attrs.stateLabel === original.stateLabel && attrs.stateLabel ? { ...attrs, stateLabel: "" } : attrs;

const dataName = (name: string) => `data-${name === "headingId" ? "heading-id" : name.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`)}`;
interface Token { type: string; level: number; markup: string; map: [number, number] | null; content: string; attrSet(name: string, value: string): void }
interface MarkdownIt { options?: { pageSource?: boolean }; core: { ruler: { before(name: string, rule: string, run: (state: { src: string; tokens: Token[]; md: MarkdownIt }) => void): void } } }
const installed = new WeakSet<object>();

/**
 * Reads the block of every top-level heading line before its text is parsed: the block leaves the text and becomes the heading's
 * attributes (the data-* the heading's attributes read). A refused block stays text and is reported (an error in the page source).
 * Only the page source reads it (`pageSource`, set by the editor's source session): pasted or imported Markdown and the assistant's
 * text keep `{…}` as the text it is there.
 */
export function installHeadingBlocks(md: MarkdownIt) {
  if (installed.has(md)) return;
  installed.add(md);
  md.core.ruler.before("inline", "atlas_requirement_heading", state => {
    if (!state.md.options?.pageSource) return;
    let lines: string[] | undefined;
    state.tokens.forEach((token, index) => {
      const inline = state.tokens[index + 1];
      if (token.type !== "heading_open" || token.level !== 0 || !token.markup.startsWith("#") || !token.map || inline?.type !== "inline") return;
      const line = (lines ??= state.src.split("\n"))[token.map[0]] ?? "", { start, body } = headingBlockStart(line);
      if (start < 0 || !inline.content.endsWith(line.slice(start).replace(/[ \t]+$/, ""))) return;
      const { attrs, error } = readHeadingBlock(body);
      if (error) { reportMarkdownSyntaxError({ line: token.map[0] + 1, column: start + 1, construct: "heading", message: error }); return; }
      inline.content = inline.content.slice(0, inline.content.length - (line.replace(/[ \t]+$/, "").length - start)).replace(/[ \t]+$/, "");
      for (const [name, value] of Object.entries(attrs)) token.attrSet(dataName(name), value);
    });
  });
}
