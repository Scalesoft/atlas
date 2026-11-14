import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import SpaceCard from "./SpaceCard.vue";

const space = { id: "space-1", name: "Inženýrství", icon: "book", color: "blue", role: "Reader" as const, pageCount: 12, lastChangedAt: null, cover: { source: "Upload" as const, catalogKey: null, version: "a1", tinted: true } };

describe("SpaceCard cover", () => {
  it("shows an image that arrives during the card's entrance at once and fades only a late one", async () => {
    const card = mount(SpaceCard, { props: { space, href: "#/spaces/space-1", index: 0 } });
    const image = () => card.get("img.space-cover-image");
    expect(image().classes()).not.toContain("is-loaded");
    // Arrived while the card still rises in: part of that entrance, no fade of its own (it would delay the page's largest
    // paint). Shown two frames on, when the entrance is under way, never in its first frame at opacity 0.
    await image().trigger("load");
    expect(image().classes()).not.toContain("is-loaded");
    await vi.waitFor(() => expect(image().classes()).toContain("is-loaded"));
    expect(image().classes()).not.toContain("is-late");
    // The card has entered; another cover arrives later: that one fades in instead of popping.
    await card.get(".space-card").trigger("animationend");
    await card.setProps({ space: { ...space, cover: { ...space.cover, version: "b2" } } });
    expect(image().classes()).not.toContain("is-loaded");
    await image().trigger("load");
    expect(image().classes()).toEqual(expect.arrayContaining(["is-loaded", "is-late"]));
  });

  // The facts of a narrow card ("455 stránek", the role, "před 20 minutami") must stay on one line: when the time would be cut
  // mid-word the role gives way first, as its icon (its words stay for assistive technology and in the title).
  it("lets the role give way to its icon when the time of the last change would be cut", async () => {
    let observed: (() => void) | undefined;
    globalThis.ResizeObserver = class { constructor(callback: () => void) { observed = callback; } observe() {} disconnect() {} } as unknown as typeof ResizeObserver;
    let needed = 108;
    const card = mount(SpaceCard, { props: { space: { ...space, lastChangedAt: "2026-10-11T08:00:00Z" }, href: "#/spaces/space-1" }, attachTo: document.body });
    const time = card.get(".space-card-changed").element as HTMLElement;
    Object.defineProperty(time, "clientWidth", { get: () => 89 });
    Object.defineProperty(time, "scrollWidth", { get: () => needed });
    const facts = card.get(".space-card-facts");
    observed!();
    expect(facts.attributes("data-tight")).toBeDefined();
    // The role keeps its words for assistive technology and its title.
    expect(card.get("[data-space-access-level]").text()).toBe("Čte");
    expect(card.get("[data-space-access-level]").attributes("title")).toContain("Váš přístup");
    expect(card.find(".space-card-role-icon").exists()).toBe(true);
    // Room again (a wider card, a shorter time): the words come back.
    needed = 89;
    observed!();
    expect(facts.attributes("data-tight")).toBeUndefined();
    card.unmount();
    delete (globalThis as { ResizeObserver?: unknown }).ResizeObserver;
  });

  it("asks for the covers of the first view at once and leaves the rest lazy", () => {
    const attributes = (index: number) => mount(SpaceCard, { props: { space, index } }).get("img.space-cover-image").attributes();
    expect(attributes(0)).toMatchObject({ loading: "eager", fetchpriority: "high" });
    expect(attributes(5).loading).toBe("eager");
    expect(attributes(5).fetchpriority).toBeUndefined();
    expect(attributes(12).loading).toBe("lazy");
  });
});
