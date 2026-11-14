import { afterEach, expect, it, vi } from "vitest";
import { flushPromises } from "@vue/test-utils";
import type { App } from "vue";
// Load the real graph once in module setup, outside each entry scenario. The
// gallery shell stays real; resetting every Vue/editor dependency between entries made
// the cold import consume the test budget and left a late mount in the next test.
import "./components/dev/ComponentGallery.vue";

// Each URL executes the actual entry again while retaining its shared dependencies.
const entry = "./main.ts";
const boot = (scenario: string) => import(`${entry}?gallery-test=${scenario}`);

vi.mock("./App.vue", () => ({ default: { template: "<p>Wiki application</p>" } }));
// This seam tests entry routing. AiGalleryPage.test mounts every real AI example,
// checks its content and backend isolation, and exercises motion-switch replay.
vi.mock("./components/dev/AiGalleryPage.vue", () => ({ default: { template: '<h2 data-ai-gallery-route>AI gallery route target</h2>' } }));

afterEach(async () => {
  await vi.dynamicImportSettled();
  (document.querySelector("#app") as HTMLElement & { __vue_app__?: App })?.__vue_app__?.unmount();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
  vi.unstubAllEnvs();
});

it("opens the component gallery explicitly in development without mounting the wiki", async () => {
  vi.stubEnv("DEV", true);
  history.replaceState(null, "", "/?gallery=1");
  document.body.innerHTML = '<div id="app"></div>';
  await boot("development-gallery");
  await vi.dynamicImportSettled();
  await flushPromises();
  expect(document.body.textContent).toContain("Galerie komponent");
  expect(document.body.textContent).not.toContain("Wiki application");
  location.hash = "#/gallery/catalog";
  window.dispatchEvent(new HashChangeEvent("hashchange"));
  await flushPromises();
  const sourceButton = [...document.querySelectorAll<HTMLButtonElement>("button")].find(button => button.textContent === "AppDialog.vue");
  expect(sourceButton).toBeDefined();
  sourceButton!.click();
  await vi.dynamicImportSettled();
  await flushPromises();
  expect(document.querySelector(".gallery-catalog-source code")?.textContent).toContain('class="app-dialog"');
}, 30_000);

it("opens the AI and suggestions page of the gallery", async () => {
  vi.stubEnv("DEV", true);
  history.replaceState(null, "", "/?gallery=1#/gallery/ai");
  document.body.innerHTML = '<div id="app"></div>';
  await boot("ai-gallery");
  await vi.dynamicImportSettled();
  await flushPromises();
  expect(document.querySelector(".gallery-content [data-ai-gallery-route]")?.textContent).toBe("AI gallery route target");
}, 30_000);

it("ignores the gallery parameter in production", async () => {
  vi.stubEnv("DEV", false);
  history.replaceState(null, "", "/?gallery=1");
  document.body.innerHTML = '<div id="app"></div>';
  await boot("production");
  await flushPromises();
  expect(document.body.textContent).toContain("Wiki application");
  expect(document.body.textContent).not.toContain("Galerie komponent");
});
