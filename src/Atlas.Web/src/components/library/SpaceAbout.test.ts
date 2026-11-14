import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import type { PageSummary, Space } from "../../api";
import SpaceAbout from "./SpaceAbout.vue";

// Fixtures leave out `cover`: the head of the space shows the image, this tab does not.
const space: Space = {
  id: "space-1", name: "Inženýrství", icon: "book", color: "blue", role: "Reader", pinPolicy: "None", pinned: false,
  pageCount: 2, lastChangedAt: "2026-10-10T08:00:00Z", description: "Postupy a rozhodnutí vývojového týmu.",
};
const page = (id: string, isPublished = true): PageSummary => ({ id, spaceId: "space-1", parentId: null, title: `Stránka ${id}`, version: 1, isPublished });
const pages = [page("p-1"), page("p-2", false)];
const templates = [page("t-1"), { ...page("t-2", false), title: "Zápis z porady" }];
const text = (wrapper: ReturnType<typeof mount>, key: string) => wrapper.get(`[data-summary="${key}"]`).text();

describe("SpaceAbout", () => {
  it("shows the description and the facts of the header with the account's own access", () => {
    const wrapper = mount(SpaceAbout, { props: { space, pages } });
    expect(wrapper.get("[data-space-about]").classes()).toContain("space-about");
    expect(wrapper.get(".space-about-description").text()).toBe("Postupy a rozhodnutí vývojového týmu.");
    expect(wrapper.find(".space-description").exists()).toBe(false);
    expect(text(wrapper, "pages")).toContain("2 stránky");
    expect(text(wrapper, "drafts")).toContain("1 koncept");
    expect(wrapper.get('[data-summary="changed"] time').attributes("datetime")).toBe("2026-10-10T08:00:00Z");
    expect(text(wrapper, "role")).toContain("Váš přístup");
    expect(text(wrapper, "role")).toContain("Čte");

    // Facts that have nothing to say are left out rather than shown as zero or a dash.
    const quiet = mount(SpaceAbout, { props: { space: { ...space, description: undefined, lastChangedAt: null }, pages: [page("p-1")] } });
    expect(quiet.find(".space-about-description").exists()).toBe(false);
    expect(quiet.find('[data-summary="drafts"]').exists()).toBe(false);
    expect(quiet.find('[data-summary="changed"]').exists()).toBe(false);
    expect(text(quiet, "pages")).toContain("1 stránka");
  });

  it("lists the templates and opens one through the shell", async () => {
    const wrapper = mount(SpaceAbout, { props: { space, pages, templates } });
    expect(wrapper.text()).toContain("Zápis z porady");
    await wrapper.get('[data-open-template="t-2"]').trigger("click");
    expect(wrapper.emitted("openPage")).toEqual([["t-2"]]);
  });

  it("does not claim the space has no templates before the answer arrives", () => {
    const waiting = mount(SpaceAbout, { props: { space, pages, templatesLoading: true } });
    expect(waiting.text()).not.toContain("Prostor zatím nemá šablony");
    expect(waiting.find("[data-open-template]").exists()).toBe(false);
    const answered = mount(SpaceAbout, { props: { space, pages, templates: [] } });
    expect(answered.text()).toContain("Prostor zatím nemá šablony");
  });

  it("shows the administration block only to a manager, and nothing about other people's access to anyone", () => {
    const reader = mount(SpaceAbout, { props: { space, pages, templates } });
    expect(reader.find("[data-space-about-admin]").exists()).toBe(false);
    expect(reader.find("[data-space-about-link]").exists()).toBe(false);
    expect(reader.find('a[href*="settings"]').exists()).toBe(false);

    const manager = mount(SpaceAbout, { props: { space: { ...space, role: "Admin" }, pages, templates, canManage: true } });
    const links = manager.get("[data-space-about-admin]").findAll("a[data-space-about-link]");
    expect(manager.get("[data-space-about-admin]").text()).toContain("Správa prostoru");
    expect(links.map((link) => [link.attributes("href"), link.text()])).toEqual([
      ["#/spaces/space-1/settings/general", expect.stringContaining("Obecné")],
      ["#/spaces/space-1/settings/access", expect.stringContaining("Přístupy")],
      ["#/spaces/space-1/settings/sharing", expect.stringContaining("Sdílení odkazem")],
      ["#/spaces/space-1/settings/templates", expect.stringContaining("Šablony stránek")],
      ["#/spaces/space-1/settings/vaults", expect.stringContaining("Trezory")],
    ]);
  });

  it("shows a failed template listing inline in place of the section, with retry, and keeps the rest", async () => {
    const wrapper = mount(SpaceAbout, { props: { space, pages, templatesError: "Offline" } });
    expect(wrapper.get('[role="alert"]').text()).toContain("Šablony se nepodařilo načíst");
    expect(wrapper.get('[role="alert"]').text()).toContain("Offline");
    expect(wrapper.find("[data-open-template]").exists()).toBe(false);
    expect(wrapper.text()).not.toContain("Prostor zatím nemá šablony");
    expect(text(wrapper, "pages")).toContain("2 stránky");
    expect(wrapper.get(".space-about-description").text()).toContain("Postupy");
    await wrapper.get('[role="alert"] button').trigger("click");
    expect(wrapper.emitted("retryTemplates")).toEqual([[]]);
  });
});
