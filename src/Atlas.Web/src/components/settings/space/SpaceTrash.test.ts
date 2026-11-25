import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, type Component } from "vue";
import { provideSettingsContext, type SettingsContext } from "../context";
import { createDirtyGuard, provideDirtyGuard } from "../dirtyGuard";
import { createSettingsFlash, provideSettingsFlash } from "../flash";
import { provideSettingsRoute } from "../useSettingsRoute";
import SpaceGeneralPage from "./SpaceGeneralPage.vue";
import SpaceTrashPage from "../global/SpaceTrashPage.vue";

const settings = { id: "provoz", name: "Provoz", icon: "book-open", color: "blue", description: "", pinPolicy: "None", version: "v1", cover: null };
const trashed = { id: "provoz", name: "Provoz", icon: "server", color: "teal", trashedAt: "2026-10-27T10:00:00Z", trashedById: "u", trashedByName: "Správce", pageCount: 12, vaultNames: ["Servery a síť"] };
let wrapper: VueWrapper | undefined;
let context: SettingsContext;
let trashList = [trashed];
let calls: { method: string; path: string; body?: string }[];
let flash: ReturnType<typeof createSettingsFlash>;

function mountWith(component: Component, props: Record<string, unknown>, isWikiAdmin: boolean) {
  context = {
    userId: "u", isWikiAdmin, spaces: [], backupWarning: null, version: undefined, imported: vi.fn(), warning: vi.fn(), openPage: vi.fn(),
    newTemplate: vi.fn(), newPage: vi.fn(), spaceChanged: vi.fn(), spaceTrashed: vi.fn(), spacesChanged: vi.fn(), accessChanged: vi.fn(),
  };
  const Host = defineComponent({
    setup() {
      provideSettingsContext(context);
      provideDirtyGuard(createDirtyGuard());
      flash = createSettingsFlash(provideSettingsRoute("#/settings"));
      provideSettingsFlash(flash);
      return () => h(component, props);
    },
  });
  wrapper = mount(Host, { attachTo: document.body });
}
beforeEach(() => {
  calls = [];
  trashList = [trashed];
  vi.stubGlobal("fetch", vi.fn(async (input: string, init?: RequestInit) => {
    const path = String(input);
    calls.push({ method: init?.method ?? "GET", path, body: init?.body as string | undefined });
    if (path.endsWith("/auth/csrf")) return Response.json({ token: "t" });
    if (path.endsWith("/admin/trash-cleanup")) return Response.json({ scope: "space", automatic: false, days: 60, version: null });
    if (path.endsWith("/settings")) return Response.json(settings);
    if (path.endsWith("/space-trash")) return Response.json(trashList);
    if (path.endsWith("/trash") || path.endsWith("/restore") || path.endsWith("/purge")) return new Response(null, { status: 204 });
    return Response.json({ catalogKey: null });
  }));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); document.body.innerHTML = ""; });

describe("Přesunout prostor do koše in Obecné", () => {
  it("is offered to the wiki admin only", async () => {
    mountWith(SpaceGeneralPage, { spaceId: "provoz" }, false);
    await flushPromises();
    expect(document.querySelector("[data-space-trash]")).toBeNull();
  });
  it("moves the space to the trash once its name is typed and tells the shell", async () => {
    mountWith(SpaceGeneralPage, { spaceId: "provoz" }, true);
    await flushPromises();
    (document.querySelector("[data-space-trash-open]") as HTMLButtonElement).click();
    await flushPromises();
    const confirm = () => document.querySelector("[data-confirm-action]") as HTMLButtonElement;
    expect(confirm().disabled).toBe(true);
    const name = document.querySelector("[data-space-trash-name]") as HTMLInputElement;
    name.value = "provoz";
    name.dispatchEvent(new Event("input"));
    await flushPromises();
    expect(confirm().disabled).toBe(true);
    name.value = "Provoz";
    name.dispatchEvent(new Event("input"));
    await flushPromises();
    confirm().click();
    await flushPromises();
    expect(calls).toContainEqual({ method: "POST", path: "/api/spaces/provoz/trash", body: JSON.stringify({ name: "Provoz" }) });
    expect(context.spaceTrashed).toHaveBeenCalledWith("provoz");
  });
});

describe("Koš prostorů", () => {
  it("lists the trashed spaces and restores one, which the shell then reads again", async () => {
    mountWith(SpaceTrashPage, {}, true);
    await flushPromises();
    const row = document.querySelector('[data-trash-row="provoz"]')!;
    expect(row.textContent).toContain("12 stránek");
    expect(row.textContent).toContain("Správce");
    (row.querySelector("[data-trash-restore]") as HTMLButtonElement).click();
    await flushPromises();
    expect(calls.some((call) => call.method === "POST" && call.path === "/api/spaces/provoz/restore")).toBe(true);
    expect(context.spacesChanged).toHaveBeenCalled();
    expect(document.querySelector('[data-trash-row="provoz"]')).toBeNull();
    expect(flash.message.value?.text).toContain("Prostor Provoz je zpět");
  });

  it("deletes a space permanently only after its exact name is typed, naming its vaults", async () => {
    mountWith(SpaceTrashPage, {}, true);
    await flushPromises();
    (document.querySelector('[data-trash-row="provoz"] [data-trash-purge]') as HTMLButtonElement).click();
    await flushPromises();
    expect(document.querySelector("[data-space-trash-vaults]")?.textContent).toContain("Servery a síť");
    // SEC3-5: permanent here, not in the backups made before.
    expect(document.querySelector("[data-trash-backup-note]")?.textContent).toContain("dřívějších zálohách");
    const confirm = () => document.querySelector("[data-confirm-action]") as HTMLButtonElement;
    const name = document.querySelector("[data-trash-confirm-name]") as HTMLInputElement;
    expect(confirm().disabled).toBe(true);
    name.value = "provoz";
    name.dispatchEvent(new Event("input"));
    await flushPromises();
    expect(confirm().disabled).toBe(true);
    name.value = "Provoz";
    name.dispatchEvent(new Event("input"));
    await flushPromises();
    confirm().click();
    await flushPromises();
    expect(calls).toContainEqual({ method: "POST", path: "/api/spaces/provoz/purge", body: JSON.stringify({ name: "Provoz" }) });
    expect(document.querySelector('[data-trash-row="provoz"]')).toBeNull();
    expect(flash.message.value?.text).toContain("Prostor Provoz je trvale smazaný");
    // UX3-4: the last space gone, the focus lands on the cleanup rule, not on the page body.
    await vi.waitFor(() => expect(document.activeElement?.closest("[data-trash-cleanup-scope]")).not.toBeNull());
  });

  it("offers no permanent delete when the trash is empty, only its empty state", async () => {
    trashList = [];
    mountWith(SpaceTrashPage, {}, true);
    await flushPromises();
    expect(document.body.textContent).toContain("Koš prostorů je prázdný");
    expect(document.querySelector("[data-trash-purge]")).toBeNull();
  });

  it("tells same-named trashed spaces apart by order and the second of their move to the trash", async () => {
    trashList = [{ ...trashed, id: "a", trashedAt: "2026-10-27T10:00:07Z" }, { ...trashed, id: "b", trashedAt: "2026-10-27T10:00:41Z" }];
    mountWith(SpaceTrashPage, {}, true);
    await flushPromises();
    const rows = [...document.querySelectorAll("[data-trash-row]")].map(row => row.textContent);
    expect(rows[0]).toContain("1. z 2");
    expect(rows[1]).toContain("2. z 2");
    expect(rows[0]).toMatch(/:07/);
    expect(rows[1]).toMatch(/:41/);
  });
});
