import shellMore from "./i18n/shell-more";
/**
 * Rights of a personal API token, as the wiki words them (Atlas 2.0 MCP track, T1; 3.6): the owner's level (the ceiling an
 * administrator sets), the scopes of a token (read, or read and write) and the channels it is accepted on (rest, mcp). 3.6 has no
 * drafts: a write is a live edit as a person makes it, and what readers see follows the page's publication setting. The server
 * decides; this module only names the values the same way everywhere (profile, people, token lists, audit).
 */
export type ApiLevel = "none" | "read" | "write";
export type ApiScope = "read" | "write";
export type ApiChannel = "rest" | "mcp";

export interface ApiLevelInfo {
  label: string;
  /** What a token (or an account's ceiling) of this level may do, in one sentence. */
  description: string;
  /** The wording of a token note: "Čte · Zapisuje". */
  verb: string;
  icon: string;
}

export const apiLevelOrder: ApiLevel[] = ["none", "read", "write"];
export const apiLevelInfo: Record<ApiLevel, ApiLevelInfo> = {
  none: { get label() { return shellMore.apilevel.none.label; }, get description() { return shellMore.apilevel.none.description; }, verb: "", icon: "lock" },
  read: { get label() { return shellMore.apilevel.read.label; }, get description() { return shellMore.apilevel.read.description; }, get verb() { return shellMore.apilevel.read.verb; }, icon: "book-open" },
  write: { get label() { return shellMore.apilevel.write.label; }, get description() { return shellMore.apilevel.write.description; }, get verb() { return shellMore.apilevel.write.verb; }, icon: "pencil" },
};
export const apiChannelInfo: Record<ApiChannel, { label: string; short: string; icon: string }> = {
  rest: { label: "REST API", short: "REST API", icon: "code-xml" },
  mcp: { label: "MCP", short: "MCP", icon: "plug" },
};

/** An unknown or missing level counts as no API: nothing is offered that the server did not name. */
export function apiLevel(value: string | null | undefined): ApiLevel {
  return apiLevelOrder.includes(value as ApiLevel) ? (value as ApiLevel) : "none";
}
export const levelRank = (level: ApiLevel) => apiLevelOrder.indexOf(level);

const scopeOrder = ["read", "write"] as const;
/** The scopes a token of this level holds: read ⊂ write. */
export function scopesOfLevel(level: ApiLevel): ApiScope[] {
  const rank = levelRank(level);
  return scopeOrder.filter((_, index) => index < rank);
}
/** The level a set of scopes amounts to: the highest. */
export function levelOfScopes(scopes: readonly string[]): ApiLevel {
  if (scopes.includes("write")) return "write";
  return scopes.includes("read") ? "read" : "none";
}
/** The options a person may pick for a new token: every level up to the ceiling, never the higher ones. */
export const levelsUpTo = (ceiling: ApiLevel): ApiLevel[] => apiLevelOrder.filter((level) => level !== "none" && levelRank(level) <= levelRank(ceiling));

/** Channels in the canonical order, only known ones; none named means the REST API (how tokens were issued before channels). */
export function apiChannels(values: readonly string[] | null | undefined): ApiChannel[] {
  const known = (["rest", "mcp"] as const).filter((channel) => values?.includes(channel));
  return known.length ? known : values ? [] : ["rest"];
}

/** "Čte · Zapisuje · REST API": what a token may do and where, the way the server words a token note. */
export function rightsText(scopes: readonly string[], channels: readonly string[] | null | undefined): string {
  const rank = levelRank(levelOfScopes(scopes));
  const verbs = scopeOrder.filter((_, index) => index < rank).map((scope) => apiLevelInfo[scope].verb);
  return [...verbs, ...apiChannels(channels).map((channel) => apiChannelInfo[channel].short)].join(" · ");
}
