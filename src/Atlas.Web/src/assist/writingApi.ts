import { api } from '../api';
import type { ModelIntentRequest } from './modelIntents';
export const transformActions = ['proofread','shorten','simplify','formal','informal','translate-en','translate-cs','bullets','steps','table','custom'] as const;
export type TransformAction = typeof transformActions[number];
export interface WritingProposal { readonly text: string; readonly provenance: { readonly location: string; readonly provider: string; readonly model: string } }
function request<T>(path: string, value: unknown, signal: AbortSignal) { return api<T>(`/assist/${path}`, { method: 'POST', body: JSON.stringify(value), signal }); }
export const writingApi = {
  intents: (value: ModelIntentRequest, signal: AbortSignal) => request<unknown>('intents', value, signal),
  transform: (value: { pageId: string; text: string; action: TransformAction; instruction?: string }, signal: AbortSignal) => request<WritingProposal>('transform', value, signal),
  outline: (value: { pageId: string; topic: string }, signal: AbortSignal) => request<WritingProposal>('outline', value, signal),
};
export type AtlasWritingApi = typeof writingApi;
