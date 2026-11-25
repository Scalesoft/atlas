import { mount, flushPromises } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import AssistantToolsInfo from "./AssistantToolsInfo.vue";

vi.mock("../../api", () => ({ api: vi.fn().mockRejectedValue(new Error("404")) }));

describe("AssistantToolsInfo", () => {
  it("shows the groups of the sample with read and write marked", async () => {
    const groups = [{ group: "zaklad", tools: [{ name: "atlas_search", description: "d", write: false }] }, { group: "stitky", tools: [{ name: "atlas_apply_labels", description: "d", write: true }] }];
    const wrapper = mount(AssistantToolsInfo, { props: { groups }, attachTo: document.body });
    await wrapper.get("[data-assistant-tools-info]").trigger("click");
    await flushPromises();
    const list = document.body.querySelector("[data-assistant-tools-list]")!;
    expect(list.querySelectorAll("li").length).toBe(2);
    expect(list.querySelector("li[data-write] code")?.textContent).toBe("apply_labels");
    wrapper.unmount();
  });
});
