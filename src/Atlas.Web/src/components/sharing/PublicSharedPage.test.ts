import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { openApiNode } from "@atlas/markdown/plugins/openapi";
import PublicSharedPage from "./PublicSharedPage.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; });

// The server projects shared blocks to `mode: "reference"`; the interactive case proves the
// client policy disables Try It even for a block that was not projected.
it.each([["reference" as const], [undefined]])("renders a shared OpenAPI block as documentation with download and no Try It (mode %s)", async (mode) => {
  const source = "openapi: 3.1.0\ninfo:\n  title: NIS API\n  version: '2.3'\npaths:\n  /patients:\n    get:\n      responses:\n        '200':\n          description: OK\n";
  const contentJson = JSON.stringify({ format: "atlas-rich", version: 1, plugins: [{ id: "openapi", version: 1 }], document: { type: "doc", content: [
    openApiNode({ version: 1, filename: "nis.yaml", source, ...(mode ? { mode } : {}) }),
  ] } });
  wrapper = mount(PublicSharedPage, { props: { state: { kind: "ready", value: {
    title: "Veřejné API", contentJson, revisionVersion: 2,
    publishedAt: "2026-09-13T09:00:00Z", expiresAt: "2026-09-20T09:00:00Z",
  } } }, attachTo: document.body });
  await vi.waitFor(() => expect(wrapper!.find(".md-openapi-card").exists()).toBe(true));
  expect(wrapper.get(".md-openapi-card h3").text()).toBe("NIS API");
  expect(wrapper.get(".md-openapi-actions .md-btn-primary").text()).toBe("Dokumentace");
  expect(wrapper.get(".md-openapi-actions .md-btn").text()).toContain("Stáhnout");
  expect(wrapper.text()).not.toContain("vyzkoušení");
  expect(wrapper.find('[role="alert"]').exists()).toBe(false);
});

it("renders sanitized shared content without editor, presence, or private commands", async () => {
  const contentJson = JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [
    { type: "paragraph", content: [{ type: "text", text: "Pevná publikovaná revize" }] },
  ] } });
  wrapper = mount(PublicSharedPage, { props: { state: { kind: "ready", value: {
    title: "Veřejný dokument", contentJson, revisionVersion: 4,
    publishedAt: "2026-09-13T09:00:00Z", expiresAt: "2026-09-20T09:00:00Z",
  } } }, attachTo: document.body });
  await flushPromises();
  expect(wrapper.get("h1").text()).toBe("Veřejný dokument");
  expect(wrapper.text()).toContain("Pevná publikovaná revize");
  expect(wrapper.find("[contenteditable=true]").exists()).toBe(false);
  expect(wrapper.find("[data-testid=presence-stack]").exists()).toBe(false);
  expect(wrapper.find("button").exists()).toBe(false);
});

it("offers retry for a failed public lookup, not for a link that is gone, without revealing why it is unavailable", async () => {
  wrapper = mount(PublicSharedPage, { props: { state: { kind: "error", missing: false } } });
  expect(wrapper.get("h1").text()).toBe("Odkaz není dostupný");
  await wrapper.get("button").trigger("click");
  expect(wrapper.emitted("retry")).toHaveLength(1);
  // Expired, revoked or never there (one 404 for all): permanent, so nothing to try again.
  await wrapper.setProps({ state: { kind: "error", missing: true } });
  expect(wrapper.get("[role=alert]").text()).toBe("Odkaz není dostupný. Mohl vypršet nebo být zrušen.");
  expect(wrapper.find("button").exists()).toBe(false);
});

it("renders safe published widgets with the same static modules as revision history", async () => {
  const contentJson = JSON.stringify({ format: "atlas-rich", version: 1, plugins: [
    { id: "contacts", version: 2 }, { id: "math", version: 1 }, { id: "diagrams", version: 2 },
  ], document: { type: "doc", content: [
    { type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Jana Nováková", emails: ["jana@example.cz"] }] } } },
    { type: "mathBlock", attrs: { data: { version: 1, expression: "E = mc^2" } } },
    { type: "diagram", attrs: { data: { version: 1, source: "graph LR\n A-->B", caption: "Bezpečný tok" } } },
  ] } });
  wrapper = mount(PublicSharedPage, { props: { state: { kind: "ready", value: {
    title: "Veřejný dokument", contentJson, revisionVersion: 4,
    publishedAt: "2026-09-13T09:00:00Z", expiresAt: "2026-09-20T09:00:00Z",
  } } }, attachTo: document.body });
  await flushPromises();
  await expect.poll(() => wrapper!.findAll(".md-contact-card, .md-diagram, math").length).toBeGreaterThanOrEqual(3);
  expect(wrapper.text()).toContain("Jana Nováková");
  expect(wrapper.text()).toContain("Bezpečný tok");
  expect(wrapper.find('[role="alert"]').exists()).toBe(false);
  expect(wrapper.find('[contenteditable="true"]').exists()).toBe(false);
  // First load of the math and diagram renderers takes 15-30 s while every CPU runs a test file.
}, 90000);
