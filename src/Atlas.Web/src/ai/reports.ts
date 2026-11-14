import { api } from "../api";
import t from "../i18n/ai-answer";
export interface UsageRow { task: string; kind: string; operation: string; calls: number; errors: number; inputTokens: number | null; outputTokens: number | null; inputKnownCalls: number; outputKnownCalls: number; p50Ms: number; p95Ms: number; estimatedCost: number | null; costKnownCalls: number }
export interface UsageProviderRow { provider: string; location: string; model: string; calls: number; errors: number; canceled: number; inputTokens: number | null; outputTokens: number | null; inputKnownCalls: number; outputKnownCalls: number; costKnownCalls: number; estimatedCost: number | null; lastCallAt: string }
export interface UsageDayRow { day: string; calls: number; errors: number; canceled: number; inputTokens: number | null; outputTokens: number | null; inputKnownCalls: number; outputKnownCalls: number; costKnownCalls: number; estimatedCost: number | null }
export interface UsageReport extends Report<UsageRow> { providers: UsageProviderRow[]; days: UsageDayRow[] }
export interface QualityRow { task: string; promptVersion: string | null; rating: string; reason: string | null; count: number }
export interface Report<T> { since: string; until: string; items: T[] }
export interface AiReportsClient { usage(signal?: AbortSignal): Promise<UsageReport>; quality(signal?: AbortSignal): Promise<QualityReport> }
export const aiReportsClient: AiReportsClient = {
  usage: signal => api("/admin/ai/usage", { signal }),
  quality: signal => api("/admin/ai/quality", { signal }),
};
export interface DuplicatePair { pageId: string; title: string; otherPageId: string; otherTitle: string; similarity: number }
export interface DuplicateClient { list(spaceId: string, signal?: AbortSignal): Promise<{ items: DuplicatePair[] }>; dismiss(spaceId: string, pageId: string, otherPageId: string): Promise<void> }
export const duplicateClient: DuplicateClient = {
  list: (spaceId, signal) => api(`/spaces/${encodeURIComponent(spaceId)}/duplicates`, { signal }),
  dismiss: (spaceId, pageId, otherPageId) => api(`/spaces/${encodeURIComponent(spaceId)}/duplicates/dismiss`, { method: "POST", body: JSON.stringify({ pageId, otherPageId }) }),
};
export const aiTaskLabel = (task: string) => t.reports.tasks[task] ?? task;
export const feedbackReason = (reason: string | null) => t.feedback.reasons[reason ?? ""] ?? t.reports.noReason;

export interface AiEvaluationReport { setVersion: string; promptVersion: string; items: { id: string; question: string; expectedAnswerable: boolean; answerable: boolean | null; passed: boolean; invalidCitations: number; code: string | null; latencyMs: number }[] }
export interface AiInteractionRow { task: string; kind: string; outcome: string; count: number }
export interface QualityReport extends Report<QualityRow> { interactions?: AiInteractionRow[]; noSources?: { spaceId: string | null; spaceName: string; count: number }[] }
export const interactionOutcome = (outcome: string) => t.reports.outcomes[outcome] ?? outcome;
export const interactionKindLabel = (kind: string) => t.reports.kinds[kind] ?? kind;