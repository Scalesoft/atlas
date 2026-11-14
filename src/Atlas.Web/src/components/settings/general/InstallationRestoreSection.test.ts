import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../api";
import InstallationRestoreSection from "./InstallationRestoreSection.vue";
import { fakeApi, mountPage, problem, type FakeCall } from "./testing";

let wrapper: VueWrapper | undefined;
let calls: FakeCall[] = [];
const preview = (extra: Record<string, unknown> = {}) => ({ mode: "content-only", keyId: null, empty: true, rows: { A: 40, B: 2 }, problems: [], warnings: [], message: "Obnoví obsah.", ...extra });

async function open(configured: boolean, handler: Parameters<typeof fakeApi>[0], source: "git" | "zip" = "git") {
  clearCsrf();
  calls = fakeApi(handler);
  wrapper = mountPage(InstallationRestoreSection, { source, configured }).wrapper;
  await flushPromises();
  return wrapper;
}
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
});

describe("restore from the Git backup", () => {
  it("does nothing until a repository is configured", async () => {
    const view = await open(false, () => problem(404, "unexpected"));
    expect(view.find("[data-restore-unconfigured]").exists()).toBe(true);
    // Progressive disclosure: no key field that looks usable, no check that cannot run.
    expect(view.find("[data-restore-key]").exists()).toBe(false);
    expect(view.find("[data-restore-check]").exists()).toBe(false);
  });

  it("restores only after the check and the typed word, sending the key", async () => {
    const view = await open(true, (method, path, body) => {
      if (path === "/admin/backups/git/restore/preview") return preview();
      if (path === "/admin/backups/git/restore") {
        expect(body).toEqual({ key: "K", acknowledge: true });
        return { mode: "content-only", rows: {}, warnings: [], message: "Obsah je obnovený." };
      }
      return problem(404, "unexpected");
    });
    await view.get("[data-restore-key]").setValue("K");
    await view.get("[data-restore-check]").trigger("click");
    await flushPromises();
    expect(view.get("[data-restore-preview]").text()).toContain("42");
    await view.get("[data-restore-run]").trigger("click");
    await flushPromises();
    const confirm = document.body.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-action]")!;
    expect(confirm.disabled).toBe(true);
    const word = document.body.querySelector<HTMLInputElement>("[data-restore-confirm-word]")!;
    word.value = "OBNOVIT";
    word.dispatchEvent(new Event("input"));
    await flushPromises();
    expect(confirm.disabled).toBe(false);
    confirm.click();
    await flushPromises();
    expect(calls.filter((call) => call.path === "/admin/backups/git/restore")).toHaveLength(1);
    expect(view.text()).toContain("Obsah je obnovený.");
    // The next step is offered, not only the server's text.
    expect(view.find("[data-restore-reload]").exists()).toBe(true);
  });

  it("offers no restore while the check lists problems", async () => {
    const view = await open(true, () => preview({ empty: false, problems: ["Obnova instalace potřebuje prázdnou instalaci."], message: "Zálohu nejde obnovit." }));
    await view.get("[data-restore-check]").trigger("click");
    await flushPromises();
    expect(view.get("[data-restore-problems]").text()).toContain("prázdnou instalaci");
    expect(view.get("[data-restore-run]").attributes("disabled")).toBeDefined();
  });

  it("shows a failed check", async () => {
    const view = await open(true, () => problem(400, "Neplatná záloha: x"));
    await view.get("[data-restore-check]").trigger("click");
    await flushPromises();
    expect(view.get("[data-restore-error]").text()).toContain("Neplatná záloha");
  });
});

describe("restore from a complete ZIP (GAP2-1)", () => {
  it("checks and restores the uploaded archive with its key, as the Git restore does", async () => {
    const zip = new File(["PK"], "atlas-complete.zip", { type: "application/zip" });
    const view = await open(false, (method, path, body) => {
      const form = body as FormData;
      if (path === "/admin/backups/restore/preview") {
        expect([form.get("file"), form.get("key"), form.get("acknowledge")]).toEqual([zip, "K", null]);
        return preview();
      }
      if (path === "/admin/backups/restore") {
        expect([form.get("file"), form.get("key"), form.get("acknowledge")]).toEqual([zip, "K", "true"]);
        return { mode: "content-only", rows: {}, warnings: [], message: "Obsah je obnovený." };
      }
      return problem(404, "unexpected");
    }, "zip");
    expect(view.get("[data-restore-check]").attributes("disabled")).toBeDefined();
    const input = view.get('input[type="file"]');
    Object.defineProperty(input.element, "files", { configurable: true, value: [zip] });
    await input.trigger("change");
    await view.get("[data-restore-key]").setValue("K");
    await view.get("[data-restore-check]").trigger("click");
    await flushPromises();
    expect(view.get("[data-restore-preview]").text()).toContain("42");
    await view.get("[data-restore-run]").trigger("click");
    await flushPromises();
    const word = document.body.querySelector<HTMLInputElement>("[data-restore-confirm-word]")!;
    word.value = "OBNOVIT";
    word.dispatchEvent(new Event("input"));
    await flushPromises();
    document.body.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-action]")!.click();
    await flushPromises();
    expect(calls.filter((call) => call.path === "/admin/backups/restore")).toHaveLength(1);
    expect(view.text()).toContain("Obsah je obnovený.");
  });
});
