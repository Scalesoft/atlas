import { afterEach, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import PageCard from "./PageCard.vue";
import AtlasIcon from "../../AtlasIcon.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); });

const id = "3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b";
const target = { type: "atlas.page", resourceId: id, title: "Provozní příručka", spaceTitle: "Provoz", excerpt: "Tento postup popisuje, jak se vydává.", available: true };
const show = (props: Record<string, unknown>) => (wrapper = mount(PageCard, { props: { item: { id: "i1", targetResourceId: id }, ...props } }));

it("shows the page: title, space, the first text and a link that opens it", async () => {
  show({ target });
  expect(wrapper!.get(".bcard-title").text()).toBe("Provozní příručka");
  expect(wrapper!.get(".bcard-muted").text()).toBe("Provoz");
  expect(wrapper!.get("[data-board-excerpt]").text()).toBe("Tento postup popisuje, jak se vydává.");
  const open = wrapper!.get("[data-board-open-page]");
  expect(open.attributes("href")).toContain(`#/pages/${id}`);
  expect(open.text()).toBe("Otevřít");
  // A drag that starts on the link must not move the node (Vue Flow).
  expect(open.classes()).toEqual(expect.arrayContaining(["nodrag", "nopan"]));

  const click = new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 });
  expect(open.element.dispatchEvent(click)).toBe(false);
  expect(wrapper!.emitted("open")).toEqual([[id]]);
  await open.trigger("click", { ctrlKey: true });
  await open.trigger("click", { button: 1 });
  expect(wrapper!.emitted("open")).toHaveLength(1);
});

// 3.6 (owner, 2026-11-06): no „Upravit“ on the card; the board edits the page by Enter, F2 and a double-click (BoardCanvas).
it("has no „Upravit“, not even for who may edit the page", () => {
  show({ target: { ...target, canEdit: true } });
  expect(wrapper!.find("[data-board-edit-page]").exists()).toBe(false);
  expect(wrapper!.findAll("button").map(button => button.text())).not.toContain("Upravit");
  expect(wrapper!.get(".bcard-actions").text()).toBe("Otevřít");
});

it("shows the icon of the target's type (the unavailable stand-in is an unlinked chain)", () => {
  show({ target });
  expect(wrapper!.get(".bcard-icon svg").attributes("data-icon")).toBe("file-text");
  wrapper!.unmount();
  show({ target: { ...target, typeId: "unavailable" } });
  expect(wrapper!.get(".bcard-icon svg").attributes("data-icon")).toBe("unlink");
});

it("reads the target from the item when none is given, and prefers a given one", () => {
  show({ item: { id: "i1", targetResourceId: id, target } });
  expect(wrapper!.get(".bcard-title").text()).toBe("Provozní příručka");
  wrapper!.unmount();
  show({ item: { id: "i1", targetResourceId: id, target }, target: { ...target, title: "Jiný název" } });
  expect(wrapper!.get(".bcard-title").text()).toBe("Jiný název");
});

it("is content with what the current server sends: no space, no excerpt, no empty box of either", () => {
  show({ target: { resourceId: id, type: "atlas.page", title: "Příručka", available: true } });
  expect(wrapper!.get(".bcard-title").text()).toBe("Příručka");
  expect(wrapper!.find(".bcard-muted").exists()).toBe(false);
  expect(wrapper!.find("[data-board-excerpt]").exists()).toBe(false);
  expect(wrapper!.find("[data-board-open-page]").exists()).toBe(true);
});

it("reads what the session keeps: a title and a flag, with the id from the item", () => {
  show({ target: { title: "Příručka", available: true } });
  expect(wrapper!.get("[data-board-open-page]").attributes("href")).toContain(`#/pages/${id}`);
});

it("is a locked placeholder without title, text or link for a page the reader may not see, whatever the target holds", () => {
  show({ target: { ...target, available: false } });
  const card = wrapper!.get("[data-board-card=page]");
  expect(card.attributes("data-unavailable")).toBeDefined();
  expect(card.text()).toBe("Stránka není dostupná");
  expect(card.text()).not.toContain("Provozní");
  expect(wrapper!.find("a").exists()).toBe(false);
});

it("says a readable page is in the trash, with Obnovit only for who may restore it, and not as a page without access", () => {
  show({ target: { type: "atlas.page", resourceId: id, title: null, available: false, trashed: true, spaceId: "s1" } });
  expect(wrapper!.get("[data-board-trashed]").text()).toBe("Stránka je v koši");
  expect(wrapper!.find("[data-board-restore-page]").exists()).toBe(false);
  wrapper!.unmount();
  show({ target: { type: "atlas.page", resourceId: id, title: null, available: false, trashed: true, spaceId: "s1", canRestore: true } });
  expect(wrapper!.get("[data-board-restore-page]").text()).toBe("Obnovit");
});

it("is still resolving while the target is unknown: nothing before the defer, then a placeholder without link or title, and not the lock", async () => {
  vi.useFakeTimers();
  show({ target: undefined });
  expect(wrapper!.get(".bcard-swap").attributes("aria-busy")).toBe("true");
  expect(wrapper!.find("[data-board-card]").exists()).toBe(false);
  await vi.advanceTimersByTimeAsync(150);
  const card = wrapper!.get("[data-board-card=page]");
  expect(card.text()).toBe("Načítání stránky…");
  expect(wrapper!.find("a").exists()).toBe(false);
  expect(wrapper!.find("[data-unavailable]").exists()).toBe(false);
  // The answer replaces the placeholder with the content.
  await wrapper!.setProps({ target });
  expect(wrapper!.get(".bcard-title").text()).toBe("Provozní příručka");
  expect(wrapper!.get(".bcard-swap").attributes("aria-busy")).toBeUndefined();
});

it("never shows the placeholder when the target arrives within the defer", async () => {
  vi.useFakeTimers();
  show({ target: undefined });
  await vi.advanceTimersByTimeAsync(100);
  await wrapper!.setProps({ target });
  expect(wrapper!.get(".bcard-title").text()).toBe("Provozní příručka");
  await vi.advanceTimersByTimeAsync(500);
  expect(wrapper!.find("[data-board-pending]").exists()).toBe(false);
});

it("turns the placeholder into the lock when the answer is that the page is not available", async () => {
  vi.useFakeTimers();
  show({ target: undefined });
  await vi.advanceTimersByTimeAsync(150);
  await wrapper!.setProps({ target: { resourceId: id, title: null, available: false } });
  expect(wrapper!.get("[data-unavailable]").text()).toBe("Stránka není dostupná");
});

it("takes a page without a title for one the reader may not see", () => {
  show({ target: { resourceId: id, available: true, title: null } });
  expect(wrapper!.find("[data-board-card=page]").exists()).toBe(true);
  expect(wrapper!.get(".bcard-title").text()).toBe("Stránka bez názvu");
});

it("draws a board and a contact of the relations graph with their own icon and address, a contact with none", async () => {
  show({ target: { ...target, typeId: "atlas.board" } });
  expect(wrapper!.getComponent(AtlasIcon).props("name")).toBe("layout-dashboard");
  expect(wrapper!.get("[data-board-open-page]").attributes("href")).toContain(`#/boards/${id}`);
  wrapper!.unmount();
  show({ target: { ...target, typeId: "atlas.contact" } });
  expect(wrapper!.getComponent(AtlasIcon).props("name")).toBe("user");
  const open = wrapper!.get("[data-board-open-page]");
  expect(open.attributes("href")).toBeUndefined();
  await open.trigger("keydown", { key: "Enter" });
  expect(wrapper!.emitted("open")).toEqual([[id]]);
  // A role="button" opens with Space too (review 3.5.1, finding 13).
  await open.trigger("keydown", { key: " " });
  expect(wrapper!.emitted("open")).toEqual([[id], [id]]);
});
