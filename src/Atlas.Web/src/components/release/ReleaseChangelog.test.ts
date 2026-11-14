import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import { parseReleaseNote } from "../../whatsNew/releaseNotes";
import ReleaseChangelog from "./ReleaseChangelog.vue";

// Releases that do not exist: nothing of the real notes is built in.
const named = parseReleaseNote("4.2.0", "codename: Quick Quince\nmark: quince\n\n*9. 3. 2027*\n\n### Nové\n- **Tabule:** nové rozvržení.\n",
  { quince: () => Promise.resolve("") });
const short = parseReleaseNote("4.2.1", "*10. 3. 2027*\n\n### Opravy\n- Přihlášení funguje.\n");
const long = parseReleaseNote("4.2.2", "*17. 3. 2027*\n\n### Opravy\n- **Tabule** drží fokus. Vždy.\n- Dva.\n- Tři.\n- Čtyři.\n");
const older = parseReleaseNote("4.1.0", "*1. 2. 2027*\n\n### Nové\n- Kontakty.\n");

describe("ReleaseChangelog", () => {
  it("shows each named version once, its patches beneath it, smaller, newest first and with their dates", () => {
    const wrapper = mount(ReleaseChangelog, { props: { notes: [long, short, named, older], current: "4.2.2" } });
    const entries = wrapper.findAll(".release-entry");
    expect(entries.map(entry => entry.attributes("data-release"))).toEqual(["4.2.0", "4.1.0"]);
    expect(entries[0]!.attributes()).toHaveProperty("data-focus-landing");
    const patches = entries[0]!.findAll(".release-entry-patch");
    expect(patches.map(patch => patch.get("summary h4").text())).toEqual(["Atlas 4.2.2", "Atlas 4.2.1"]);
    expect(patches.map(patch => patch.get(".release-entry-patch-date").text())).toEqual(["17. 3. 2027", "10. 3. 2027"]);
    // A long patch is folded with its first point as a preview; a short one is open. The current release is the patch, not 4.2.
    expect(patches.map(patch => (patch.element as HTMLDetailsElement).open)).toEqual([false, true]);
    expect(patches[0]!.get(".release-entry-patch-preview").text()).toBe("Tabule drží fokus");
    expect(patches[0]!.find(".release-entry-current").exists()).toBe(true);
    expect(entries[0]!.findAll(".release-entry-current")).toHaveLength(1);
    expect(entries[1]!.find(".release-entry-patches").exists()).toBe(false);
  });
});
