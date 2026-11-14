import { describe, expect, it } from "vitest";
import { apiChannels, apiLevel, levelOfScopes, levelsUpTo, rightsText, scopesOfLevel } from "./apiTokenRights";
import { auditActivity, type AccessAuditEvent } from "./components/settings/auditActivity";
import { settingsIssues } from "./components/settings/global/issues";

describe("API token rights wording", () => {
  it("nests the scopes read ⊂ write and offers only levels up to the ceiling", () => {
    expect(scopesOfLevel("read")).toEqual(["read"]);
    expect(scopesOfLevel("write")).toEqual(["read", "write"]);
    expect(levelsUpTo("none")).toEqual([]);
    expect(levelsUpTo("read")).toEqual(["read"]);
    expect(levelsUpTo("write")).toEqual(["read", "write"]);
    // 3.6: the former levels are gone; an unknown one offers nothing.
    for (const former of ["draft", "publish", "direct"]) expect(apiLevel(former)).toBe("none");
  });

  it("names a token by its highest scope and words it like the server's note", () => {
    expect(levelOfScopes(["read"])).toBe("read");
    expect(levelOfScopes(["read", "write"])).toBe("write");
    expect(levelOfScopes([])).toBe("none");
    expect(rightsText(["read", "write"], ["rest"])).toBe("Čte · Zapisuje · REST API");
    expect(rightsText(["read"], ["rest", "mcp"])).toBe("Čte · REST API · MCP");
    expect(apiChannels(undefined)).toEqual(["rest"]);
    expect(apiChannels(["mcp", "unknown"])).toEqual(["mcp"]);
  });
});

describe("audit and overview of the API level", () => {
  const event: AccessAuditEvent = {
    id: "9", occurredAt: "2026-10-05T10:00:00Z", actorId: "a", actorName: "Admin", action: "api_permission.changed", userId: "u", userName: "Jana", groupId: null, groupName: null,
    spaceId: null, spaceName: null, role: null, isActive: null, isWikiAdmin: null, canIssueTokens: true, apiLevel: "write",
  };
  it("words api_permission.changed with the level instead of the raw action", () => {
    const item = auditActivity(event);
    expect(item.action).toBe("změnil(a) úroveň osobního API");
    expect(item.context).toContain("úroveň API Zápis");
    expect(item.details).toContainEqual({ label: "Úroveň osobního API", value: "Zápis" });
    expect(JSON.stringify(item)).not.toContain("api_permission");
  });
  it("lists api_levels_review among the things to resolve only while the review waits", () => {
    const facts = { pendingRequests: 0, freshAuth: null, outsideWithoutNetworks: false };
    expect(settingsIssues([], [{ id: "l" } as never], null, true, { ...facts, levelsReview: true }).map((issue) => issue.id)).toEqual(["api_levels_review"]);
    expect(settingsIssues([], [{ id: "l" } as never], null, true, facts)).toEqual([]);
  });
});
