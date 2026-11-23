import { mount } from "@vue/test-utils";
import { afterEach, describe, expect, it, onTestFinished, vi } from "vitest";
import { parseReleaseNote } from "../../whatsNew/releaseNotes";
import ReleaseSplash from "./ReleaseSplash.vue";

// A release that does not exist: nothing of 3.5 is built in.
const note = parseReleaseNote("4.2.0", "codename: Quick Quince\nmark: quince\n\n*9. 3. 2027*\n\n### Hlavně\nRychlejší všechno.\n"
  + "- **Tabule** `Alt T`: nové rozvržení.\n- **Kontakty:** rychlejší čipy.\n\n### Nové\n- **Tabule:** nové rozvržení.\n", { quince: () => Promise.resolve("") });
const hotfix = (version: string, point: string) => parseReleaseNote(version, `*1. 3. 2027*\n\n### Opravy\n- ${point}\n`);

afterEach(() => { document.body.innerHTML = ""; vi.restoreAllMocks(); });

describe("ReleaseSplash", () => {
  it("shows the release's motif and what is new since the account's last release, from the notes alone", () => {
    const wrapper = mount(ReleaseSplash, { props: { note, since: "4.1.0", others: [hotfix("4.1.2", "**Panel** stojí vpravo. Vždy."), hotfix("4.1.1", "Přihlášení funguje.")] } });
    expect(wrapper.find("h1").text()).toBe("Atlas 4.2 Quick Quince");
    expect(wrapper.find(".release-splash-eyebrow").text()).toBe("Nová verze · 9. 3. 2027");
    expect(wrapper.find(".release-mark").attributes()).toMatchObject({ "aria-hidden": "true", "data-variant": "hero" });
    expect(wrapper.text()).toContain("Novinky od vaší poslední verze 4.1: 3 vydání.");
    expect(wrapper.findAll(".release-splash-card").map(card => card.text())).toEqual(["TabuleAlt Tnové rozvržení.", "Kontaktyrychlejší čipy."]);
    expect(wrapper.find(".release-splash-also").text()).toBe("Také v 4.1.1–4.1.2: Přihlášení funguje. Panel stojí vpravo.");
  });

  it("a major release (x.0.0) is announced larger, with up to twelve highlights and the rest of its notes as chips; its patches are a small update as any", () => {
    const highlights = Array.from({ length: 13 }, (_, index) => `- **Bod ${index + 1}:** popis.\n`).join("");
    const major = parseReleaseNote("5.0.0", `codename: Big Berry\nmark: quince\n\n*9. 3. 2027*\n\n### Hlavně\nVšechno nové.\n${highlights}\n### Nové\n- **Tabule:** nové.\n- **Kontakty** jsou rychlejší.\n- bez názvu.\n`, { quince: () => Promise.resolve("") });
    const wrapper = mount(ReleaseSplash, { props: { note: major, since: "4.2.0", others: [] } });
    expect(wrapper.find("section").attributes("data-major")).toBe("");
    expect(wrapper.find(".release-splash-eyebrow").text()).toBe("Nová velká verze · 9. 3. 2027");
    expect(wrapper.findAll(".release-splash-card")).toHaveLength(12);
    expect(wrapper.find(".release-splash-more").text()).toBe("A k tomu 2 další novinkyTabuleKontakty");
    expect(mount(ReleaseSplash, { props: { note, since: "4.1.0", others: [] } }).find("section").attributes("data-major")).toBeUndefined();
    expect(mount(ReleaseSplash, { props: { note: major, since: "5.0.0", others: [], patches: [hotfix("5.0.1", "Oprava.")] } }).find("section").attributes("data-major")).toBeUndefined();
  });

  it("a click or a key during the intro only shows the texts; then Enter continues and Esc closes", async () => {
    const finish = vi.fn();
    const animate = vi.fn(() => ({ finish, finished: new Promise(() => {}) }) as unknown as Animation);
    Object.defineProperty(Element.prototype, "animate", { value: animate, configurable: true });
    onTestFinished(() => { delete (Element.prototype as Partial<Element>).animate; });
    const wrapper = mount(ReleaseSplash, { props: { note, since: "4.1.0", others: [] }, attachTo: document.body });
    expect(animate).toHaveBeenCalled();
    await wrapper.find("[data-release-continue]").trigger("click");
    expect(finish).toHaveBeenCalled();
    expect(wrapper.emitted("close")).toBeUndefined();
    document.body.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    expect(wrapper.emitted("close")).toBeUndefined();
    await wrapper.find("[data-release-continue]").trigger("click");
    await wrapper.find(".release-splash-lead").trigger("keydown", { key: "Enter" });
    await wrapper.find("section").trigger("keydown", { key: "Escape" });
    expect(wrapper.emitted("close")).toHaveLength(3);
    await wrapper.find("[data-release-changelog]").trigger("click");
    expect(wrapper.emitted("changelog")).toHaveLength(1);
    wrapper.unmount();
    // Esc closes at once, during the intro too: the splash never holds up work.
    const again = mount(ReleaseSplash, { props: { note, since: "4.1.0", others: [] }, attachTo: document.body });
    document.body.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    expect(again.emitted("close")).toHaveLength(1);
    again.unmount();
  });

  it("a highlight with a live demo shows it on top of its card, and the card flies out of the motif", async () => {
    const staged = parseReleaseNote("5.1.0", "codename: Tandem Tangerine\nmark: quince\ndemos: graph\n\n### Hlavně\nSpolu.\n- **Mapa znalostí:** kdo co umí.\n- **PostgreSQL 19:** nová databáze.\n",
      { quince: () => Promise.resolve("") }, { graph: 1 });
    const wrapper = mount(ReleaseSplash, { props: { note: staged, since: "5.0.0", others: [] } });
    expect(wrapper.find("section").attributes("data-staged")).toBe("");
    const [demo, plain] = wrapper.findAll(".release-splash-card");
    expect([demo!.attributes("data-fly"), demo!.attributes("data-wave"), demo!.find(".release-splash-demo").exists()]).toEqual(["", undefined, true]);
    expect([plain!.attributes("data-fly"), plain!.attributes("data-wave"), plain!.find(".release-splash-demo").exists()]).toEqual([undefined, "", false]);
    await vi.waitFor(() => expect(wrapper.find(".demo-graph").exists()).toBe(true));
  });

  it("a covered motif lies under the invisible ink, a button that Enter uncovers without leaving the splash", async () => {
    const wrapper = mount(ReleaseSplash, { props: { note: { ...note, cover: "secret" }, since: "4.1.0", others: [] }, attachTo: document.body });
    const cover = wrapper.find("[data-release-cover] .md-concealed");
    expect(cover.attributes()).toMatchObject({ role: "button", tabindex: "0", "aria-label": "Značka verze Atlas 4.2 Quick Quince: setřete krytí tahem, nebo stiskněte Enter či mezerník" });
    expect(wrapper.find(".release-mark").exists()).toBe(false);
    await cover.trigger("keydown", { key: "Enter" });
    await vi.waitFor(() => expect(wrapper.find("[data-release-cover] .sr-only[role=status]").text()).toBe("Odkryto: Značka verze Atlas 4.2 Quick Quince"));
    expect(wrapper.find("[data-release-cover] .release-mark").exists()).toBe(true);
    expect(cover.attributes("role")).toBeUndefined();
    expect(wrapper.emitted("close")).toBeUndefined();
    wrapper.unmount();
    // Replayed from the changelog (no `since`) the same release stands uncovered.
    const replay = mount(ReleaseSplash, { props: { note: { ...note, cover: "secret" }, others: [] } });
    expect([replay.find("[data-release-cover]").exists(), replay.find(".release-mark").exists()]).toEqual([false, true]);
  });
});
