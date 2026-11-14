import { expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import AiSourceList from "./AiSourceList.vue";
import { source } from "../dev/ai/aiSampleData";
it("leaves modified source clicks to native navigation instead of opening another tab twice", () => {
  const open = vi.fn();
  const wrapper = mount(AiSourceList, { props: { sources: [source(1)], onOpenSource: open } });
  const link = wrapper.get("a");
  const modified = new MouseEvent("click", { ctrlKey: true, bubbles: true, cancelable: true });
  link.element.dispatchEvent(modified);
  expect(open).not.toHaveBeenCalled();
  expect(modified.defaultPrevented).toBe(false);
  link.element.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true }));
  expect(open).toHaveBeenCalledTimes(1);
  wrapper.unmount();
});
