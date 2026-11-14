// @vitest-environment node
import { describe, expect, it } from "vitest";
import { settingsPlaceHeading, settingsReturnLabel } from "./settingsPlaces";

describe("settings places", () => {
  it("names each place in the rail", () => {
    expect(settingsPlaceHeading).toEqual({ profile: "Profil", space: "Nastavení prostoru", wiki: "Správa wiki" });
  });

  it("says the way back of space settings and Správa wiki the same wherever they were opened from, but for Správa wiki opened from space settings", () => {
    for (const route of ["#/", "#/pages/page-1", "#/spaces/space-1"]) {
      expect(settingsReturnLabel("space", route), route).toBe("Zpět do prostoru");
      expect(settingsReturnLabel("wiki", route), route).toBe("Zpět do wiki");
    }
    expect(settingsReturnLabel("wiki", "#/spaces/space-1/settings/access")).toBe("Zpět do nastavení prostoru");
  });

  it("says the way back of the profile by where the stored route leads", () => {
    const rows: [string, string][] = [
      ["#/pages/page-1", "Zpět na stránku"],
      ["#/pages/page-1?heading=pristupy", "Zpět na stránku"],
      ["#/pages/page-1/access/links", "Zpět na stránku"],
      ["#/spaces/space-1", "Zpět do prostoru"],
      ["#/spaces/space-1/about", "Zpět do prostoru"],
      ["#/spaces/space-1/new-page", "Zpět do prostoru"],
      ["#/spaces/space-1/settings/access", "Zpět do nastavení prostoru"],
      ["#/settings/people/accounts", "Zpět do správy wiki"],
      ["#/", "Zpět do wiki"],
      ["#/library/recent", "Zpět do wiki"],
      ["#/new-space", "Zpět do wiki"],
      ["#/unknown", "Zpět do wiki"],
    ];
    for (const [route, label] of rows) expect(settingsReturnLabel("profile", route), route).toBe(label);
  });
});
