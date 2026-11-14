import { afterEach, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import UnsavedChangesDialog from "./UnsavedChangesDialog.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  document.body.replaceChildren();
});

function open(props: Record<string, unknown> = {}) {
  wrapper = mount(UnsavedChangesDialog, { attachTo: document.body, props: { open: true, ...props } });
  return wrapper;
}

it("shows the shared title and question with three distinct actions", () => {
  const view = open();
  expect(view.get(".app-dialog-header h2").text()).toBe("Neuložené změny");
  expect(view.text()).toContain("Máte rozepsané změny. Chcete je uložit, nebo zahodit?");
  const stay = view.get("button[data-variant=outline]");
  const discard = view.get("button[data-variant=destructive]");
  const save = view.get("button[data-variant=default]");
  expect(stay.text()).toBe("Zůstat");
  expect(discard.text()).toBe("Zahodit");
  expect(save.text()).toBe("Uložit");
});

it("emits stay, discard and save from their respective buttons", async () => {
  const view = open();
  await view.get("button[data-variant=outline]").trigger("click");
  expect(view.emitted("stay")).toHaveLength(1);
  await view.get("button[data-variant=destructive]").trigger("click");
  expect(view.emitted("discard")).toHaveLength(1);
  await view.get("button[data-variant=default]").trigger("click");
  expect(view.emitted("save")).toHaveLength(1);
});

it("treats Escape and the close icon as staying, not discarding", async () => {
  const view = open();
  await view.get("dialog").trigger("cancel");
  expect(view.emitted("stay")).toHaveLength(1);
  expect(view.emitted("discard")).toBeUndefined();
});

// U2-16: Enter saves (the expected answer), never the close icon; without saving, Enter stays.
it("starts on Uložit, or on Zůstat when the draft cannot be saved", async () => {
  const view = open();
  await vi.waitFor(() => expect(document.activeElement).toBe(view.get("button[data-variant=default]").element));
  view.unmount();
  const unsavable = open({ canSave: false });
  await vi.waitFor(() => expect(document.activeElement).toBe(unsavable.get("button[data-variant=outline]").element));
});

it("disables discard and save while saving, and labels the primary button", async () => {
  const view = open({ saving: true });
  expect(view.get("button[data-variant=destructive]").attributes("disabled")).toBeDefined();
  expect(view.get("button[data-variant=default]").attributes("disabled")).toBeDefined();
  expect(view.get("button[data-variant=default]").text()).toBe("Ukládání…");
});
