import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import DuplicatePagesSection from "./DuplicatePagesSection.vue";

let view: VueWrapper | undefined;
afterEach(() => view?.unmount());
const pair = { pageId: "p1", title: "Obnova dat", otherPageId: "p2", otherTitle: "Obnovení zálohy", similarity: 0.98 };
describe("duplicate page decisions", () => {
  it("keeps the pair after a failed dismissal and removes it only after a saved decision", async () => {
    const dismiss = vi.fn().mockRejectedValueOnce(new Error("offline")).mockResolvedValueOnce(undefined);
    const client = { list: vi.fn(async () => ({ items: [pair] })), dismiss };
    view = mount(DuplicatePagesSection, { props: { spaceId: "space", client } });
    await flushPromises();
    expect(view.findAll("[data-duplicate-page-link]").map(x => x.attributes("href"))).toEqual(["#/pages/p1", "#/pages/p2"]);
    await view.get("[data-dismiss-duplicate]").trigger("click");
    await flushPromises();
    expect(view.find("[data-duplicate-pair]").exists()).toBe(true);
    await view.get("[data-dismiss-duplicate]").trigger("click");
    await flushPromises();
    expect(dismiss).toHaveBeenLastCalledWith("space", "p1", "p2");
    expect(view.find("[data-duplicate-pair]").exists()).toBe(false);
    expect(view.text()).toContain("Žádné možné duplicity");
    expect(view.find("[data-ai-glyph]").exists()).toBe(false);
  });
});

it("ignores an old dismissal error after navigating to another space", async () => {
  let reject!: (error: Error) => void;
  const client = { list: vi.fn(async () => ({ items: [pair] })), dismiss: vi.fn(() => new Promise<void>((_resolve, fail) => { reject = fail; })) };
  view = mount(DuplicatePagesSection, { props: { spaceId: "first", client } });
  await flushPromises();
  await view.get("[data-dismiss-duplicate]").trigger("click");
  await view.setProps({ spaceId: "second" });
  await flushPromises();
  reject(new Error("OLD-SPACE-FAILURE"));
  await flushPromises();
  expect(view.text()).not.toContain("OLD-SPACE-FAILURE");
  expect(view.get("[data-dismiss-duplicate]").attributes("disabled")).toBeUndefined();
});
