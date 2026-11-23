import { api, post, put } from "../api";
import type { ContactData } from "./contactData";

/**
 * Mapa znalostí (docs/skills-design.md): what a person knows, the rating, who knows a topic and the topic catalog (page tags with aliases).
 * The server never sends anonymous ratings one by one: `score` is the shared self-assessment with the colleagues' average from three on.
 */
export type SkillVisibility = "private" | "shared" | "signed";
export interface SkillRater { name: string; level: number; visibility: SkillVisibility; note?: string | null }
/** One topic of a person; `score` null when the reader may see no number (`fewPeers`: colleagues rated it, too few for an average that keeps the anonymous ones anonymous). */
export interface ContactSkill {
  tagId: string; name: string; score: number | null; self?: number | null; selfNote?: string | null; peers: number; fewPeers: boolean;
  mine?: number | null; myVisibility?: SkillVisibility | null; myNote?: string | null; raters: SkillRater[]; evidence: number;
  /** The broader topic (part of), for grouping. */
  group?: string | null;
}
export interface TopicRef { id: string; name: string; people: number; matched?: string | null }
/** `moderate`: the reader may remove a topic from the person for everyone (the person or a wiki admin). */
export interface ContactSkills { contactId: string; name: string; self: boolean; skills: ContactSkill[]; suggested: TopicRef[]; moderate?: boolean }
export interface TopicView { id: string; name: string; aliases: string[]; parents: TopicRef[]; children: TopicRef[]; related?: TopicRef[] | null }
export type TopicLinkKind = "part_of" | "related";
/** Who knows what as a graph: people and topics, person–topic links with the level the reader may see, topic links part_of and related. */
export interface SkillNode { id: string; kind: "person" | "topic"; name: string; title?: string | null; organization?: string | null; people: number }
export interface SkillLink { source: string; target: string; kind: "skill" | TopicLinkKind; level?: number | null }
export interface SkillGraph { nodes: SkillNode[]; links: SkillLink[]; truncated: boolean }
export interface TaggedPage { id: string; title: string; spaceName: string }
export interface Expert { contactId: string; contact: ContactData; skill?: ContactSkill | null; via?: string | null; inContext: boolean; evidence: number; pages: { pageId: string; title: string; spaceName: string }[] }
export interface ExpertList { topic?: TopicView | null; similar: TopicRef[]; people: Expert[]; selfContactId?: string | null }
/** `everyone` with level null: the topic goes from the person for everyone (`moderate` only). */
export interface RateSkillRequest { topic: string; level: number | null; visibility?: SkillVisibility; note?: string | null; everyone?: boolean }

const id = (value: string) => encodeURIComponent(value);

export const skillApi = {
  contact: (contactId: string, signal?: AbortSignal) => api<ContactSkills>(`/contacts/${id(contactId)}/skills`, { signal }),
  rate: (contactId: string, request: RateSkillRequest) => put<ContactSkills>(`/contacts/${id(contactId)}/skills`, request),
  experts: (topic: string, signal?: AbortSignal) => api<ExpertList>(`/skills/experts?topic=${id(topic)}&limit=50`, { signal }),
  topics: (q: string, signal?: AbortSignal) => api<{ items: TopicRef[] }>(`/skills/topics?q=${id(q)}`, { signal }),
  merge: (tagId: string, intoId: string) => post<TopicView>(`/skills/topics/${id(tagId)}/merge`, { intoId }),
  link: (tagId: string, otherId: string, kind: TopicLinkKind, remove = false) => put<TopicView>(`/skills/topics/${id(tagId)}/parents`, { parentId: otherId, kind, remove }),
  graph: (spaceId?: string | null, signal?: AbortSignal) => api<SkillGraph>(`/skills/graph${spaceId ? `?spaceId=${id(spaceId)}` : ""}`, { signal }),
  /** The readable pages carrying the topic's tag (the page tags' own listing). */
  pages: async (tagId: string, signal?: AbortSignal) => (await api<{ items: TaggedPage[] }>(`/tags/${id(tagId)}/pages?limit=5`, { signal })).items,
};
export type SkillApi = typeof skillApi;

/** The level a reader sees for a topic: the summary's score, else their own rating. */
export const shownLevel = (skill: ContactSkill) => skill.score ?? skill.mine ?? null;
