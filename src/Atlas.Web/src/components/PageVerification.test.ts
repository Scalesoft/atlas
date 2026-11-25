import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PageVerification from "./PageVerification.vue";
import { verificationMark } from "../verification/verificationMark";
import { verificationIcon, verificationLabel, verificationTone, type PageVerification as Verification, type VerificationState } from "../verification/pageVerification";

const day = 86_400_000;
const at = (days: number) => new Date(Date.now() + days * day).toISOString();
const sample = (state: VerificationState, over: Partial<Verification> = {}): Verification => ({ state, stewardId: "u1", stewardName: "Jana Malá", verifiedAt: at(-30), verifiedUntil: at(90), verifiedVersion: 3, changedSinceVerification: false, verifiedByName: "Petr Test", intervalMonths: null, spaceIntervalMonths: 6, ...over });
const api = () => ({ read: vi.fn(), change: vi.fn(async (_: string, change: object) => sample("verified", { verifiedUntil: at(180), ...change })), report: vi.fn(async () => ({ notified: 1 })) });
const people = vi.fn(async () => [{ id: "u2", name: "Eva Nováková" }, { id: "u3", name: "Eva Dvořáková" }]);

let wrapper: VueWrapper | undefined;
function open(verification: Verification | null, props: { pageId?: string; canEdit?: boolean; isPublished?: boolean; online?: boolean; api?: ReturnType<typeof api> } = {}) {
  wrapper = mount(PageVerification, { attachTo: document.body, props: { pageId: "p1", verification, canEdit: true, isPublished: true, api: api(), people, ...props } });
  return wrapper;
}
const chip = () => document.body.querySelector<HTMLElement>("[data-page-verification-chip]");
const pop = () => document.body.querySelector<HTMLElement>("[data-page-verification-popover]");
async function openPopover() { chip()!.click(); await flushPromises(); }
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.clearAllMocks(); });

describe("PageVerification chip", () => {
  it("shows nothing while verification is off", () => {
    open(sample("off"));
    expect(chip()).toBeNull();
    wrapper!.unmount();
    open(null);
    expect(chip()).toBeNull();
  });

  it.each<[VerificationState, string, string | undefined]>([
    ["verified", "Ověřeno", "neutral"],
    ["expiring", "Vyprší za 9 dní", "neutral"],
    ["expired", "Ověření vypršelo", "warning"],
    ["unverified", "Neověřeno", "neutral"],
  ])("draws %s as %s in tone %s without a bar above the text", (state, text, tone) => {
    open(sample(state, state === "expiring" ? { verifiedUntil: at(8.5) } : {}));
    expect(chip()!.textContent).toContain(text);
    expect(chip()!.querySelector(".status-badge")!.getAttribute("data-tone")).toBe(tone);
    expect(chip()!.getAttribute("title")).toBeTruthy();
    expect(document.body.querySelector("[role=alert]")).toBeNull();
  });

  it("counts the days of an expiring verification and says today on the last one", () => {
    expect(verificationLabel({ state: "expiring", verifiedUntil: at(0) })).toBe("Vyprší dnes");
    expect(verificationLabel({ state: "expiring", verifiedUntil: at(1) })).toBe("Vyprší za 1 den");
    expect(verificationMark("verified")).toMatchObject({ icon: "shield-check", warn: false });
    expect(verificationMark("expired")).toMatchObject({ icon: "shield-alert", warn: true });
    expect(verificationMark("unverified")).toBeNull();
    // The chip derives from the same mapping: unverified is a plain neutral shield, never a check.
    expect([verificationIcon("unverified"), verificationTone("unverified")]).toEqual(["shield", "neutral"]);
    expect([verificationIcon("expiring"), verificationTone("expired")]).toEqual(["shield-check", "warning"]);
  });
});

describe("PageVerification popover", () => {
  it("shows the owner, who verified and what changed since, and an editor verifies with V", async () => {
    const service = api();
    service.change.mockResolvedValueOnce(sample("verified", { verifiedAt: at(0) }));
    open(sample("verified", { changedSinceVerification: true, verifiedViaAi: true }), { api: service });
    await openPopover();
    expect(pop()!.textContent).toContain("Jana Malá");
    expect(pop()!.textContent).toContain("Petr Test · přes AI");
    expect(pop()!.querySelector("[data-verification-changed]")!.textContent).toContain("Změněno po ověření");
    pop()!.dispatchEvent(new KeyboardEvent("keydown", { key: "v", bubbles: true }));
    await flushPromises();
    expect(service.change).toHaveBeenCalledWith("p1", { verify: true });
    expect(wrapper!.emitted("change")![0]![0]).toMatchObject({ state: "verified" });
    // The new verification is confirmed once on the chip (StatusBadge pop and halo); the bundle's old one never was.
    expect(chip()!.querySelector(".status-badge[data-confirmed]")).toBeNull();
    await wrapper!.setProps({ verification: wrapper!.emitted("change")![0]![0] as Verification });
    expect(chip()!.querySelector(".status-badge[data-confirmed]")).not.toBeNull();
  });

  it("cannot verify a page that is not published and says why", async () => {
    open(sample("unverified"), { isPublished: false });
    await openPopover();
    expect(pop()!.querySelector<HTMLButtonElement>("[data-verification-verify]")!.disabled).toBe(true);
    expect(pop()!.textContent).toContain("Nejdřív ji publikujte");
  });

  it("verifies until a chosen date and changes the owner with the first search result and Enter", async () => {
    const service = api();
    open(sample("expiring"), { api: service });
    await openPopover();
    pop()!.querySelector<HTMLElement>("[data-verification-until]")!.click();
    await flushPromises();
    const date = pop()!.querySelector<HTMLInputElement>("[data-verification-date]")!;
    date.value = "2031-01-15";
    date.dispatchEvent(new Event("input", { bubbles: true }));
    await flushPromises();
    pop()!.querySelector<HTMLFormElement>("form")!.dispatchEvent(new Event("submit", { cancelable: true }));
    await flushPromises();
    expect(service.change).toHaveBeenCalledWith("p1", { verify: true, until: "2031-01-15" });

    await openPopover();
    if (!pop()) await openPopover();
    pop()!.querySelector<HTMLElement>("[data-verification-steward-change]")!.click();
    await flushPromises();
    await vi.waitFor(() => expect(people).toHaveBeenCalled());
    await flushPromises();
    const search = pop()!.querySelector<HTMLInputElement>("input[type=search]")!;
    search.value = "Eva";
    search.dispatchEvent(new Event("input", { bubbles: true }));
    await vi.waitFor(() => expect(people).toHaveBeenCalledTimes(2), { timeout: 1000 });
    await flushPromises();
    search.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    await flushPromises();
    expect(service.change).toHaveBeenLastCalledWith("p1", { stewardId: "u2" });
  });

  it("clears the owner with clearSteward, as the server cannot tell null from a missing key", async () => {
    const service = api();
    open(sample("verified"), { api: service });
    await openPopover();
    pop()!.querySelector<HTMLElement>("[data-verification-steward-change]")!.click();
    await flushPromises();
    pop()!.querySelector<HTMLElement>("[data-verification-steward-clear]")!.click();
    await flushPromises();
    expect(service.change).toHaveBeenCalledWith("p1", { clearSteward: true });
  });

  it("lets a reader report the page as outdated with a note, and nothing else", async () => {
    const service = api();
    open(sample("expired", { verifiedUntil: at(-3) }), { canEdit: false, api: service });
    await openPopover();
    expect(pop()!.querySelector("[data-verification-verify]")).toBeNull();
    pop()!.querySelector<HTMLElement>("[data-verification-report]")!.click();
    await flushPromises();
    const note = pop()!.querySelector<HTMLTextAreaElement>("[data-verification-note]")!;
    note.value = "Postup už neplatí";
    note.dispatchEvent(new Event("input", { bubbles: true }));
    await flushPromises();
    pop()!.querySelector<HTMLFormElement>("form")!.dispatchEvent(new Event("submit", { cancelable: true }));
    await flushPromises();
    expect(service.report).toHaveBeenCalledWith("p1", "Postup už neplatí");
    expect(pop()!.querySelector("[data-verification-reported]")).not.toBeNull();
    expect(service.change).not.toHaveBeenCalled();
    // The keyboard moves to the status, the popover closes by itself, and the page cannot be reported again this session.
    expect(document.activeElement).toBe(pop()!.querySelector("[data-verification-reported]"));
    await vi.waitFor(() => expect(pop()).toBeNull(), { timeout: 3000 });
    await openPopover();
    pop()!.querySelector<HTMLElement>("[data-verification-report]")!.click();
    await flushPromises();
    expect(pop()!.querySelector("[data-verification-note]")).toBeNull();
    expect(pop()!.querySelector("[data-verification-reported]")).not.toBeNull();
  });

  it("tells the reader when the report reached nobody, and offers „Porovnat verze“ only to who may open the history", async () => {
    const service = api();
    service.report.mockResolvedValueOnce({ notified: 0 });
    open(sample("expired", { changedSinceVerification: true }), { pageId: "p-nobody", canEdit: false, api: service });
    await openPopover();
    expect(pop()!.querySelector("[data-verification-changed]")!.textContent).toContain("Změněno po ověření");
    expect(pop()!.querySelector("[data-verification-compare]")).toBeNull();
    pop()!.querySelector<HTMLElement>("[data-verification-report]")!.click();
    await flushPromises();
    pop()!.querySelector<HTMLFormElement>("form")!.dispatchEvent(new Event("submit", { cancelable: true }));
    await flushPromises();
    expect(pop()!.querySelector("[data-verification-reported]")!.textContent).toContain("nemá komu přijít");
    wrapper!.unmount();
    open(sample("expired", { changedSinceVerification: true }));
    await openPopover();
    expect(pop()!.querySelector("[data-verification-compare]")).not.toBeNull();
  });

  it("keeps an offline editor's actions, disabled, instead of the reader's", async () => {
    open(sample("expired"), { online: false });
    await openPopover();
    expect(pop()!.querySelector("[data-verification-report]")).toBeNull();
    expect(pop()!.querySelector<HTMLButtonElement>("[data-verification-verify]")!.disabled).toBe(true);
    expect(pop()!.querySelector<HTMLButtonElement>("[data-verification-steward-change]")!.disabled).toBe(true);
  });

  it("opens on the owner search from the page panel and gives the keyboard back to the panel's link", async () => {
    const link = document.createElement("button");
    document.body.append(link);
    open(sample("verified"));
    link.focus();
    await (wrapper!.vm as unknown as { openSteward(): Promise<void> }).openSteward();
    await flushPromises();
    expect(pop()!.querySelector("input[type=search]")).not.toBeNull();
    pop()!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    await vi.waitFor(() => expect(pop()).toBeNull());
    await flushPromises();
    expect(document.activeElement).toBe(link);
  });
});
