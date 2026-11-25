import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { LiveObject } from "../../objects/liveObjects";
import { toast } from "vue-sonner";
import PageContactEditor from "./PageContactEditor.vue";

vi.mock("vue-sonner", () => ({ toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn(), dismiss: vi.fn() } }));

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });
const id = "0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b";
const snapshot = { name: "Denis", emails: ["denis@example.cz"], objectId: id, role: "Správce" };

function editor(canEdit = true, total = 3) {
  const object: LiveObject = { id, typeId: "atlas.contact", version: 1, title: "Denis", data: { name: "Denis", emails: ["denis@example.cz"] }, canEdit, available: true };
  const store = { get: vi.fn(() => object), load: vi.fn(async () => {}), edit: vi.fn(async (_id: string, fields: Record<string, unknown>) => { object.data = { ...object.data, ...fields }; }) };
  const save = vi.fn(), forget = vi.fn();
  wrapper = mount(PageContactEditor, { attachTo: document.body, props: { snapshot, save, store, forget, usage: { count: async () => ({ objectId: id, total, visible: total, hidden: 0 }) } } });
  return { store, save, forget };
}

it("saves a directory field to the contact at once and stores the page's snapshot when it closes", async () => {
  const { store, save, forget } = editor();
  await flushPromises();
  expect(wrapper!.text()).toContain("ještě na 2 místech");
  await wrapper!.get('[data-contact-field="emails"] .contact-edit-link').trigger("click");
  await wrapper!.get("[data-contact-input]").setValue("denis@example.cz\nd@firma.example.test");
  await wrapper!.get("[data-contact-input]").trigger("keydown", { key: "Enter", ctrlKey: true });
  await vi.waitFor(() => expect(store.edit).toHaveBeenCalledWith(id, { emails: ["denis@example.cz", "d@firma.example.test"] }));
  expect(forget).toHaveBeenCalledWith(id);
  await wrapper!.get("[data-page-contact-role]").setValue("");
  const inputs = wrapper!.findAll("input");
  await inputs[1]!.setValue("denis.n");
  expect(save).not.toHaveBeenCalled();
  wrapper!.unmount(); wrapper = undefined;
  await flushPromises();
  expect(save).toHaveBeenCalledTimes(1);
  const stored = save.mock.calls[0]![0];
  expect(stored).toMatchObject({ name: "Denis", emails: ["denis@example.cz", "d@firma.example.test"], username: "denis.n", objectId: id });
  expect(stored.role).toBeUndefined();
});

it("stores nothing when nothing changed, and reads only without the right to change the contact", async () => {
  const { save } = editor(false, 1);
  await flushPromises();
  expect(wrapper!.find(".contact-edit-link").exists()).toBe(false);
  expect(wrapper!.text()).toContain("může měnit jen ten");
  expect(document.activeElement).toBe(wrapper!.get("[data-page-contact-role]").element);
  wrapper!.unmount(); wrapper = undefined;
  expect(save).not.toHaveBeenCalled();
});

it("a field cleared in the directory leaves the page's snapshot, and a field still open is kept when the panel closes", async () => {
  const { save } = editor();
  await flushPromises();
  await wrapper!.setProps({ snapshot: { ...snapshot, website: "https://old.example.cz", organization: "Stará firma" } });
  await wrapper!.get('[data-contact-field="phones"] .contact-edit-link').trigger("click");
  await wrapper!.get("[data-contact-input]").setValue("+420 777 000 111");
  (wrapper!.get("[data-contact-input]").element as HTMLElement).focus();
  // The editor's rules have loaded (start() asked for them); a click outside the panel then unmounts it with the field open.
  await import("@atlas/markdown/plugins/contacts"); await flushPromises();
  document.body.dispatchEvent(new Event("pointerdown", { bubbles: true }));
  wrapper!.unmount(); wrapper = undefined;
  await flushPromises();
  const stored = save.mock.calls[0]![0];
  expect(stored.phones).toEqual(["+420 777 000 111"]);
  expect(stored.website).toBeUndefined(); expect(stored.organization).toBeUndefined();
  expect(stored.role).toBe("Správce");
});

it("closing otherwise (Esc) leaves the open field's draft", async () => {
  const { store, save } = editor();
  await flushPromises();
  await wrapper!.get('[data-contact-field="phones"] .contact-edit-link').trigger("click");
  await wrapper!.get("[data-contact-input]").setValue("+420 777 000 111");
  wrapper!.unmount(); wrapper = undefined;
  await flushPromises();
  expect(store.edit).not.toHaveBeenCalled(); expect(save).not.toHaveBeenCalled();
});

it("stores the page's snapshot at once as shown, and again without a value the directory refuses later", async () => {
  const { store, save } = editor();
  await flushPromises();
  let refuse!: (error: Error) => void;
  // As liveObjects: shown at once, read again after the refusal, which is thrown on.
  store.edit.mockImplementationOnce(async (_id, fields) => {
    const before = store.get().data;
    store.get().data = { ...before, ...fields };
    await new Promise((_, reject) => { refuse = reject; }).catch(error => { store.get().data = before; throw error; });
  });
  await wrapper!.get('[data-contact-field="phones"] .contact-edit-link').trigger("click");
  await wrapper!.get("[data-contact-input]").setValue("+420 777 000 111");
  await wrapper!.get("[data-contact-input]").trigger("keydown", { key: "Enter", ctrlKey: true });
  await vi.waitFor(() => expect(store.edit).toHaveBeenCalledOnce());
  // Leaving edit mode right after the change: the snapshot goes out before the directory answers, while the page still edits.
  wrapper!.unmount(); wrapper = undefined;
  expect(save).toHaveBeenCalledTimes(1);
  expect(save.mock.calls[0]![0].phones).toEqual(["+420 777 000 111"]);
  refuse(new Error("Zamítnuto"));
  await flushPromises();
  expect(save).toHaveBeenCalledTimes(2);
  expect(save.mock.calls[1]![0].phones).toBeUndefined();
});

it("a refusal answered after a newer panel of the contact stored its snapshot leaves that one", async () => {
  const { store, save } = editor();
  await flushPromises();
  let refuse!: (error: Error) => void;
  store.edit.mockImplementationOnce(async () => { await new Promise((_, reject) => { refuse = reject; }); });
  await wrapper!.get('[data-contact-field="phones"] .contact-edit-link').trigger("click");
  await wrapper!.get("[data-contact-input]").setValue("+420 777 000 111");
  await wrapper!.get("[data-contact-input]").trigger("keydown", { key: "Enter", ctrlKey: true });
  await vi.waitFor(() => expect(store.edit).toHaveBeenCalledOnce());
  wrapper!.unmount();
  const newer = mount(PageContactEditor, { attachTo: document.body, props: { snapshot, save, store, forget: vi.fn(), usage: { count: async () => ({ objectId: id, total: 1, visible: 1, hidden: 0 }) } } });
  await flushPromises();
  await newer.get("[data-page-contact-role]").setValue("Vedoucí");
  newer.unmount(); wrapper = undefined;
  refuse(new Error("Zamítnuto"));
  await flushPromises();
  expect(save).toHaveBeenCalledTimes(2);
  expect(save.mock.calls[1]![0].role).toBe("Vedoucí");
});

it("2B: says „Kontakt upraven“ with „Zpět“, which writes the fields as they were back to the contact (P6)", async () => {
  const { store, forget } = editor();
  await flushPromises();
  await wrapper!.get('[data-contact-field="emails"] .contact-edit-link').trigger("click");
  await wrapper!.get("[data-contact-input]").setValue("d@firma.example.test");
  await wrapper!.get("[data-contact-input]").trigger("keydown", { key: "Enter", ctrlKey: true });
  await vi.waitFor(() => expect(toast.success).toHaveBeenCalledWith("Kontakt upraven.", expect.objectContaining({ id: `contact-edit:${id}` })));
  const undo = vi.mocked(toast.success).mock.calls.at(-1)![1]!.action as unknown as { onClick: () => void };
  forget.mockClear();
  undo.onClick();
  await flushPromises();
  expect(store.edit).toHaveBeenLastCalledWith(id, { emails: ["denis@example.cz"] });
  expect(forget).toHaveBeenCalledWith(id);
});
