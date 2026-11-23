import { api, post, put } from "../api";
import type { ContactData } from "./contactData";

/**
 * Kdo co umí (docs/skills-design.md): what a person knows, the rating, who knows a topic and the topic catalog (page tags with aliases).
 * The server never sends anonymous ratings one by one: `score` is the shared self-assessment with the colleagues' average from three on.
 */
export type SkillVisibility = "private" | "shared" | "signed";
export interface SkillRater { name: string; level: number; visibility: SkillVisibility; note?: string | null }
/** One topic of a person; `score` null when the reader may see no number (`fewPeers`: one or two colleagues rated it). */
export interface ContactSkill {
  tagId: string; name: string; score: number | null; self?: number | null; selfNote?: string | null; peers: number; fewPeers: boolean;
  mine?: number | null; myVisibility?: SkillVisibility | null; myNote?: string | null; raters: SkillRater[]; evidence: number;
}
export interface TopicRef { id: string; name: string; people: number; matched?: string | null }
export interface ContactSkills { contactId: string; name: string; self: boolean; skills: ContactSkill[]; suggested: TopicRef[] }
export interface TopicView { id: string; name: string; aliases: string[]; parents: TopicRef[]; children: TopicRef[] }
export interface Expert { contactId: string; contact: ContactData; skill?: ContactSkill | null; via?: string | null; inContext: boolean; evidence: number; pages: { pageId: string; title: string; spaceName: string }[] }
export interface ExpertList { topic?: TopicView | null; similar: TopicRef[]; people: Expert[]; selfContactId?: string | null }
export interface RateSkillRequest { topic: string; level: number | null; visibility?: SkillVisibility; note?: string | null }

const id = (value: string) => encodeURIComponent(value);

export const skillApi = {
  contact: (contactId: string, signal?: AbortSignal) => api<ContactSkills>(`/contacts/${id(contactId)}/skills`, { signal }),
  rate: (contactId: string, request: RateSkillRequest) => put<ContactSkills>(`/contacts/${id(contactId)}/skills`, request),
  experts: (topic: string, signal?: AbortSignal) => api<ExpertList>(`/skills/experts?topic=${id(topic)}&limit=8`, { signal }),
  topics: (q: string, signal?: AbortSignal) => api<{ items: TopicRef[] }>(`/skills/topics?q=${id(q)}`, { signal }),
  merge: (tagId: string, intoId: string) => post<TopicView>(`/skills/topics/${id(tagId)}/merge`, { intoId }),
};
export type SkillApi = typeof skillApi;

/** The level a reader sees for a topic: the summary's score, else their own rating. */
export const shownLevel = (skill: ContactSkill) => skill.score ?? skill.mine ?? null;
