// The readable form of requirement blocks (spec 2026-10-27 §2.3). Platform/Content/EditorMarkdownWriter.cs writes the same text;
// tests/fixtures/content/requirements.json keeps both sides equal.
import t from "../i18n/projector";
import { keyed, satisfied, summarize } from "./requirementModel";
// A cycle with requirementReadiness (it reads the state words here); both use the other only inside functions.
import { requirementReadiness } from "./requirementReadiness";
import type { OverviewJson } from "../overview/overviewKinds";
import type { RequirementAttributes, RequirementDirection, RequirementItem, RequirementState } from "./requirementTypes";
import { historyStampText } from "@atlas/markdown/core";

/** The state words as the page's text carries them (requirementMeta, the Markdown the server writes too): content, Czech in every language of the tool. */
const stateLabels: Record<RequirementState, string> = {
  planned: "neřešeno", waiting_for_input: "čeká na vstup", in_progress: "rozpracováno", done: "hotovo", blocked: "blokováno", not_applicable: "netýká se",
};

/**
 * The one vocabulary of the directions, as Platform/Content/RequirementText.cs writes it too: the word of the badge and the
 * projection (`label`), the party in the projection ("od: IT KBN", `party`) and the party field of the panel (`partyField`).
 * Content, Czech in every language of the tool: what the tool shows of it is `directionLabel`, `partyPrefix` and `partyLabel`.
 */
export const directionWords: Record<RequirementDirection, { readonly label: string; readonly party: string; readonly partyField: string }> = {
  internal: { label: "naše práce", party: "kdo", partyField: "Kdo" },
  inbound: { label: "vstup", party: "od", partyField: "Od koho" },
  outbound: { label: "předání", party: "komu", partyField: "Komu" },
};

/** The party field's name: who works on the block, whom it comes from or goes to, or on whom it waits. */
export const partyLabel = (direction: RequirementDirection, state: RequirementState) =>
  state === "waiting_for_input" ? t.summary.waitingOn : t.summary.partyField[direction];

/** The state's word as the tool shows it. */
export const requirementStateLabel = (state: RequirementState) => t.state[state];

/** The direction's word (the badge) as the tool shows it. */
export const directionLabel = (direction: RequirementDirection) => t.summary.direction[direction];

/** „from“ / „to“ before a party in a line of the tool („od IT KBN“). */
export const partyPrefix = (direction: RequirementDirection) => t.summary.partyPrefix[direction];

/** How a block's state reads: its own word (`stateLabel`), else the state's. */
export const stateText = (attrs: Pick<RequirementAttributes, "state" | "stateLabel">) => attrs.stateLabel || t.state[attrs.state];

/** The group key of a block that names no party, in the summary's and „Na co čekáme“'s groups alike (as the server groups it); shown by `partyName`. */
export const unknownParty = "Bez určení";
/** A party group's name as the tool shows it. */
export const partyName = (party: string) => party === unknownParty ? t.summary.unknownParty : party;

/** The task-list marker: [x] done, [-] does not apply, [ ] anything else; the state word follows in the line. */
export const requirementCheckbox = (state: RequirementState) => state === "done" ? "- [x] " : state === "not_applicable" ? "- [-] " : "- [ ] ";

/** Free text written into Markdown: a `<` of a party or a place must not read as HTML, the same as the editor writes text nodes. */
export const markdownText = (text: string) => text.replace(/</g, "&lt;").replace(/>/g, "&gt;");

/**
 * How the page's dependencies read, by key: the title of the block, "title (key)" when two blocks share the title, the key when the
 * block has none. A key without a block on the page is not in the map and reads as itself.
 */
export function dependencyNames(items: readonly RequirementItem[]): Map<string, string> {
  const shared = new Map<string, number>();
  items.forEach(item => { if (item.title) shared.set(item.title, (shared.get(item.title) ?? 0) + 1); });
  return new Map(items.filter(item => item.attrs.key).map(item => [item.attrs.key,
    !item.title ? item.attrs.key : shared.get(item.title)! > 1 ? `${item.title} (${item.attrs.key})` : item.title]));
}

/** The line after the title: "hotovo 10. 9. 2026 · vstup · od: IT KBN · služba: NIA · uloženo: Keepass · klíč: portal.ssl · závisí na: SSL certifikát". */
export function requirementMeta(attrs: RequirementAttributes, names: ReadonlyMap<string, string> = new Map()): string {
  const words = directionWords[attrs.direction];
  return [
    (attrs.stateLabel || stateLabels[attrs.state]) + (attrs.date ? " " + historyStampText({ date: attrs.date }) : ""),
    words.label,
    attrs.party && `${words.party}: ${attrs.party}`,
    attrs.system && `služba: ${attrs.system}`,
    attrs.where && `uloženo: ${attrs.where}`,
    attrs.key && `klíč: ${attrs.key}`,
    attrs.requires && `závisí na: ${attrs.requires.split(",").map(key => names.get(key) ?? key).join(", ")}`,
  ].filter(Boolean).join(" · ");
}

/** The order of the readiness groups in the summary; „čeká na N prerekvizit“ goes between, by N. */
const readinessOrder = ["lze nasadit", "lze předat", "čeká na dodání", "čeká na vstup"];

/**
 * "57 % hotovo (12 z 21) · čeká na: SMS brána (IT KBN) · nedodané vstupy: SMS brána (IT KBN) · lze nasadit: Core · čeká na 2 prerekvizity: NIA":
 * whom the page waits on (waiting for input only, not our work in progress) apart from the inputs not delivered; then the unfinished
 * blocks grouped by their readiness (requirementReadiness: its words, by direction, blocked however deep), five titles each, then how
 * many more. Blocks that do not apply are not counted.
 */
export function requirementSummaryText(items: readonly RequirementItem[]): string {
  const { percent, done, total, waiting, missing } = summarize(items);

  const named = (list: readonly RequirementItem[]) => list.map(item => item.title + (item.attrs.party ? ` (${item.attrs.party})` : "")).join(", ");
  const byKey = keyed(items), memo = new Map<RequirementItem, RequirementItem[]>(), groups = new Map<string, { rank: number; titles: string[] }>();
  for (const item of items.filter(entry => !satisfied(entry.attrs.state))) {
    const { state, content: label, prerequisites } = requirementReadiness(item, byKey, memo);
    const rank = state === "blocked" ? Number.MAX_SAFE_INTEGER : state === "waiting" && prerequisites.length ? readinessOrder.length + prerequisites.length : readinessOrder.indexOf(label);
    const group = groups.get(label) ?? groups.set(label, { rank, titles: [] }).get(label)!;
    group.titles.push(item.title || item.attrs.key || "bez názvu");
  }
  const readiness = [...groups].sort(([, a], [, b]) => a.rank - b.rank)
    .map(([label, { titles }]) => ` · ${label}: ${titles.slice(0, 5).join(", ")}${titles.length > 5 ? ` +${titles.length - 5}` : ""}`).join("");
  return `${percent} % hotovo (${done} z ${total})` + (waiting.length ? ` · čeká na: ${named(waiting)}` : "")
    + (missing.length ? ` · nedodané vstupy: ${named(missing)}` : "") + readiness;
}

/** The text of the first paragraph, a line break read as a space; inline components are left out (the server reads it the same way). */
export const titleOfJson = (node: OverviewJson) => (node.content?.[0]?.content ?? [])
  .map(child => child.type === "text" ? child.text ?? "" : child.type === "hardBreak" ? " " : "").join("").replace(/^ +| +$/g, "");
