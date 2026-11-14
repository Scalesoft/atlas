import { inject, type InjectionKey } from "vue";
import { api } from "../api";
import type { AiPersonalUsage } from "../administration";
export type AiUsageLoader = (signal: AbortSignal) => Promise<AiPersonalUsage>;
export const aiUsageLoaderKey: InjectionKey<AiUsageLoader> = Symbol("ai-usage-loader");
export const useAiUsageLoader = () => inject(aiUsageLoaderKey, (signal: AbortSignal) => api<AiPersonalUsage>("/profile/ai", { signal }));
const listeners = new Set<() => void>();
export function onAiUsageChanged(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; }
export function notifyAiUsageChanged() { for (const listener of listeners) listener(); }
