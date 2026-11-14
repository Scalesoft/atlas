/**
 * What a publication sends besides the version: the optional change summary.
 * The server accepts one line of at
 * most 500 characters (`Rules.OptionalLine`) and answers 400 otherwise; the client enforces the same
 * rule so the field can say so before anything is sent.
 */

import t from "../i18n/shell";
import type { AiProvenanceView } from "../ai/assistantApi";
import type { RevealTarget } from "../editor/revealTarget";

export const CHANGE_SUMMARY_MAX = 500;
/** The sentence for a summary that breaks the rule, read where it is shown. */
export const changeSummaryError = () => t.pub.summaryTooLong(CHANGE_SUMMARY_MAX);

/** The server's `char.IsControl`: C0 (line breaks, tabs, NUL …), DEL and C1. */
const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f-\u009f]/;

export interface ChangeSummaryResult {
  /** The text to send: trimmed, `null` when blank or invalid. */
  value: string | null;
  error?: string;
}

/** What a finding is about; the panel lists them in this fixed order (plan §9: passwords, links, empty sections). */
export type PublicationCheckKind = "secret" | "link" | "section" | "spelling";

/** A finding the publish panel lists before publishing (filled by the publication check, package E). */
export interface PublicationCheck {
  id: string;
  /** `warning` turns the panel's confirmation into "Publikovat i tak"; `info` only informs. */
  severity: "warning" | "info";
  text: string;
  kind?: PublicationCheckKind;
  /** Where "Zobrazit" leads in the editor; a finding without one has no Zobrazit. */
  target?: RevealTarget;
  /** "Otevřít": the page the finding is about (the target of an unpublished link), opened beside the editor. */
  openHref?: string;
  /** Present only for a completed model finding; ordinary checks keep their type icon. */
  provenance?: AiProvenanceView;
}

const kindOrder: Record<PublicationCheckKind, number> = { secret: 0, link: 1, section: 2, spelling: 3 };

/** Findings in the panel's order: warnings first, then passwords, links and empty sections, then as given. */
export function orderPublicationChecks(checks: readonly PublicationCheck[]): PublicationCheck[] {
  const rank = (check: PublicationCheck) => (check.kind ? kindOrder[check.kind] : 3);
  return checks
    .map((check, index) => ({ check, index }))
    .sort((a, b) => (a.check.severity === b.check.severity ? 0 : a.check.severity === "warning" ? -1 : 1)
      || rank(a.check) - rank(b.check) || a.index - b.index)
    .map(({ check }) => check);
}

/**
 * One run of the publication check: what the browser found at once, and what the server adds within its budget.
 * `server` never rejects; on an error or after the budget it resolves with no findings.
 */
export interface PublicationCheckRun {
  readonly local: readonly PublicationCheck[];
  readonly server: Promise<readonly PublicationCheck[]>;
}

export function normalizeChangeSummary(input: string | null | undefined): ChangeSummaryResult {
  const value = (input ?? "").trim();
  if (value === "") return { value: null };
  if (value.length > CHANGE_SUMMARY_MAX || CONTROL_CHARACTERS.test(value)) return { value: null, error: changeSummaryError() };
  return { value };
}

export interface PublishRequestBody {
  expectedVersion: number;
  changeSummary?: string;
  refuseUnsavedLiveEdits?: true;
}

/**
 * `POST /pages/{id}/publish`: without a summary the body is exactly what it always was. `savedOnly` (publishing outside editing,
 * nothing just saved): the server refuses while unsaved live edits exist, which publishing the saved revision would leave out.
 */
export function publishRequest(expectedVersion: number, summary: string | null, savedOnly = false): PublishRequestBody {
  return { expectedVersion, ...(summary === null ? {} : { changeSummary: summary }), ...(savedOnly ? { refuseUnsavedLiveEdits: true as const } : {}) };
}

export interface ApproveRequestBody extends PublishRequestBody {
  expectedRevisionId: string;
}

/** `POST /spaces/{id}/drafts/{id}/approve`. */
export function approveRequest(expectedVersion: number, expectedRevisionId: string, summary: string | null): ApproveRequestBody {
  return summary === null ? { expectedVersion, expectedRevisionId } : { expectedVersion, expectedRevisionId, changeSummary: summary };
}
