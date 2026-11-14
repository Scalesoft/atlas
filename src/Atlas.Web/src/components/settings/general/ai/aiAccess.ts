import type { AiQuotaOverrides } from "../../../../administration";
import t from "../../../../i18n/settings-ai";
export const quotaFields = [
  { key: "searchAnswerDailyLimit", task: "search.answer", get label() { return t.quota.fields.searchAnswer; } },
  { key: "chatAnswerDailyLimit", task: "chat.answer", get label() { return t.quota.fields.chat; } },
  { key: "pageSummaryDailyLimit", task: "page.summary", get label() { return t.quota.fields.pageSummary; } },
  { key: "agentDailyLimit", task: "assistant.agent", get label() { return t.quota.fields.agent; } },
] as const;
export const emptyQuotas = (): AiQuotaOverrides => ({ searchAnswerDailyLimit: null, chatAnswerDailyLimit: null, pageSummaryDailyLimit: null, agentDailyLimit: null });
export const validQuotas = (draft: AiQuotaOverrides) => quotaFields.every(({ key }) => draft[key] === null || (Number.isInteger(draft[key]) && draft[key]! >= 0 && draft[key]! <= 10_000));
export function quotaSummary(draft: AiQuotaOverrides): string {
  if (!validQuotas(draft)) return t.quota.invalidSummary;
  const custom = quotaFields.filter(({ key }) => draft[key] !== null);
  return custom.length ? custom.map(({ key, label }) => `${label}: ${draft[key] === 0 ? t.quota.noLimit : t.quota.perDay(draft[key]!)}`).join(" · ") : t.quota.defaultLimits;
}
