import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, describe, expect, it } from "vitest";
import { parseReleaseNote } from "../whatsNew/releaseNotes";
import WhatsNewPage from "./WhatsNewPage.vue";

// Releases that do not exist: nothing of the real notes is built in.
const named = parseReleaseNote("4.2.0", "codename: Quick Quince\nmark: quince\n\n*9. 3. 2027*\n\n### Hlavně\nRychlejší všechno.\n- **Tabule:** nové rozvržení.\n",
  { quince: () => Promise.resolve("") });
const hotfix = parseReleaseNote("4.2.1", "*10. 3. 2027*\n\n### Opravy\n- Přihlášení funguje.\n");

afterEach(() => { document.body.innerHTML = ""; });

describe("WhatsNewPage", () => {
  it("replays a named release's splash in a dialog over the changelog, without the since-your-last-release line, and closes back", async () => {
    const wrapper = mount(WhatsNewPage, { props: { notes: [hotfix, named], current: "4.2.1" }, attachTo: document.body });
    expect(wrapper.find("[data-release-splash]").exists()).toBe(false);
    // Only a named release (codename and mark) offers the replay: „Přehrát úvod“ and its codename.
    expect(wrapper.findAll("[data-release-play]")).toHaveLength(1);
    expect(wrapper.findAll("[data-release-replay]")).toHaveLength(1);
    await wrapper.get("[data-release-play]").trigger("click");
    await flushPromises();
    const dialog = wrapper.get("dialog.whats-new-replay");
    expect((dialog.element as HTMLDialogElement).open).toBe(true);
    const splash = dialog.get("[data-release-splash]");
    expect(splash.get("h1").text()).toBe("Atlas 4.2 Quick Quince");
    expect(splash.get(".release-splash-eyebrow").text()).toBe("Verze · 9. 3. 2027");
    expect(splash.find(".release-splash-since").exists()).toBe(false);
    // The changelog stays underneath; the splash's „Celý changelog“ (or Esc, or a click outside) closes the dialog.
    expect(wrapper.find("[data-release='4.2.0']").exists()).toBe(true);
    await splash.get("[data-release-changelog]").trigger("click");
    await flushPromises();
    expect(wrapper.find("[data-release-splash]").exists()).toBe(false);
    expect(wrapper.emitted("close")).toBeUndefined();
    wrapper.unmount();
  });

  it("after an update still welcomes with the newest unseen named release", () => {
    const wrapper = mount(WhatsNewPage, { props: { notes: [hotfix, named], current: "4.2.1", since: "4.1.0" } });
    expect(wrapper.get(".release-splash-eyebrow").text()).toBe("Nová verze · 9. 3. 2027");
    expect(wrapper.get(".release-splash-since").text()).toBe("Novinky od vaší poslední verze 4.1: 2 vydání.");
  });

  it("after a patch of the named release the account knows welcomes with a small update of it, not a new release", () => {
    const wrapper = mount(WhatsNewPage, { props: { notes: [hotfix, named], current: "4.2.1", since: "4.2.0" } });
    const splash = wrapper.get("[data-release-splash]");
    expect(splash.attributes()).toHaveProperty("data-patch");
    expect(splash.get(".release-splash-eyebrow").text()).toBe("Opravná verze 4.2.1 · 10. 3. 2027");
    expect(splash.get("h1").text()).toBe("Atlas 4.2 Quick Quince");
    expect(splash.get(".release-splash-since").text()).toBe("Od vaší poslední verze 4.2: 1 opravná verze.");
    expect(splash.findAll(".release-splash-fixes li").map(item => item.text())).toEqual(["Přihlášení funguje"]);
    expect(splash.find(".release-splash-cards").exists()).toBe(false);
  });
});
