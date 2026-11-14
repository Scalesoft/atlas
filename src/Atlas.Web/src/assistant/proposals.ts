import type { InjectionKey } from "vue";
import { api, ApiError, post } from "../api";
import t from "../i18n/assistant-panel";
import { findingsFor, type RequestFindings } from "../mcpPublish";

/**
 * A write the agent proposes (spec §6, §9.2). The server (WP-20) builds the preview from the current state of the pages; the client
 * only ever answers with the id and the indexes of the items the user kept, never with arguments.
 */
export type ProposalState = "pending" | "applying" | "applied" | "failed" | "declined" | "expired" | "superseded";
export type ProposalItemStatus = "applied" | "failed" | "skipped" | "rolled_back";
export interface ProposalLabels { icon: string | null; color: string | null; tags: string[] }
/** A draft write (WP-31): the changed sections' Markdown before and after (placeholders as „⟦komponenta: …⟧“); a rename the old
 *  and new title; a new draft its headings and first lines (`before` empty). */
export interface ProposalText { sections: string[]; before: string; after: string }
/** A move (WP-40): the path „Prostor › … › Stránka“ before and after, the subpages that move along (those the user sees) and
 *  whether the page leaves its space (it then takes the target space's default access). */
export interface ProposalMove { before: string; after: string; subpages: number; otherSpace: boolean }
/** A publication (WP-41, spec §6.3): what the MCP confirmation page shows, built by the server: the version to publish, the
 *  published one (null = first publication), the changed sections, the change summary and the server's link check. */
export interface ProposalPublish { version: number; publishedVersion: number | null; changedSections: number; changeSummary: string | null; findings: RequestFindings }
/** `labels` carries `before`/`after`; `content`, `rename` and `draft` carry `text`; `move` carries `move`, `publish` carries `publish`.
 *  A new draft's `pageId` is empty until it exists. */
export type ProposalKind = "labels" | "content" | "rename" | "draft" | "move" | "publish";
export interface ProposalItem {
  index: number; pageId: string; title: string; path: string; kind: ProposalKind;
  before: ProposalLabels | null; after: ProposalLabels | null; status: ProposalItemStatus | null; code?: string | null; text?: ProposalText | null;
  move?: ProposalMove | null; publish?: ProposalPublish | null;
}
export interface Proposal { id: string; messageId: string; tool: string; state: ProposalState; expiresAt: string; items: ProposalItem[] }

export const getProposal = (id: string, signal?: AbortSignal) => api<Proposal>(`/ai/proposals/${encodeURIComponent(id)}`, { signal });
/** `items` are the indexes to apply; a decline sends none. */
export const decideProposal = (id: string, approve: boolean, items: number[]) =>
  post<Proposal>(`/ai/proposals/${encodeURIComponent(id)}/decision`, { approve, items });

/** The state a 409 from the decision endpoint means; `proposal_decided` has none here (the card reloads the real one). */
export const CONFLICT_STATE: Record<string, ProposalState | undefined> = { proposal_expired: "expired", proposal_superseded: "superseded" };


/** Why a decision could not be sent. Nothing was changed in any of these cases. */
export function decisionErrorText(error: unknown) {
  if (error instanceof ApiError) {
    if (error.status === 409 && error.code && t.proposal.conflict[error.code]) return t.proposal.conflict[error.code]!;
    if (error.status === 403) return t.proposal.forbidden;
    if (error.status === 400) return t.proposal.invalid;
    if (error.status === 404) return t.proposal.gone;
  }
  return t.proposal.sendFailed;
}

/** The reason of one row that failed (`code` of the item). */
export function itemFailureText(code?: string | null) {
  return (code && t.proposal.failure[code]) || t.proposal.failureDefault;
}

/** „Provedeno 12 změn, 1 se nepodařila“ */
export function summaryText(items: readonly ProposalItem[]) {
  const done = items.filter((item) => item.status === "applied").length;
  const failed = items.filter((item) => item.status === "failed").length;
  return !done && failed ? t.proposal.nothingChanged : t.proposal.summary(done, failed);
}

/** Injected by the gallery and tests to answer a decision without a server; the card falls back to the real endpoint. */
export const PROPOSAL_DECIDE: InjectionKey<typeof decideProposal> = Symbol("assistant-proposal-decide");
/** Injected by the gallery and tests for the check before a publication; the card falls back to the MCP confirmation page's. */
export const PROPOSAL_FINDINGS: InjectionKey<typeof findingsFor> = Symbol("assistant-proposal-findings");
