// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  accessKind, accessKinds, applyAccessKinds, grantableKinds, levelHierarchy, levelOptions, loadAccessKinds, pluginKinds, registerAccessKind, resetAccessKinds,
} from "./accessKinds";
import { grantNeedsReauth, reauthTargetOf, revokeNeedsReauth } from "./accessChanges";
import type { AccessSource, PrincipalAccess, ResourceAccess } from "./accessModel";
import { accessEnd, automaticRows, expiringSoon, levelCounts, matchesQuery, principalGroups, resourceRows, sourceTiming, sourcesSummary } from "./accessView";

afterEach(() => resetAccessKinds());

const source = (extra: Partial<AccessSource>): AccessSource => ({ via: "direct", label: "", level: "read", effective: true, ...extra });

describe("access kinds registry", () => {
  it("stands in with the core kinds and their staircases until the server answers", () => {
    expect(accessKinds().map((kind) => kind.id)).toEqual(["wiki", "space", "page", "vault", "secret", "token", "item"]);
    expect(levelOptions("vault").map((option) => option.label)).toEqual(["Vidí", "Čte", "Spravuje"]);
    expect(levelHierarchy("space")).toBe("Čte → Upravuje → Spravuje");
    expect(grantableKinds().map((kind) => kind.id)).toEqual(["space", "page", "vault", "secret", "item"]);
    // An unknown kind (a plugin without a UI module) still renders.
    expect(accessKind("jira")).toMatchObject({ id: "jira", icon: "blocks", one: "jira" });
  });

  it("takes the server's kinds, keeps a plugin's registered UI extras and names plugin kinds for the Co menu", async () => {
    const tabs = [{ id: "projects", label: "Projekty", icon: "blocks", component: {} }];
    registerAccessKind({ id: "youtrack", tabs });
    const load = vi.fn(async () => [
      { id: "space", one: "Prostor", many: "Prostory", icon: "book-open", parent: null, plugin: null, grantable: true, levels: [{ level: "manage" as const, description: "Spravuje" }, { level: "read" as const, description: "Čte" }] },
      { id: "youtrack", one: "YouTrack projekt", many: "YouTrack", icon: "blocks", parent: "space", plugin: "YouTrack", grantable: false, levels: [{ level: "read" as const, description: "Vidí úkoly" }] },
    ]);
    await loadAccessKinds(load);
    await loadAccessKinds(load);
    expect(load).toHaveBeenCalledTimes(1);
    expect(accessKinds().map((kind) => kind.id)).toEqual(["space", "youtrack"]);
    expect(levelOptions("space").map((option) => option.value)).toEqual(["read", "manage"]);
    expect(accessKind("youtrack").tabs).toBe(tabs);
    expect(pluginKinds().map((kind) => kind.id)).toEqual(["youtrack"]);
    expect(grantableKinds().map((kind) => kind.id)).toEqual(["space"]);
  });

  it("lets a failed load try again and a full registration add a kind before the server answers", async () => {
    await expect(loadAccessKinds(async () => Promise.reject(new Error("offline")))).rejects.toThrow("offline");
    registerAccessKind({ id: "youtrack", icon: "blocks", one: "YouTrack projekt", many: "YouTrack", plugin: "YouTrack", levels: { read: "Vidí úkoly" } });
    expect(pluginKinds().map((kind) => kind.id)).toEqual(["youtrack"]);
    applyAccessKinds([]);
    expect(accessKinds()).toEqual([]);
  });
});

describe("guard rules", () => {
  it("asks for a fresh sign-in for every vault and secret change and for Spravuje on a space or page", () => {
    expect(grantNeedsReauth("vault", "discover")).toBe(true);
    expect(grantNeedsReauth("secret", "read")).toBe(true);
    expect(grantNeedsReauth("space", "manage")).toBe(true);
    expect(grantNeedsReauth("page", "manage")).toBe(true);
    expect(grantNeedsReauth("space", "write")).toBe(false);
    expect(grantNeedsReauth("page", "read")).toBe(false);
    expect(revokeNeedsReauth("vault")).toBe(true);
    expect(revokeNeedsReauth("space")).toBe(false);
    // Every guarded change binds its fresh sign-in to the global security target.
    expect(reauthTargetOf({ kind: "secret", id: "s-1" })).toEqual({});
    expect(reauthTargetOf({ kind: "space", id: "provoz" })).toEqual({});
  });
});

describe("access rows", () => {
  const card: PrincipalAccess = {
    principal: { kind: "user", id: "u-1", name: "Jana" },
    scope: "wiki",
    canManage: true,
    entries: [
      { kind: "token", resourceId: "t-1", title: "Záloha", level: "read", sources: [source({})] },
      { kind: "vault", resourceId: "v-1", title: "Servery", spaceId: "provoz", spaceName: "Provoz", level: "read", sources: [source({})], note: "14 hesel" },
      { kind: "space", resourceId: "provoz", title: "Provoz", spaceId: "provoz", spaceName: "Provoz", level: "write", sources: [source({ via: "group", label: "Provoz tým", groupId: "g-1", level: "write" })] },
      { kind: "page", resourceId: "p-1", title: "Rozhodnutí", parentTitle: "Architektura", spaceId: "eng", spaceName: "Engineering", level: null, sources: [] },
      { kind: "secret", resourceId: "s-1", title: "Router", parentTitle: "Síť", spaceId: "provoz", spaceName: "Provoz", level: "read", sources: [source({})] },
      { kind: "wiki", resourceId: "wiki", title: "Wiki", level: "manage", sources: [source({ via: "automatic", label: "Správce wiki", level: "manage" })] },
    ],
  };

  it("groups a card by space: the wiki role first, Celý prostor before its exceptions, what is outside spaces last", () => {
    const groups = principalGroups(card);
    expect(groups.map((group) => group.title)).toEqual(["Celá wiki", "Engineering", "Provoz", "API a MCP"]);
    const provoz = groups[2]!;
    expect(provoz.description).toBe("Prostor · Upravuje přes skupinu Provoz tým");
    expect(provoz.rows.map((row) => [row.title, row.detail])).toEqual([
      ["Celý prostor", undefined],
      ["Servery", "Trezor · 14 hesel"],
      ["Router", "Heslo v trezoru Síť"],
    ]);
    expect(groups[1]!.rows[0]).toMatchObject({ title: "Architektura › Rozhodnutí", level: null });
    expect(groups[1]!.description).toBe("Prostor · jen vybrané části");
    expect(groups[3]!.rows[0]!.detail).toBe("Osobní API token");
  });

  it("words the paths, the validity and the counts of a row", () => {
    expect(sourcesSummary([source({}), source({ via: "group", label: "Ops", level: "manage" }), source({ via: "automatic", label: "Správce prostoru" })])).toBe("Přes skupinu Ops a 2 další cesty");
    expect(sourcesSummary([source({})])).toBe("Přímý přístup");
    const now = Date.parse("2026-09-29T10:00:00Z");
    expect(sourceTiming(source({ effective: false, startAt: "2026-10-01T00:00:00Z" }), now)).toBe("pending");
    expect(sourceTiming(source({ effective: false, expiresAt: "2026-09-01T00:00:00Z" }), now)).toBe("expired");
    expect(expiringSoon("2026-10-05T00:00:00Z", now)).toBe(true);
    expect(expiringSoon("2026-12-05T00:00:00Z", now)).toBe(false);
    // One unbounded path keeps the access without an end.
    expect(accessEnd([source({ expiresAt: "2026-10-05T00:00:00Z" }), source({ via: "group", label: "Ops" })])).toBeUndefined();
    expect(accessEnd([source({ expiresAt: "2026-10-05T00:00:00Z" }), source({ expiresAt: "2026-11-05T00:00:00Z" })])).toBe("2026-11-05T00:00:00Z");
    expect(levelCounts([{ level: "read" }, { level: "read" }, { level: null }, { level: "manage" }])).toEqual({ read: 2, manage: 1 });
    expect(matchesQuery("Síťová zařízení Provoz", "sitova prov")).toBe(true);
    expect(matchesQuery("Síťová zařízení", "router")).toBe(false);
  });

  it("keeps the grant end separate from a renewable Hub verification", () => {
    const hub = source({ via: "group", groupSource: "hub", verificationLimited: true, expiresAt: "2026-11-13T12:05:00Z", grantExpiresAt: null });
    expect(accessEnd([hub])).toBeUndefined();
    expect(accessEnd([{ ...hub, grantExpiresAt: "2026-12-01T12:00:00Z" }])).toBe("2026-12-01T12:00:00Z");
    expect(accessEnd([source({ expiresAt: "2026-11-13T12:02:00Z", groupSource: "hub" })])).toBe("2026-11-13T12:02:00Z");
  });

  it("turns who reaches a resource into locked automatic rows and one row per person or group", () => {
    const access: ResourceAccess = {
      resource: { kind: "vault", id: "v-1", title: "Servery" },
      canManage: true,
      automatic: [{ label: "Správci prostoru", detail: "mění se v přístupech prostoru", level: "manage", count: 3 }],
      entries: [
        { subject: { kind: "user", id: "u-2", name: "Petr", detail: "petr", isActive: false }, level: "read", sources: [source({})] },
        { subject: { kind: "group", id: "g-1", name: "Ops", memberCount: 1 }, level: "discover", sources: [source({ level: "discover" })] },
      ],
      hasMore: false,
    };
    expect(automaticRows(access)[0]).toMatchObject({ title: "Správci prostoru", detail: "3 lidé · mění se v přístupech prostoru", locked: true, level: "manage" });
    const rows = resourceRows(access, (subject) => `#/card/${subject.id}`);
    expect(rows.map((row) => [row.key, row.detail, row.href])).toEqual([
      ["group:g-1", "Skupina · 1 člen", "#/card/g-1"],
      ["user:u-2", "petr · deaktivovaný účet", "#/card/u-2"],
    ]);
  });
});
