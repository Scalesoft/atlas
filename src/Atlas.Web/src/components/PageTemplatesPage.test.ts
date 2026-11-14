import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import PageTemplatesPage from "./PageTemplatesPage.vue";
import { pageTemplateApi } from "../pageTemplateApi";

vi.mock("../pageTemplateApi", () => ({ pageTemplateApi: { list: vi.fn() } }));
const space = { id: "space-1", name: "Docs", icon: "book", color: "blue", role: "Admin" as const, pinPolicy: "None" as const, pinned: false, pageCount: 0, lastChangedAt: null };
const templates = [
  { id: "draft-1", spaceId: "space-1", parentId: null, title: "Draft checklist", version: 2, isPublished: false },
  { id: "published-1", spaceId: "space-1", parentId: null, title: "Runbook", version: 3, isPublished: true },
];
beforeEach(() => { vi.clearAllMocks(); vi.mocked(pageTemplateApi.list).mockResolvedValue(templates); });
describe("space template settings", () => {
  it("lists draft and published template pages and delegates opening to the parent", async () => {
    const wrapper = mount(PageTemplatesPage, { props: { space } });
    try {
      await flushPromises();
      expect(wrapper.text()).toContain("Draft checklist");
      expect(wrapper.text()).toContain("Runbook");
      await wrapper.get('[data-select-template="draft-1"]').trigger("click");
      expect(wrapper.emitted("select")).toEqual([["draft-1"]]);
      expect(wrapper.find(".template-authoring").exists()).toBe(false);
    } finally { wrapper.unmount(); }
  });
  it("delegates creation of a template page to the normal page flow", async () => {
    const wrapper = mount(PageTemplatesPage, { props: { space } });
    try {
      await flushPromises();
      await wrapper.get('[data-action="new-template"]').trigger("click");
      expect(wrapper.emitted("create")).toEqual([[]]);
    } finally { wrapper.unmount(); }
  });
  it("shows a retryable list error", async () => {
    vi.mocked(pageTemplateApi.list).mockRejectedValueOnce(new Error("Offline"));
    const wrapper = mount(PageTemplatesPage, { props: { space } });
    try {
      await flushPromises();
      expect(wrapper.get('[role="alert"]').text()).toContain("Offline");
      await wrapper.get(".error-state button").trigger("click");
      await flushPromises();
      expect(pageTemplateApi.list).toHaveBeenCalledTimes(2);
      expect(wrapper.text()).toContain("Runbook");
    } finally { wrapper.unmount(); }
  });
});
