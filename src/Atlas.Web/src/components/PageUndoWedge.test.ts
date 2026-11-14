import { expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import PageUndoWedge from "./PageUndoWedge.vue";

const wedge = (props: { editable: boolean; canUndo: boolean }) => mount(PageUndoWedge, { props, attachTo: document.body });

it("shows only while something can be undone on a page the reader may edit (a writer, not locked)", async () => {
  for (const [editable, canUndo, state] of [[true, true, "faint"], [true, false, "hidden"], [false, true, "hidden"], [false, false, "hidden"]] as const) {
    const wrapper = wedge({ editable, canUndo });
    const root = wrapper.get("[data-page-undo]");
    expect(root.attributes("data-state")).toBe(state);
    // Hidden, it takes neither the pointer nor the Tab key.
    expect(root.attributes("inert") !== undefined).toBe(state === "hidden");
    wrapper.unmount();
  }
});

it("fades back to hidden after the last undo", async () => {
  const wrapper = wedge({ editable: true, canUndo: true });
  await wrapper.setProps({ canUndo: false });
  expect(wrapper.get("[data-page-undo]").attributes("data-state")).toBe("hidden");
  wrapper.unmount();
});

it("undoes on a click without taking the editor's focus on the press", async () => {
  const wrapper = wedge({ editable: true, canUndo: true });
  const button = wrapper.get("button");
  expect(button.attributes("aria-label")).toBe("Zpět");
  const press = new MouseEvent("mousedown", { bubbles: true, cancelable: true });
  button.element.dispatchEvent(press);
  expect(press.defaultPrevented).toBe(true);
  await button.trigger("click");
  expect(wrapper.emitted("undo")).toHaveLength(1);
  wrapper.unmount();
});
