import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { openApiNode } from "@atlas/markdown/plugins/openapi";
import HistoryRevisionPreview from "./HistoryRevisionPreview.vue";

let wrapper: VueWrapper | undefined;
const contentJson = JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Historický obsah" }] }] } });
afterEach(() => { wrapper?.unmount(); wrapper = undefined; });

describe("static history renderer", () => {
  it("renders a saved OpenAPI block as documentation only, without Try It", async () => {
    const source = "openapi: 3.1.0\ninfo:\n  title: NIS API\n  version: '2.3'\npaths:\n  /patients:\n    get:\n      responses:\n        '200':\n          description: OK\n";
    // A block without `mode` is interactive for a standalone editor; Atlas's policy still disables requests.
    const rich = { format: "atlas-rich", version: 1, plugins: [{ id: "openapi", version: 1 }], document: { type: "doc", content: [openApiNode({ version: 1, filename: "nis.yaml", source })] } };
    wrapper = mount(HistoryRevisionPreview, { props: { contentJson: JSON.stringify(rich), dark: false, accent: "blue" }, attachTo: document.body });
    await vi.waitFor(() => expect(wrapper!.find(".md-openapi-card").exists()).toBe(true));
    expect(wrapper.get(".md-openapi-card h3").text()).toBe("NIS API");
    expect(wrapper.get(".md-openapi-actions .md-btn-primary").text()).toBe("Dokumentace");
    expect(wrapper.text()).not.toContain("vyzkoušení");
    expect(wrapper.find('[role="alert"]').exists()).toBe(false);
    expect(wrapper.find('[contenteditable="true"]').exists()).toBe(false);
  });
  it("renders saved contacts with the same widgets as the editor", async () => {
    const rich = { format: "atlas-rich", version: 1, plugins: [{ id: "contacts", version: 2 }], document: { type: "doc", content: [{ type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Jana Nováková", emails: ["jana@example.cz"] }] } } }] } };
    wrapper = mount(HistoryRevisionPreview, { props: { contentJson: JSON.stringify(rich), dark: false, accent: "blue" }, attachTo: document.body });
    await vi.waitFor(() => expect(wrapper!.text()).toContain("Jana Nováková"));
    expect(wrapper.find('[role="alert"]').exists()).toBe(false);
    expect(wrapper.find('[contenteditable="true"]').exists()).toBe(false);
  });
  it("draws a page's contacts overview at the page's level, as its reader sees it", async () => {
    const card = { type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Jana Nováková", emails: ["jana@example.cz"] }] } } };
    const rich = { format: "atlas-rich", version: 1, plugins: [{ id: "contacts", version: 2 }, { id: "atlas-requirements", version: 2 }], document: { type: "doc", content: [{ type: "overview", attrs: { kind: "contacts" } }, card] } };
    wrapper = mount(HistoryRevisionPreview, { props: { contentJson: JSON.stringify(rich), dark: false, accent: "blue", pageId: "p-1" }, attachTo: document.body });
    await vi.waitFor(() => expect(wrapper!.find(".overview-read-card").exists()).toBe(true));
    await vi.waitFor(() => expect(wrapper!.get(".overview-read-card").text()).toContain("Jana Nováková"));
    expect(wrapper.find("[data-overview-contacts]").exists()).toBe(true);
  });
  it("preserves attachment metadata and renders a cookie-protected download without editor controls", async () => {
    const rich = { format: "atlas-rich", version: 1, plugins: [{id:"attachments",version:1}], document: {type:"doc",content:[{type:"attachment",attrs:{data:{version:1,name:"Release notes.txt",url:"/api/attachments/fixture/download",size:123,mimeType:"text/plain"}}}] } };
    wrapper=mount(HistoryRevisionPreview,{props:{contentJson:JSON.stringify(rich),dark:false,accent:"blue"},attachTo:document.body}); await flushPromises();
    await vi.waitFor(() => expect(wrapper!.html()).toContain('md-attachment-download'));
    expect(wrapper.text()).toContain("Release notes.txt"); expect(wrapper.get('a[download]').attributes("href")).toBe("/api/attachments/fixture/download"); expect(wrapper.find('[contenteditable="true"]').exists()).toBe(false); expect(wrapper.find("button").exists()).toBe(false); expect(wrapper.find('[role="alert"]').exists()).toBe(false);
  });
  it("renders the actual rich document without an editable surface or commands", async () => {
    wrapper = mount(HistoryRevisionPreview, { props: { contentJson, dark: true, accent: "blue" } });
    await flushPromises();
    expect(wrapper.text()).toContain("Historický obsah");
    expect(wrapper.get('[role="document"]').attributes("aria-label")).toBe("Náhled historické verze pouze pro čtení");
    expect(wrapper.find('[contenteditable="true"]').exists()).toBe(false);
    expect(wrapper.find("button").exists()).toBe(false);
    expect(wrapper.get('[role="document"]').classes()).toContain("md-theme-midnight");
  });
  it("reports invalid rich data without silently replacing it with a lossy fallback", async () => {
    wrapper = mount(HistoryRevisionPreview, { props: { contentJson: '{"format":"unknown"}', dark: false, accent: "green" } });
    expect(wrapper.get('[role="alert"]').text()).toContain("Uložená data zůstávají zachovaná");
    expect(wrapper.find('[role="document"]').exists()).toBe(false);
    await wrapper.setProps({ contentJson }); await flushPromises();
    expect(wrapper.text()).toContain("Historický obsah");
    expect(wrapper.find('[role="alert"]').exists()).toBe(false);
  });
});
