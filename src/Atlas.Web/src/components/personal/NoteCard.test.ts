import { afterEach, expect, it } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import NoteCard from "./NoteCard.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

// A field that says it is a button opens with Enter and with Space, and Space does not scroll the page (review 3.5.1, finding 13).
it.each([["Enter", "title"], [" ", "text"]] as const)("opens a field for editing with %j", async (key, field) => {
  wrapper = mount(NoteCard, { attachTo: document.body, props: { note: { title: "Porada", text: "Body" }, editable: true } });
  const target = wrapper.get(`[data-note-field="${field}"]`);
  expect(target.attributes("role")).toBe("button");
  const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true });
  target.element.dispatchEvent(event);
  await wrapper.vm.$nextTick();
  expect(target.classes()).toContain("is-editing");
  if (key === " ") expect(event.defaultPrevented).toBe(true);
});
