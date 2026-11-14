import { describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { toast } from "vue-sonner";
import OverviewActions from "./OverviewActions.vue";

vi.mock("vue-sonner", () => ({ toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn(), dismiss: vi.fn() } }));

describe("OverviewActions", () => {
  it("runs an action once while it is busy, keeps the focus on its button and ends its failure in a toast", async () => {
    let fail!: (error: Error) => void;
    const run = vi.fn(() => new Promise<void>((_resolve, reject) => { fail = reject; }));
    const wrapper = mount(OverviewActions, { props: { actions: [{ id: "compose", label: "Poskládat tabuli", run }] }, attachTo: document.body });
    const button = wrapper.get("[data-overview-action=compose]");
    (button.element as HTMLElement).focus();
    await button.trigger("click");
    await button.trigger("click");
    expect(run).toHaveBeenCalledTimes(1);
    expect(button.attributes()).toMatchObject({ "aria-disabled": "true", "aria-busy": "true" });
    expect(button.attributes("disabled")).toBeUndefined();
    expect(document.activeElement).toBe(button.element);
    fail(new Error("Stránka nejde načíst."));
    await flushPromises();
    expect(toast.error).toHaveBeenCalledWith("Poskládat tabuli: Stránka nejde načíst.", expect.anything());
    expect(button.attributes("disabled")).toBeUndefined();
  });
});
