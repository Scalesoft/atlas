import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import ChannelBadge from "./components/ChannelBadge.vue";
import { channelMark, channelSearchText, channelText, tokenText } from "./channel";
import { auditActivity, auditCategoryLabels, type AccessAuditEvent } from "./components/settings/auditActivity";

const token = { id: "t", name: "Claude Code – notebook", revoked: false };

describe("channel mark", () => {
  it("names MCP and API with a plug and code icon, and nothing for web, legacy or unknown", () => {
    expect(channelMark("mcp")).toEqual({ label: "přes MCP", icon: "plug" });
    expect(channelMark("api")).toEqual({ label: "přes API", icon: "code-xml" });
    expect(channelMark("assistant")).toEqual({ label: "přes asistenta", icon: "user-check" });
    expect(channelText("assistant", null, true)).toBe("přes asistenta");
    expect(channelSearchText("assistant")).toBe("asistent");
    for (const quiet of ["web", null, undefined, "legacy", "other"]) expect(channelMark(quiet, true)).toBeNull();
  });
  it("shows import and system only in detail", () => {
    expect(channelMark("import")).toBeNull();
    expect(channelMark("system")).toBeNull();
    expect(channelMark("import", true)?.label).toBe("import");
  });
  it("marks a revoked token and names it only when present", () => {
    expect(tokenText(token)).toBe("token ‚Claude Code – notebook‘");
    expect(tokenText({ ...token, revoked: true })).toBe("token ‚Claude Code – notebook‘ (odvolaný)");
    expect(tokenText(null)).toBeNull();
    expect(channelText("mcp", token, true)).toBe("přes MCP · token ‚Claude Code – notebook‘");
    expect(channelText("mcp", null, true)).toBe("přes MCP");
    expect(channelText("web", token, true)).toBe("");
  });
});

describe("ChannelBadge", () => {
  it("renders a neutral badge per channel without the token in a list row", () => {
    const mcp = mount(ChannelBadge, { props: { channel: "mcp", token } });
    expect(mcp.text()).toBe("přes MCP");
    expect(mcp.get("[data-tone]").attributes("data-tone")).toBe("neutral");
    expect(mount(ChannelBadge, { props: { channel: "api" } }).text()).toBe("přes API");
    expect(mount(ChannelBadge, { props: { channel: "assistant", detailed: true } }).text()).toBe("přes asistenta");
  });
  it("adds the token name in detail, with the revoked marker", () => {
    expect(mount(ChannelBadge, { props: { channel: "mcp", token, detailed: true } }).text()).toContain("token ‚Claude Code – notebook‘");
    expect(mount(ChannelBadge, { props: { channel: "mcp", token: { ...token, revoked: true }, detailed: true } }).text()).toContain("(odvolaný)");
    expect(mount(ChannelBadge, { props: { channel: "mcp", detailed: true } }).text()).toBe("přes MCP");
  });
  it("names the client by its token alone in a history row, as the AI caret does", () => {
    expect(mount(ChannelBadge, { props: { channel: "mcp", token, client: true } }).text()).toBe("přes MCPClaude Code – notebook");
    expect(mount(ChannelBadge, { props: { channel: "assistant", client: true } }).text()).toBe("přes asistenta");
  });
  it("renders nothing for web and legacy rows", () => {
    for (const channel of ["web", null, undefined]) expect(mount(ChannelBadge, { props: { channel, token } }).find("[data-channel-badge]").exists()).toBe(false);
  });
});

describe("audit texts of the API and MCP track", () => {
  const event = (over: Partial<AccessAuditEvent>): AccessAuditEvent => ({ id: "1", occurredAt: "2026-10-05T10:00:00Z", actorId: "u", actorName: "Jan", action: "api_permission.changed", userId: null, userName: "Jana", groupId: null, groupName: null, spaceId: null, spaceName: null, role: null, isActive: null, isWikiAdmin: null, canIssueTokens: null, ...over });
  it("reads a level change with its level", () => {
    const item = auditActivity(event({ apiLevel: "write" }));
    expect(item.action).toBe("změnil(a) úroveň osobního API");
    expect(item.details).toContainEqual({ label: "Úroveň osobního API", value: "Zápis" });
  });
  it("reads a refused MCP write with outcome and token name", () => {
    const item = auditActivity(event({ action: "mcp.write", apiChannel: "mcp", apiOutcome: "live_edits", apiTokenName: "Claude Code – notebook", pageTitle: "Příručka" }));
    // Without an operation the action stays the event's own; the page is the target, the token the context.
    expect([item.action, item.target, item.via]).toEqual(["upravil(a) koncept přes MCP", "Příručka", undefined]);
    expect([item.outcome, item.badge?.label, item.category]).toEqual(["failed", "Stránku někdo upravuje", "api"]);
    expect(item.details).toContainEqual({ label: "Kód výsledku", value: "live_edits" });
    expect(item.context).toBe("token ‚Claude Code – notebook‘");
  });
  it("reads every permanent delete with what it was and where", () => {
    const page = auditActivity(event({ action: "page.purged", objectTypeId: "atlas.page", objectTitle: "Stará příručka", spaceName: "Provoz", spaceId: "s" }));
    expect([page.action, page.target, page.context, page.category]).toEqual(["trvale smazal(a) stránku", "Stará příručka", "prostor Provoz", "trash"]);
    const space = auditActivity(event({ action: "space.purged", objectTypeId: "atlas.space", objectTitle: "Provoz", spaceName: "Provoz", spaceId: "s" }));
    expect([space.action, space.target, space.context]).toEqual(["trvale smazal(a) prostor", "Provoz", undefined]);
    expect(auditActivity(event({ action: "object.purged", objectTypeId: "atlas.board", objectTitle: "Plán" })).action).toBe("trvale smazal(a) tabuli");
    expect(auditActivity(event({ action: "object.purged", objectTypeId: "atlas.contact", objectTitle: "Jana" })).action).toBe("trvale smazal(a) kontakt");
    // A private note keeps no title in the audit.
    const note = auditActivity(event({ action: "object.purged", objectTypeId: "atlas.note", objectTitle: null }));
    expect([note.action, note.target]).toEqual(["trvale smazal(a) poznámku", undefined]);
    expect(auditActivity(event({ action: "object.purged", objectTypeId: "acme.thing" })).action).toBe("trvale smazal(a) objekt");
    expect(auditCategoryLabels.trash?.label).toBe("Trvalé smazání");
  });
  it("names where a backup now goes and who took a complete archive out (SEC3-4)", () => {
    const moved = auditActivity(event({ action: "backup.destination_changed", backupKind: "git.remote", backupDestination: "git@git.example.com:atlas/backup.git" }));
    expect([moved.action, moved.target, moved.category]).toEqual(["změnil(a) repozitář zálohy Git", "git@git.example.com:atlas/backup.git", "backup"]);
    expect(auditActivity(event({ action: "backup.destination_changed", backupKind: "archive.complete", backupDestination: "/var/backups" })).action).toBe("přepnul(a) archivy ZIP na úplnou zálohu");
    const taken = auditActivity(event({ action: "backup.downloaded", backupKind: "archive.download", backupDestination: "wiki-complete.zip" }));
    expect([taken.action, taken.target]).toEqual(["stáhl(a) úplnou zálohu", "wiki-complete.zip"]);
    // SEC-T-3, SEC-A-1: a new backup key and the OIDC providers are security changes of their own.
    expect(auditActivity(event({ action: "backup.destination_changed", backupKind: "backup.key", backupDestination: "k1" })).action).toBe("vydal(a) nový klíč zálohy");
    const signIn = auditActivity(event({ action: "oidc.configuration_changed" }));
    expect([signIn.action, signIn.category, auditCategoryLabels[signIn.category!]?.label]).toEqual(["změnil(a) přihlášení přes OIDC", "signin", "Přihlášení"]);
  });
  it("labels the module switches", () => {
    expect(auditActivity(event({ action: "mcp.enabled" })).action).toBe("zapnul(a) MCP server");
    expect(auditActivity(event({ action: "mcp.disabled" })).action).toBe("vypnul(a) MCP server");
    expect(auditActivity(event({ action: "mcp.publish" })).action).toBe("publikoval(a) přes MCP");
    expect(auditActivity(event({ action: "api.write" })).action).toBe("změnil(a) stránku přes API");
  });
  it("reads an assistant write as its user, without a token", () => {
    const item = auditActivity(event({ action: "assistant.write", apiChannel: "assistant", apiOperation: "atlas_set_tags", apiOutcome: "ok", pageId: "p1", pageTitle: "Příručka" }));
    expect([item.action, item.target, item.via, item.href]).toEqual(["změnil(a) štítky stránky", "Příručka", "přes asistenta", "#/pages/p1"]);
    expect([item.outcome, item.badge]).toEqual(["changed", undefined]);
    expect(item.details).toContainEqual({ label: "Kanál", value: "asistent" });
    expect(item.details?.some((detail) => detail.label === "Token")).toBe(false);
    expect(auditActivity(event({ action: "assistant.publish" })).action).toBe("publikoval(a) přes asistenta");
  });
  it("never reads an assistant proposal or a failed tool call as a refusal", () => {
    const write = (apiOperation: string, apiOutcome: string) => auditActivity(event({ action: "assistant.write", apiChannel: "assistant", apiOperation, apiOutcome }));
    const proposal = write("assistant.proposal", "pending");
    expect([proposal.action, proposal.via, proposal.outcome, proposal.badge?.label]).toEqual(["dostal(a) od asistenta návrh změny", undefined, "changed", "Čeká na potvrzení"]);
    expect([write("assistant.decision", "applied").action, write("assistant.decision", "applied").badge]).toEqual(["potvrdil(a) návrh asistenta", undefined]);
    expect([write("assistant.decision", "declined").action, write("assistant.decision", "declined").badge?.label]).toEqual(["odmítl(a) návrh asistenta", "Odmítnuto uživatelem"]);
    const conflict = write("atlas_update_section", "version_conflict");
    expect([conflict.outcome, conflict.badge?.label, conflict.details?.[0]]).toEqual(["failed", "Konflikt verzí", { label: "Co se stalo", value: "Stránku mezitím někdo změnil; nic se nepřepsalo." }]);
    expect([write("atlas_update_section", "heading_not_found").badge?.label, write("atlas_update_section", "invalid_argument").badge?.label]).toEqual(["Nenalezeno", "Neplatný vstup"]);
    expect([write("atlas_update_section", "scope_missing").outcome, write("atlas_update_section", "made_up").badge?.label]).toEqual(["denied", "Selhalo"]);
    const mcp = auditActivity(event({ action: "mcp.write", apiChannel: "mcp", apiOperation: "projector_update_requirement", apiOutcome: "ok", apiTokenName: "Claude" }));
    expect([mcp.action, mcp.via, mcp.target, mcp.context]).toEqual(["upravil(a) požadavky Projectoru", "přes MCP", undefined, "token ‚Claude‘"]);
  });
});
