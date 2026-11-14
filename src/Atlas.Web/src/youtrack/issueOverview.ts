import { downloadMarkdownFile } from "@atlas/markdown/core";
import { h, type FunctionalComponent } from "vue";
import { useToast } from "../composables/useToast";
import t from "../i18n/youtrack";
import { eachNode, registerOverviewKind, type OverviewCardAction, type OverviewDocument } from "../overview/overviewKinds";
import IssueOverviewCard from "./IssueOverviewCard.vue";

/** An issue as its `youtrackIssue` node stores it (`@atlas/markdown/plugins/youtrack`): the snapshot of when it was inserted or refreshed. */
export interface OverviewIssue { readonly key: string; readonly title: string; readonly state: string; readonly url: string }

/**
 * The YouTrack issues of a document (a page, a board as one document): its `youtrackIssue` nodes, each issue once by its key (the
 * first occurrence's snapshot), in document order.
 */
export function documentIssues(document: OverviewDocument): OverviewIssue[] {
  const issues = new Map<string, OverviewIssue>();
  const add = (type: string | undefined, attrs: Readonly<Record<string, unknown>> | undefined) => {
    const key = typeof attrs?.key === "string" ? attrs.key.trim() : "";
    if (type !== "youtrackIssue" || !key || issues.has(key.toUpperCase())) return;
    // Only a web address becomes a link (a stored `javascript:` never does).
    const url = String(attrs?.url ?? "");
    issues.set(key.toUpperCase(), { key, title: String(attrs?.title ?? ""), state: String(attrs?.state ?? ""), url: /^https?:\/\//i.test(url) ? url : "" });
  };
  eachNode(document, add);
  return [...issues.values()];
}

/** The keys as one line to paste („Kopírovat klíče“). */
export const issueKeys = (issues: readonly OverviewIssue[]) => issues.map(issue => issue.key).join(", ");

/** A CSV cell: quoted, and a value a spreadsheet would run as a formula starts with an apostrophe (as the server's ContactExport). */
const cell = (text: string) => `"${(/^[=+\-@\t\r\n]/.test(text) ? `'${text}` : text).replace(/"/g, '""')}"`;
/** The issues as CSV for Excel (UTF-8 with BOM, `;` between cells, CRLF), one row each: key, title, state, link (the header in the page language). */
export const issuesCsv = (issues: readonly OverviewIssue[]) =>
  `\uFEFF${t.csvHeader}\r\n${issues.map(issue => [issue.key, issue.title, issue.state, issue.url].map(cell).join(";")).join("\r\n")}${issues.length ? "\r\n" : ""}`;

const ReadCard: FunctionalComponent<{ items: readonly OverviewIssue[]; scope: string; actions?: readonly OverviewCardAction[] }> = props =>
  h(IssueOverviewCard, { issues: props.items, actions: props.actions });

/**
 * The overview of kind `issues` (plan 3.4 R6, overviewKinds.ts), registered with the web's YouTrack integration (documentWidgets.ts
 * imports it beside the YouTrack plugin): the issues a page or a board shows, from their stored snapshots (no request to YouTrack),
 * with their keys to copy and a CSV. No levels: the issue chip has one look.
 */
registerOverviewKind<OverviewIssue>({
  kind: "issues", get label() { return t.label; }, collect: documentIssues, card: IssueOverviewCard, readCard: ReadCard,
  actions: [
    { id: "copy-keys", get label() { return t.copyKeys; }, icon: "copy", run: async ({ items }) => {
      const toast = useToast();
      try { await navigator.clipboard.writeText(issueKeys(items)); toast.success(t.keysCopied); }
      catch { toast.error(t.keysCopyFailed); }
    } },
    { id: "csv", get label() { return t.csv; }, icon: "download",
      run: ({ items }) => downloadMarkdownFile(document, t.csvFile, new Blob([issuesCsv(items)], { type: "text/csv;charset=utf-8" })) },
  ],
});
