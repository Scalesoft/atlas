// @vitest-environment node
import { describe, expect, it } from "vitest";
import { globalSettingsHome, resolveSettingsHash, sameSectionInSpace, sameSettingsSurface, spaceSettingsHash, spaceSettingsSection } from "./settingsRoutes";

describe("settings routes", () => {
  it("redirects the old #/settings and its former tabs to their sections", () => {
    expect(resolveSettingsHash("#/settings")).toEqual({ scope: "global", hash: globalSettingsHome, redirect: true });
    expect(resolveSettingsHash("#/settings/")).toMatchObject({ hash: "#/settings/security/overview", redirect: true });
    expect(resolveSettingsHash("#/settings/users")).toMatchObject({ hash: "#/settings/people/accounts", redirect: true });
    expect(resolveSettingsHash("#/settings/vaults")).toMatchObject({ hash: "#/settings/security/vaults", redirect: true });
    expect(resolveSettingsHash("#/settings/backups")).toMatchObject({ hash: "#/settings/general/backups", redirect: true });
    // Záloha do Gitu is a tab of Zálohy now.
    expect(resolveSettingsHash("#/settings/general/git-backup")).toMatchObject({ hash: "#/settings/general/backups/git", redirect: true });
    // Přehled práv is gone: its old links land on Lidé, where every person's card shows their access.
    expect(resolveSettingsHash("#/settings/security/access")).toMatchObject({ hash: "#/settings/people/accounts", redirect: true });
    expect(resolveSettingsHash("#/settings/security/access/")).toMatchObject({ hash: "#/settings/people/accounts", redirect: true });
  });

  it("keeps group/section links and their filters as they are", () => {
    expect(resolveSettingsHash("#/settings/people/groups")).toEqual({ scope: "global", hash: "#/settings/people/groups", redirect: false });
    expect(resolveSettingsHash("#/settings/security/audit?kdo=u-1")).toMatchObject({ scope: "global", redirect: false });
  });

  it("takes a section named like a property of every object for an unknown section, not for an old link", () => {
    for (const name of ["constructor", "toString", "valueOf", "__proto__"])
      expect(resolveSettingsHash(`#/settings/${name}`), name).toEqual({ scope: "global", hash: `#/settings/${name}`, redirect: false });
  });

  it("recognises space settings and sends the bare address to Obecné", () => {
    expect(resolveSettingsHash("#/spaces/provoz/settings")).toEqual({ scope: "space", spaceId: "provoz", hash: "#/spaces/provoz/settings/general", redirect: true });
    expect(resolveSettingsHash("#/spaces/provoz/settings/vaults/v-1/access")).toEqual({ scope: "space", spaceId: "provoz", hash: "#/spaces/provoz/settings/vaults/v-1/access", redirect: false });
    expect(spaceSettingsHash("provoz", "vaults")).toBe("#/spaces/provoz/settings/vaults");
  });

  it("moves to another space at the same section, dropping everything below the section", () => {
    const rows: [string, string][] = [
      ["#/spaces/provoz/settings/general", "general"],
      ["#/spaces/provoz/settings/vaults", "vaults"],
      ["#/spaces/provoz/settings/vaults/v-1/secrets", "vaults"],
      ["#/spaces/provoz/settings/vaults/new", "vaults"],
      ["#/spaces/provoz/settings/access/people/u-1", "access"],
      ["#/spaces/provoz/settings/access/exceptions/page/p-1", "access"],
      ["#/spaces/provoz/settings/requests?stav=pending", "requests"],
      ["#/spaces/provoz/settings/search-gaps", "search-gaps"],
      // No section named: the bare settings address and an address of another surface land on Obecné.
      ["#/spaces/provoz/settings", "general"],
      ["#/settings/people/accounts", "general"],
    ];
    for (const [hash, section] of rows) {
      expect(spaceSettingsSection(hash), hash).toBe(section);
      expect(sameSectionInSpace(hash, "vyvoj"), hash).toBe(`#/spaces/vyvoj/settings/${section}`);
    }
  });

  it("leaves other routes, including look-alike prefixes, to the application shell", () => {
    for (const hash of ["#/", "#/settings-preview/global/overview", "#/spaces/provoz", "#/spaces/provoz/page-templates", "#/profile/account"])
      expect(resolveSettingsHash(hash), hash).toBeUndefined();
  });

  it("tells whether two addresses belong to the same surface", () => {
    const global = resolveSettingsHash("#/settings/people/accounts");
    const provoz = resolveSettingsHash("#/spaces/provoz/settings/general");
    expect(sameSettingsSurface(global, resolveSettingsHash("#/settings/general/files"))).toBe(true);
    expect(sameSettingsSurface(provoz, resolveSettingsHash("#/spaces/provoz/settings/vaults"))).toBe(true);
    expect(sameSettingsSurface(provoz, resolveSettingsHash("#/spaces/vyvoj/settings/vaults"))).toBe(false);
    expect(sameSettingsSurface(global, provoz)).toBe(false);
    expect(sameSettingsSurface(undefined, global)).toBe(false);
  });
});
