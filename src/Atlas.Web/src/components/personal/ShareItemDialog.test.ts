import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { AccessCandidate, ResourceAccess } from "../settings/access/accessModel";
import ShareItemDialog from "./ShareItemDialog.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.useRealTimers(); });

const access = (entries: ResourceAccess["entries"] = []): ResourceAccess => ({
  resource: { kind: "item", id: "n1", title: "Porada", parentTitle: "Poznámka" },
  canManage: true,
  automatic: [{ label: "Vlastník", detail: "Vy", level: "manage", count: 1 }],
  entries,
  hasMore: false,
});
const people: AccessCandidate[] = [
  { kind: "user", id: "u1", name: "Jana Nováková", detail: "jana" },
  { kind: "user", id: "u2", name: "Eva Malá", detail: "eva" },
];

async function open(entries: ResourceAccess["entries"] = []) {
  const pending: Array<() => void> = [];
  const api = {
    resource: vi.fn(async () => access(entries)),
    candidates: vi.fn(async (kind: "user" | "group", q: string) => {
      await new Promise<void>(resolve => { pending.push(resolve); });
      return kind === "user" ? people.filter(person => person.name.toLowerCase().includes(q.toLowerCase())) : [];
    }),
    grant: vi.fn(async () => {}),
    revoke: vi.fn(async () => {}),
  };
  wrapper = mount(ShareItemDialog, { attachTo: document.body, props: { open: true, itemId: "n1", title: "Porada", api } });
  await flushPromises();
  return { api, search: async (text: string) => {
    vi.useFakeTimers();
    await wrapper!.get("[data-share-form] input[type=search]").setValue(text);
    // Typed, not answered yet: the list says it is searching, never that nobody matches (UX1-11).
    expect(wrapper!.get("[data-share-form]").text()).toContain("Hledám…");
    expect(wrapper!.get("[data-share-form]").text()).not.toContain("Nikdo neodpovídá");
    await vi.advanceTimersByTimeAsync(250);
    vi.useRealTimers();
    pending.splice(0).forEach(resolve => resolve());
    await flushPromises();
  } };
}

it("shares a note in one form: Enter picks the first person and moves to the level, Enter shares it as read", async () => {
  const { api, search } = await open();
  // The owner is never offered (exceptMe) and the dialog opens in the search, not on Zavřít (UX1-3, UX1-4).
  expect(document.activeElement).toBe(wrapper!.get("[data-share-form] input[type=search]").element);
  await search("jana");
  expect(api.candidates).toHaveBeenCalledWith("user", "jana", true);
  await wrapper!.get("[data-share-form] input[type=search]").trigger("keydown", { key: "Enter" });
  await flushPromises();
  expect(wrapper!.get("[data-share-summary]").text()).toBe("Jana Nováková · čte · bez omezení");
  const level = wrapper!.get('[data-share-level] [role="radio"][data-state="checked"]');
  expect(document.activeElement).toBe(level.element);
  await level.trigger("keydown", { key: "Enter" });
  await flushPromises();
  expect(api.grant).toHaveBeenCalledWith("item", "n1", { subjectKind: "user", subjectId: "u1", level: "read", startAt: null, expiresAt: null, expectedVersion: null }, null);
  // Ready for the next person; the list beneath is read again.
  expect((wrapper!.get("[data-share-form] input[type=search]").element as HTMLInputElement).value).toBe("");
  expect(api.resource).toHaveBeenCalledTimes(2);
});

it("offers the end of a share only on demand and does not offer again who already has access", async () => {
  const { api, search } = await open([{ subject: { kind: "user", id: "u2", name: "Eva Malá" }, level: "write",
    sources: [{ via: "direct", label: "Přímo", level: "write", version: "v1", effective: true }] }]);
  expect(wrapper!.find(".grant-validity").exists()).toBe(false);
  await search("a");
  expect(wrapper!.get('[data-value="user:u2"]').attributes("data-disabled")).toBeDefined();
  expect(wrapper!.get('[data-value="user:u2"]').text()).toContain("Už má přístup");
  await wrapper!.get('[data-value="user:u1"]').trigger("click");
  await wrapper!.get('[data-share-level] [data-value="write"]').trigger("click");
  await wrapper!.get("[data-share-limit]").trigger("click");
  await flushPromises();
  expect(wrapper!.find(".grant-validity").exists()).toBe(true);
  await wrapper!.get("[data-share-submit]").trigger("click");
  await flushPromises();
  expect(api.grant).toHaveBeenCalledWith("item", "n1", expect.objectContaining({ subjectId: "u1", level: "write", expiresAt: null }), null);
});
