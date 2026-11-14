import { api, ApiError } from "../api";
import t from "../i18n/shell";
// The modules themselves, not the barrel: the barrel drags the intent rules and the link phrases into the first screen.
import { detectSecrets } from "../assist/rules/secretDetector";
import { sessionSecretExamples, type SecretExamples } from "../assist/rules/examples";
import { spanText } from "../assist/rules/span";
import type { RevealTarget } from "../editor/revealTarget";
import { buildShellHashEncoded } from "../shellRoute";
import type { PublicationCheck, PublicationCheckRun } from "./publication";

/**
 * The check before publishing.
 * No model is involved: the browser looks for plaintext passwords and empty sections in the saved document, the server
 * reports links readers will not reach (`GET /pages/{id}/publication-check`). It never blocks publishing: the server
 * part has a budget, and on an error or after the budget publishing goes on with the local findings only.
 *
 * It depends on the pure rules (`src/assist/rules`) and nothing else of assist, so it runs with assist switched off
 * and without the editor module (§0): protecting passwords at publication is always on. A detected value never
 * leaves this module: findings carry ranges and a section title, never the text, and nothing here logs or sends it.
 */

/** How long publishing waits for the server part (plan §9). */
export const PUBLICATION_CHECK_BUDGET_MS = 1500;

interface JsonNode {
  type?: string;
  attrs?: Record<string, unknown>;
  content?: JsonNode[];
  text?: string;
  marks?: unknown[];
}

export interface LocalCheckOptions {
  /** The page the "Je to jen příklad" marks belong to. */
  pageId: string;
  examples?: SecretExamples;
}

function documentOf(contentJson: string): JsonNode | null {
  try {
    const value = JSON.parse(contentJson) as { document?: unknown };
    const document = value?.document as JsonNode | null | undefined;
    return document && typeof document === "object" && Array.isArray(document.content) ? document : null;
  } catch {
    return null;
  }
}

const isSecretNode = (node: JsonNode) => typeof node.type === "string" && node.type.startsWith("secret");
const isHeading = (node: JsonNode) => node.type === "heading";
const headingLevel = (node: JsonNode) => (typeof node.attrs?.level === "number" ? node.attrs.level : 1);
const headingId = (node: JsonNode) => (typeof node.attrs?.headingId === "string" && node.attrs.headingId ? node.attrs.headingId : null);

/**
 * The inline content of a textblock as one string with one character per ProseMirror position: text as is, a hard
 * break as a line break and any other inline node (a page link, a mention, a password reference) as a space, so the
 * detector neither joins words across it nor reads its attributes. `null` for a node that is not a textblock.
 */
function inlineText(node: JsonNode): string | null {
  const children = node.content;
  if (!children?.some(child => child.type === "text")) return null;
  return children.map(child => (child.type === "text" ? child.text ?? "" : child.type === "hardBreak" ? "\n" : " ")).join("");
}

/** Plain text of a node, for the section title. */
function plainText(node: JsonNode): string {
  if (node.type === "text") return node.text ?? "";
  return (node.content ?? []).map(plainText).join("");
}

/** Whether a top-level node is content of its section: anything but a heading or a blank paragraph. */
function isContent(node: JsonNode) {
  if (isHeading(node)) return false;
  if (node.type === "paragraph") return plainText(node).trim() !== "" || !!node.content?.some(child => child.type !== "text");
  return true;
}

function secretFindings(document: JsonNode, { pageId, examples = sessionSecretExamples }: LocalCheckOptions): PublicationCheck[] {
  const findings: PublicationCheck[] = [];
  let section: string | null = null;
  const visit = (node: JsonNode, path: number[]) => {
    if (isSecretNode(node)) return;
    if (path.length === 1 && isHeading(node)) section = plainText(node).trim() || null;
    const text = inlineText(node);
    if (text !== null) {
      for (const hit of detectSecrets(text)) {
        // The span's text is compared by hash only and dropped right here.
        if (examples.isExample(pageId, spanText(text, hit.span))) continue;
        const target: RevealTarget = { kind: "text", path, from: hit.span.start, to: hit.span.end };
        findings.push({
          id: `secret:${path.join(".")}:${hit.span.start}`,
          kind: "secret",
          severity: "warning",
          text: section ? t.pub.secretInSection(section) : t.pub.secret,
          target,
        });
      }
      return;
    }
    node.content?.forEach((child, index) => visit(child, [...path, index]));
  };
  document.content!.forEach((child, index) => visit(child, [index]));
  return findings;
}

/** A heading with nothing but blank paragraphs until the next heading of the same or a higher level, or the end. */
function emptySectionFindings(document: JsonNode): PublicationCheck[] {
  const blocks = document.content!;
  const findings: PublicationCheck[] = [];
  blocks.forEach((block, index) => {
    if (!isHeading(block)) return;
    const title = plainText(block).trim();
    if (!title) return;
    const level = headingLevel(block);
    let hasContent = false;
    for (let next = index + 1; next < blocks.length; next++) {
      const node = blocks[next]!;
      if (isHeading(node) && headingLevel(node) <= level) break;
      if (isContent(node)) { hasContent = true; break; }
    }
    if (hasContent) return;
    findings.push({
      id: `section:${index}`,
      kind: "section",
      severity: "info",
      text: t.pub.emptySection(title),
      target: { kind: "heading", id: headingId(block), path: [index] },
    });
  });
  return findings;
}

/** The browser's part of the check over a saved `contentJson`: passwords first, then empty sections. */
export function localFindings(contentJson: string, options: LocalCheckOptions): PublicationCheck[] {
  const document = documentOf(contentJson);
  if (!document) return [];
  try {
    return [...secretFindings(document, options), ...emptySectionFindings(document)];
  } catch {
    // A document the rules cannot read is not a reason to stop publishing.
    return [];
  }
}

/** A link of the saved revision that readers will not reach (S3 contract); "unavailable" never has a title. */
export interface LinkFinding { targetPageId: string; state: "unavailable" | "unpublished"; title: string | null }
interface PublicationCheckResponse { version: number; links: LinkFinding[] }

/** The server's link findings as rows of the panel. */
export function linkFindings(links: readonly LinkFinding[]): PublicationCheck[] {
  return links.map(linkFinding);
}

function linkFinding(link: LinkFinding): PublicationCheck {
  const target: RevealTarget = { kind: "page", pageId: link.targetPageId };
  const base = { id: `link:${link.targetPageId}`, kind: "link" as const, severity: "warning" as const, target };
  // "unavailable" never names the page: the author may not be allowed to know it (decision O7: both are warnings).
  if (link.state !== "unpublished") return { ...base, text: t.pub.linkUnavailable };
  return {
    ...base,
    text: link.title ? t.pub.linkUnpublishedNamed(link.title) : t.pub.linkUnpublished,
    openHref: buildShellHashEncoded({ view: "page", pageId: link.targetPageId }),
  };
}

/** The server's part: links of the saved revision at `version` that readers will not reach. */
export async function serverFindings(pageId: string, version: number, signal: AbortSignal): Promise<PublicationCheck[]> {
  const result = await api<PublicationCheckResponse>(
    `/pages/${encodeURIComponent(pageId)}/publication-check?expectedVersion=${encodeURIComponent(String(version))}`,
    { signal },
  );
  return linkFindings(result.links ?? []);
}

export interface SavedPage { id: string; version: number; contentJson: string }

export interface PublicationCheckSource {
  /** The page as last saved; the host saves before checking, so this is the revision Publish publishes. */
  page: () => SavedPage | null | undefined;
  /** Saves the page again after the server answered 409 (the saved version moved on); `false` = not saved. */
  save: () => Promise<boolean>;
  budgetMs?: number;
  examples?: SecretExamples;
  /** The server part; tests replace it. */
  fetchCheck?: (pageId: string, version: number, signal: AbortSignal) => Promise<PublicationCheck[]>;
}

class BudgetExceeded extends Error {}

/** Runs `request` until `deadline`, then aborts it; rejects with `BudgetExceeded` when it does not answer in time. */
function withinDeadline<T>(deadline: number, request: (signal: AbortSignal) => Promise<T>): Promise<T> {
  const remaining = deadline - Date.now();
  if (remaining <= 0) return Promise.reject(new BudgetExceeded());
  const controller = new AbortController();
  let timer: ReturnType<typeof setTimeout> | undefined;
  const expired = new Promise<never>((_, reject) => {
    timer = setTimeout(() => { controller.abort(); reject(new BudgetExceeded()); }, remaining);
  });
  return Promise.race([request(controller.signal), expired]).finally(() => clearTimeout(timer));
}

/**
 * The check for a host: call it after saving. It returns the local findings at once and the server's within the
 * budget, or `null` when there is no saved page to check. A 409 from the server means the saved version moved on:
 * the page is saved again and checked once more, within the same budget (the save itself is part of publishing, as
 * today, and is never cut short).
 */
export function createPublicationChecks(source: PublicationCheckSource): () => PublicationCheckRun | null {
  const budget = source.budgetMs ?? PUBLICATION_CHECK_BUDGET_MS;
  const fetchCheck = source.fetchCheck ?? serverFindings;
  return () => {
    const page = source.page();
    if (!page) return null;
    const local = localFindings(page.contentJson, { pageId: page.id, examples: source.examples });
    const deadline = Date.now() + budget;
    const server = (async (): Promise<readonly PublicationCheck[]> => {
      try {
        return await withinDeadline(deadline, signal => fetchCheck(page.id, page.version, signal));
      } catch (failure) {
        if (!(failure instanceof ApiError) || failure.status !== 409) return [];
      }
      try {
        if (!(await source.save())) return [];
        const saved = source.page();
        if (!saved || saved.id !== page.id) return [];
        return await withinDeadline(deadline, signal => fetchCheck(saved.id, saved.version, signal));
      } catch {
        return [];
      }
    })();
    return { local, server };
  };
}
