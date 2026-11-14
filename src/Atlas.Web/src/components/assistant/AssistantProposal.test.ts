import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { ApiError } from "../../api";
import { PROPOSAL_FINDINGS, type Proposal } from "../../assistant/proposals";

const api = vi.hoisted(() => ({ get: vi.fn(), decide: vi.fn() }));
vi.mock("../../assistant/proposals", async (original) => ({ ...(await original<typeof import("../../assistant/proposals")>()), getProposal: api.get, decideProposal: api.decide }));
import AssistantProposal from "./AssistantProposal.vue";

const make = (count: number, over: Partial<Proposal> = {}): Proposal => ({
  id: "p1", messageId: "m1", tool: "atlas_apply_labels", state: "pending", expiresAt: "2026-10-16T12:00:00Z", ...over,
  items: Array.from({ length: count }, (_, index) => ({
    index, pageId: `pg${index}`, title: `Stránka ${index}`, path: `IT › Stránka ${index}`, kind: "labels" as const, status: null,
    before: { icon: null, color: null, tags: ["stary"] }, after: { icon: "lock", color: "blue", tags: ["IT"] },
  })),
});
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.clearAllMocks(); });
async function render(props: { proposal?: Proposal; proposalId?: string }) {
  wrapper = mount(AssistantProposal, { props, attachTo: document.body });
  await flushPromises();
  return wrapper;
}
const boxes = () => wrapper!.findAll('[role="checkbox"]');
const count = () => wrapper!.get("[data-proposal-count]").text();

describe("AssistantProposal", () => {
  it("sends only the kept indexes after two rows are unchecked", async () => {
    api.decide.mockResolvedValue(make(4, { state: "applied" }));
    await render({ proposal: make(4) });
    expect(count()).toBe("Vybráno 4 z 4");
    await boxes()[1]!.trigger("click");
    await boxes()[3]!.trigger("click");
    expect(count()).toBe("Vybráno 2 z 4");
    await wrapper!.get("[data-proposal-confirm]").trigger("click");
    await flushPromises();
    expect(api.decide).toHaveBeenCalledWith("p1", true, [0, 2]);
    expect(wrapper!.emitted("update")).toHaveLength(1);
  });

  it("selects all and none, and cannot confirm an empty selection", async () => {
    await render({ proposal: make(3) });
    const toggle = wrapper!.get("[data-proposal-toggle-all]");
    expect(toggle.text()).toBe("Zrušit výběr");
    await toggle.trigger("click");
    expect(count()).toBe("Vybráno 0 z 3");
    expect(toggle.text()).toBe("Vybrat vše");
    expect(wrapper!.get("[data-proposal-confirm]").attributes("disabled")).toBeDefined();
    await toggle.trigger("click");
    expect(count()).toBe("Vybráno 3 z 3");
  });

  it("shows the message of a 409 and marks nothing as applied", async () => {
    api.decide.mockRejectedValue(new ApiError(409, "x", "proposal_expired"));
    await render({ proposal: make(2) });
    await wrapper!.get("[data-proposal-confirm]").trigger("click");
    await flushPromises();
    expect(wrapper!.get("[data-proposal-error]").text()).toContain("Návrh vypršel");
    expect(wrapper!.findAll("[data-proposal-status]")).toHaveLength(0);
    expect(wrapper!.find("[data-result-banner]").exists()).toBe(false);
    expect(wrapper!.find("[data-proposal-confirm]").exists()).toBe(false);
    expect(wrapper!.get("[data-assistant-proposal]").attributes("data-state")).toBe("expired");
  });

  it("shows statuses and the summary after the decision", async () => {
    const done = make(3, { state: "applied" });
    done.items[0]!.status = "applied"; done.items[1]!.status = "failed"; done.items[1]!.code = "tags_changed"; done.items[2]!.status = "applied";
    api.decide.mockResolvedValue(done);
    await render({ proposal: make(3) });
    await wrapper!.get("[data-proposal-confirm]").trigger("click");
    await flushPromises();
    expect(wrapper!.get("[data-result-banner]").text()).toContain("Provedeno 2 změny, 1 se nepodařila");
    expect(wrapper!.findAll("[data-proposal-status]").map((s) => s.text())).toEqual(["Provedeno", "Tagy stránky mezitím někdo změnil", "Provedeno"]);
  });

  it("focuses Zrušit when it arrives, Escape leaves the decision open, and Tab goes from the list to Zrušit then Potvrdit", async () => {
    api.decide.mockResolvedValue(make(2, { state: "declined" }));
    await render({ proposal: make(2) });
    expect(document.activeElement).toBe(wrapper!.get("[data-proposal-cancel]").element);
    const order = [...(wrapper!.element as HTMLElement).querySelectorAll<HTMLElement>('button, [role="checkbox"]')].map((el) => el.hasAttribute("data-proposal-cancel") ? "cancel" : el.hasAttribute("data-proposal-confirm") ? "confirm" : "other");
    expect(order.indexOf("cancel")).toBeGreaterThan(order.lastIndexOf("other") - 1);
    expect(order.indexOf("confirm")).toBe(order.indexOf("cancel") + 1);
    await wrapper!.get("[data-assistant-proposal]").trigger("keydown", { key: "Escape" });
    await flushPromises();
    expect(api.decide).not.toHaveBeenCalled();
    expect(wrapper!.find("[data-proposal-confirm]").exists()).toBe(true);
  });

  it("passes Escape on to the panel when there is no question field to return to, and takes it when there is", async () => {
    await render({ proposal: make(2) });
    const root = wrapper!.get("[data-assistant-proposal]").element as HTMLElement;
    const press = () => { const event = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }); root.dispatchEvent(event); return event.defaultPrevented; };
    expect(press()).toBe(false);
    const panel = document.createElement("div");
    panel.setAttribute("data-assistant-panel", "");
    panel.append(document.createElement("textarea"));
    root.parentElement!.replaceWith(panel);
    panel.append(root.parentElement ?? root);
    expect(press()).toBe(true);
  });

  it("loads the card by proposalId for a reopened conversation without taking the focus", async () => {
    api.get.mockResolvedValue(make(2, { state: "superseded" }));
    await render({ proposalId: "p1" });
    expect(api.get).toHaveBeenCalledWith("p1", expect.anything());
    expect(wrapper!.get("[data-proposal-note]").text()).toContain("nahradila");
    expect(wrapper!.find('[role="checkbox"]').exists()).toBe(false);
    expect(wrapper!.emitted("update")).toHaveLength(1);
  });

  it("shows a section change as the history diff, a rename and a new draft, and opens an applied draft in the editor", async () => {
    const draft = (kind: "content" | "rename" | "draft", text: { sections: string[]; before: string; after: string }, index = 0) =>
      ({ index, pageId: kind === "draft" ? "00000000-0000-0000-0000-000000000000" : "pg1", title: "Kontakty", path: "IT › Kontakty", kind, before: null, after: null, status: null, text });
    const content = draft("content", { sections: ["Kontakty"], before: "## Kontakty\n\nVolejte ⟦komponenta: Jan⟧.", after: "## Kontakty\n\nV noci volejte ⟦komponenta: Jan⟧." });
    await render({ proposal: { ...make(0), tool: "atlas_update_section", items: [content] } });
    expect(wrapper!.get("[data-proposal-diff]").text()).toContain("Změněné sekce: Kontakty");
    expect(wrapper!.get("[data-proposal-diff] ins").text()).toContain("noci");
    expect(wrapper!.find("[data-proposal-open-editor]").exists()).toBe(false);
    await wrapper!.setProps({ proposal: { ...make(0), state: "applied", tool: "atlas_update_section", items: [{ ...content, status: "applied" }] } });
    await wrapper!.get("[data-proposal-open-editor]").trigger("click");
    expect(wrapper!.emitted("open-editor")).toEqual([["pg1"]]);
    wrapper!.unmount();

    await render({ proposal: { ...make(0), tool: "atlas_rename_page", items: [draft("rename", { sections: [], before: "VPN", after: "Připojení VPN" })] } });
    expect(wrapper!.get("[data-proposal-rename]").text()).toBe("VPNpřejmenovat naPřipojení VPN");
    expect(wrapper!.find("[data-proposal-diff]").exists()).toBe(false);
    wrapper!.unmount();

    await render({ proposal: { ...make(0), tool: "atlas_create_draft", items: [draft("draft", { sections: ["Postup"], before: "", after: "## Postup\n\nPřiložte kartu." })] } });
    expect(wrapper!.text()).toContain("Nový koncept Kontakty");
    expect(wrapper!.get("[data-proposal-diff]").text()).toContain("Začátek nové stránky");
  });

  it("says a dropped component failed the draft and nothing changed", async () => {
    const failed = { index: 0, pageId: "pg1", title: "Kontakty", path: "IT › Kontakty", kind: "content" as const, before: null, after: null, status: "failed" as const,
      code: "components_removed", text: { sections: ["Kontakty"], before: "Volejte ⟦komponenta: Jan⟧.", after: "Volejte." } };
    await render({ proposal: { ...make(0), state: "failed", tool: "atlas_replace_page", items: [failed] } });
    expect(wrapper!.get("[data-proposal-status]").text()).toContain("Komponenta by se ztratila");
    expect(wrapper!.get("[data-result-banner]").text()).toContain("Nic se nezměnilo");
    expect(wrapper!.find("[data-proposal-open-editor]").exists()).toBe(false);
  });

  it("shows a move as its path before → after with the subpages, and a cross-space move's access change", async () => {
    const move = (otherSpace: boolean, subpages: number) => ({ index: 0, pageId: "pg1", title: "Tiskárny", path: "IT › Tiskárny", kind: "move" as const,
      before: null, after: null, status: null, move: { before: "IT › Tiskárny", after: otherSpace ? "Provoz › Tiskárny" : "IT › Servery › Tiskárny", subpages, otherSpace } });
    await render({ proposal: { ...make(0), tool: "atlas_move_page", items: [move(false, 2)] } });
    expect(wrapper!.text()).toContain("Přesunout Tiskárny");
    expect(wrapper!.get("[data-proposal-move]").text()).toBe("z IT › Tiskárnydo IT › Servery › Tiskárny");
    expect(wrapper!.get("[data-proposal-move-facts]").text()).toBe("S ní se přesune 2 podstránky");
    wrapper!.unmount();

    await render({ proposal: { ...make(0), tool: "atlas_move_page_to_space", state: "applied", items: [{ ...move(true, 0), status: "applied" as const }] } });
    expect(wrapper!.get("[data-proposal-move-facts]").text()).toBe("Bez podstránek. Přístup převezme cílový prostor, vlastní práva stránek zaniknou.");
    // A move is not a text change: nothing to open in the editor.
    expect(wrapper!.find("[data-proposal-open-editor]").exists()).toBe(false);
  });

  it("shows a publication with its versions and the check before publication as PublishFindings rows", async () => {
    let finish: (checks: { id: string; severity: "warning"; text: string; kind: "secret" }[]) => void = () => {};
    const findings = vi.fn(() => new Promise<{ id: string; severity: "warning"; text: string; kind: "secret" }[]>((resolve) => { finish = resolve; }));
    const publish = { index: 0, pageId: "pg1", title: "VPN", path: "IT › VPN", kind: "publish" as const, before: null, after: null, status: null,
      publish: { version: 7, publishedVersion: 5, changedSections: 2, changeSummary: "Doplněn macOS",
        findings: { version: 7, links: [{ targetPageId: "pg9", state: "unpublished" as const, title: "Certifikáty" }] } } };
    wrapper = mount(AssistantProposal, { props: { proposal: { ...make(0), tool: "atlas_publish", items: [publish] } }, attachTo: document.body,
      global: { provide: { [PROPOSAL_FINDINGS as symbol]: findings } } });
    await flushPromises();
    expect(wrapper.text()).toContain("Publikovat VPN");
    expect(wrapper.get("[data-proposal-publish]").text()).toBe("Verze v7 (publikovaná v5) · změněné sekce: 2 · popis změny: Doplněn macOS");
    // The server's link finding at once; the browser's rules join it once the page is checked.
    expect(findings).toHaveBeenCalledWith({ pageId: "pg1", version: 7, findings: publish.publish.findings });
    expect(wrapper.get("[data-proposal-findings]").text()).toContain("Odkaz na nepublikovanou stránku „Certifikáty“");
    finish([{ id: "secret:1", severity: "warning", text: "Text vypadá jako heslo", kind: "secret" }]);
    await flushPromises();
    expect(wrapper.get("[data-proposal-findings]").text()).toContain("Text vypadá jako heslo");
  });

  it("turns read-only when the thread supersedes the proposal", async () => {
    await render({ proposal: make(2) });
    await wrapper!.setProps({ proposal: make(2, { state: "superseded" }) });
    expect(wrapper!.find("[data-proposal-confirm]").exists()).toBe(false);
  });
});
