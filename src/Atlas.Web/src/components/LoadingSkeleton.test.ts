import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import LoadingSkeleton from "./LoadingSkeleton.vue";
import { dialogSkeleton, formSkeleton } from "./loadingPlaceholders";

describe("LoadingSkeleton", () => {
  it("passes style and attributes to its root and skips the fade-in when instant", () => {
    const wrapper = mount(LoadingSkeleton, { props: { instant: true }, attrs: { style: "margin-top: 3px", "data-x": "1" } });
    expect(wrapper.classes()).toContain("instant");
    expect(wrapper.attributes("data-x")).toBe("1");
    expect((wrapper.element as HTMLElement).style.marginTop).toBe("3px");
    expect(mount(LoadingSkeleton).classes()).not.toContain("instant");
  });

  it("renders a lazy dialog's placeholder inside the open dialog with its title", () => {
    const wrapper = mount(dialogSkeleton("Historie stránky", "side"), { attachTo: document.body });
    const dialog = document.querySelector("dialog");
    expect(dialog?.hasAttribute("open") || dialog?.getAttribute("data-open")).toBeTruthy();
    expect(dialog?.textContent).toContain("Historie stránky");
    expect(dialog?.querySelector(".loading-skeleton")).not.toBeNull();
    wrapper.unmount();
  });

  it("loads a form in the column of the finished form, several rows tall", () => {
    const wrapper = mount(formSkeleton);
    expect((wrapper.element as HTMLElement).style.maxWidth).toBe("1080px");
    expect(wrapper.findAll(".loading-skeleton-row")).toHaveLength(5);
  });
});
