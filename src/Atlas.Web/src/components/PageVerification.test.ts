import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PageVerification from "./PageVerification.vue";
import { verificationLabel, verificationMark, type PageVerification as Verification, type VerificationState } from "../pageVerification";

const day = 86_400_000;
const at = (days: number) => new Date(Date.now() + days * day).toISOString();
const sample = (state: VerificationState, over: Partial<Verification> = {}): Verification => ({ state, stewardId: "u1", stewardName: "Jana Malá", verifiedAt: at(-30), verifiedUntil: at(90), verifiedVersion: 3, changedSinceVerification: false, verifiedByName: "Petr Test", intervalMonths: null, spaceIntervalMonths: 6, ...over });
const api = () => ({ change: vi.fn(async (_: string, change: object) => sample("verified", { verifiedUntil: at(180), ...change })), report: vi.fn(async () => {}) });
const people = vi.fn(async () => [{ id: "u2", name: "Eva Nováková" }, { id: "u3", name: "Eva Dvořáková" }]);

let wrapper: VueWrapper | undefined;
function open(verification: Verification | null, props: { canEdit?: boolean; isPublished?: boolean; api?: ReturnType<typeof api> } = {}) {
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
    ["unverified", "Neověřeno", "warning"],
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
  });
});

describe("PageVerification popover", () => {
  it("shows the owner, who verified and what changed since, and an editor verifies with V", async () => {
    const service = api();
    open(sample("verified", { changedSinceVerification: true, verifiedViaAi: true }), { api: service });
    await openPopover();
    expect(pop()!.textContent).toContain("Jana Malá");
    expect(pop()!.textContent).toContain("Petr Test · přes AI");
    expect(pop()!.querySelector("[data-verification-changed]")!.textContent).toContain("Změněno po ověření");
    pop()!.dispatchEvent(new KeyboardEvent("keydown", { key: "v", bubbles: true }));
    await flushPromises();
    expect(service.change).toHaveBeenCalledWith("p1", { verify: true });
    expect(wrapper!.emitted("change")![0]![0]).toMatchObject({ state: "verified" });
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
  });
});
