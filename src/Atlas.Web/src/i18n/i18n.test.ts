import { afterEach, describe, expect, it, vi } from "vitest";

// The language is decided when src/i18n loads: a test of English sets the cookie, resets the modules, imports afresh and
// waits for the texts, as main.ts and lazyComponent do.
async function load(cookie?: string) {
  document.cookie = cookie ? `atlas-language=${cookie};path=/` : "atlas-language=;path=/;max-age=0";
  vi.resetModules();
  const core = await import("./index");
  const profile = (await import("./profile")).default;
  await core.textsLoaded();
  return { core, profile };
}

afterEach(() => {
  document.cookie = "atlas-language=;path=/;max-age=0";
});

describe("the language of the tool", () => {
  it("is Czech for a Czech browser, with Czech plurals and numbers", async () => {
    const { core, profile } = await load();
    expect([core.language, core.locale, document.documentElement.lang]).toEqual(["cs", "cs-CZ", "cs"]);
    const vaults = (n: number) => core.plural(n, { one: "# trezor", few: "# trezory", other: "# trezorů" });
    expect([vaults(1), vaults(3), vaults(5), vaults(1000)]).toEqual(["1 trezor", "3 trezory", "5 trezorů", "1 000 trezorů"]);
    expect(profile.appearance.motion.system(true)).toBe("Podle systému (zapnuto)");
  });

  it("is the account's choice from the cookie, its area texts loaded before use, and a change asks for a reload", async () => {
    const { core, profile } = await load("en");
    expect([core.language, core.locale, document.documentElement.lang]).toEqual(["en", "en-US", "en"]);
    expect([core.plural(1, { one: "# vault", other: "# vaults" }), core.plural(1000, { one: "# vault", other: "# vaults" })]).toEqual(["1 vault", "1,000 vaults"]);
    expect([profile.appearance.title, profile.appearance.motion.system(false)]).toEqual(["Appearance", "Automatic (off)"]);
    expect(core.rememberLanguage("en")).toBe(false);
    // Automaticky in a Czech browser: this English page must reload, and the cookie is gone.
    expect(core.rememberLanguage("auto")).toBe(true);
    expect(document.cookie).not.toContain("atlas-language");
  });
});
