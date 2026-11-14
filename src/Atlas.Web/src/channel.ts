import shellMore from "./i18n/shell-more";
/** The channel a change, publication or opening came through (ContentChannel on the server); null on rows from before it was recorded. */
export type ContentChannel = "web" | "api" | "mcp" | "assistant" | "import" | "system";
/** The personal API token behind it (ApiTokenRef): only its owner and wiki administrators receive it. Revoked: the name stays. */
export interface ApiTokenRef { id: string; name: string; revoked: boolean }

const marks: Record<string, { label: string; icon: string }> = {
  mcp: { get label() { return shellMore.channel.mcp; }, icon: "plug" },
  api: { get label() { return shellMore.channel.api; }, icon: "code-xml" },
  // The assistant acts as its user, without a token; a conversation icon, never the AI glyph (aiGlyphBoundary.guard.test.ts).
  assistant: { get label() { return shellMore.channel.assistant; }, icon: "user-check" },
  import: { label: "import", icon: "download" },
  system: { get label() { return shellMore.channel.system; }, icon: "settings" },
};

/**
 * What to say about a channel: nothing for null (a legacy row), web and unknown values. `import` and `system` are
 * shown only where `detailed` (a revision preview), so a list row stays quiet about them.
 */
export function channelMark(channel: string | null | undefined, detailed = false): { label: string; icon: string } | null {
  if (!channel || !(channel in marks)) return null;
  return (channel === "import" || channel === "system") && !detailed ? null : marks[channel];
}

/** „token ‚Claude Code – notebook‘“, with „(odvolaný)“ for a revoked one. */
export function tokenText(token: ApiTokenRef | null | undefined): string | null {
  return token ? shellMore.channel.token(token.name, token.revoked) : null;
}

/** The plain-text line for the channel: „přes MCP · token ‚…‘“; empty when there is nothing to say. */
export function channelText(channel: string | null | undefined, token?: ApiTokenRef | null, detailed = false): string {
  const mark = channelMark(channel, detailed);
  return mark ? [mark.label, detailed ? tokenText(token) : null].filter(Boolean).join(" · ") : "";
}

/** The channels the client-side author filter also matches: typing "MCP", "API" or "asistent" finds those changes. */
export function channelSearchText(channel: string | null | undefined): string {
  return channel === "mcp" ? "mcp" : channel === "api" ? "api" : channel === "assistant" ? "asistent" : "";
}
