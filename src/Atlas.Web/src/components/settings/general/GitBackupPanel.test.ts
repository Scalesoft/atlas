import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../api";
import BackupsPage from "./BackupsPage.vue";
import type { BackupState } from "./backups";
import type { GitBackupStatus } from "./gitBackup";
import { fakeApi, mountPage, problem, type FakeCall, type FakeHandler } from "./testing";

// Zálohy → Git: the panel's form, and the git card of the overview with its state and Synchronizovat teď.

const remoteUrl = "ssh://git@git.example.test:2222/Infrastructure/atlas-backup.git";
const publicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGl0IGlzIGEgdGVzdCBrZXk atlas@wiki.example.test";
const sha = "4f2c9e1ab37d5c60e8f1a2b3c4d5e6f708192a3b";
function response(extra: Partial<GitBackupStatus> = {}, settings: Partial<GitBackupStatus["settings"]> = {}): GitBackupStatus {
  return {
    settings: { enabled: true, remoteUrl, branch: "main", publicKey, hostKeys: ["ssh-ed25519 SHA256:q1w2e3r4t5y6u7i8o9p0"], ...settings },
    running: false,
    progress: null,
    pendingPages: 0,
    lastSyncAt: "2026-09-29T10:00:00Z",
    lastCommit: sha,
    lastError: null,
    lastErrorAt: null,
    warning: null,
    ...extra,
  };
}

/** The archives beside it: healthy, so the shell's badge follows the git backup alone. */
const archives: BackupState = {
  settings: { enabled: true, intervalMinutes: 1440, mode: "full", destination: "/var/lib/atlas/backups", username: "", domain: "", hasPassword: false, retentionCount: 10 },
  runs: [],
  nextRunAt: "2026-09-30T02:00:00Z",
  lastSuccessAt: "2026-09-29T02:00:00Z",
  warning: null,
};

let wrapper: VueWrapper | undefined;
let calls: FakeCall[] = [];
function api(extra?: FakeHandler, state: () => GitBackupStatus = () => response()) {
  calls = fakeApi((method, path, body) => {
    const custom = extra?.(method, path, body);
    if (custom !== undefined) return custom;
    if (path === "/admin/backups/git" && method === "GET") return state();
    if (path === "/admin/backups" && method === "GET") return archives;
    return problem(404, `unexpected ${method} ${path}`);
  });
}
/** Holds the answer to `method path` until the returned release is called (a run the server is still doing). */
function hold(path: string, method = "POST") {
  const inner = globalThis.fetch;
  let release!: () => void;
  const gate = new Promise<void>((resolve) => (release = resolve));
  vi.stubGlobal("fetch", async (input: string, init?: RequestInit) => {
    if (new URL(String(input), "http://atlas.test").pathname === `/api${path}` && (init?.method ?? "GET") === method) await gate;
    return inner(input, init);
  });
  return release;
}
async function open() {
  const mounted = mountPage(BackupsPage, { part: "git" }, { hash: "#/settings/general/backups/git" });
  wrapper = mounted.wrapper;
  await flushPromises();
  return mounted;
}
const input = (view: VueWrapper, selector: string) => view.get(selector).element as HTMLInputElement;
const disabled = (view: VueWrapper, selector: string) => view.get(selector).attributes("disabled") !== undefined;
const confirmOpen = () => !!document.body.querySelector<HTMLDialogElement>("dialog.app-dialog")?.open;

beforeEach(() => clearCsrf());
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  vi.useRealTimers();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

describe("git backup state", () => {
  it("shows the state as tiles, the key to copy, the trusted server key and the saved form, and reports the warning", async () => {
    api(undefined, () => response({ warning: "Záloha do Gitu je pozadu." }));
    const { wrapper: view, context, guard } = await open();
    expect(view.get('[data-tab="git"]').attributes("aria-current")).toBe("page");
    expect(view.get("[data-git-status]").attributes("data-state")).toBe("ok");
    expect(view.get("[data-git-last-commit]").text()).toBe("4f2c9e1");
    expect(view.get('[data-stat="pending"]').text()).toContain("0 stránek");
    expect(view.get("[data-git-public-key]").element.textContent).toBe(publicKey);
    expect(view.text()).toContain("(Deploy klíče) a povolte zápis");
    expect(view.get("[data-git-host-keys]").text()).toContain("SHA256:q1w2e3r4t5y6u7i8o9p0");
    expect(view.get("[data-git-generate-key]").text()).toBe("Vygenerovat nový klíč");
    expect(input(view, "[data-git-remote-url]").value).toBe(remoteUrl);
    expect(input(view, "[data-git-branch]").value).toBe("main");
    expect(view.get("[data-git-enabled]").attributes("data-state")).toBe("checked");
    expect(view.get("[data-git-security-note]").text()).toContain("včetně konceptů, soukromých poznámek, stránek s omezeným přístupem");
    expect(context.warning).toHaveBeenCalledWith("Záloha do Gitu je pozadu.");
    expect(guard.dirty.value).toBe(false);
    expect(disabled(view, "[data-git-sync]")).toBe(false);
  });

  it("reads a failure that retries by itself, a run with its progress, unwritten pages and a disabled backup", async () => {
    let current = response({ lastError: "Push byl odmítnut: větev je chráněná.", lastErrorAt: "2026-09-29T10:05:00Z", pendingPages: 3 });
    api(undefined, () => current);
    const { wrapper: view } = await open();
    expect(view.get("[data-git-status]").attributes("data-state")).toBe("failed");
    expect(view.get("[data-git-error]").text()).toBe("Push byl odmítnut: větev je chráněná.");
    expect(view.get("[data-git-error]").attributes("role")).toBe("alert");
    expect(view.get("[data-git-status]").text()).toContain("Další pokus");
    expect(view.get('[data-stat="pending"]').text()).toContain("3 stránky");

    const cases: [GitBackupStatus, string, string][] = [
      [response({ running: true, progress: { done: 12, total: 40 } }), "running", "Zapsáno 12 z 40."],
      [response({ pendingPages: 1 }), "pending", "Publikace se zapíše hned, ostatní změny do 10 minut."],
      [response({ lastSyncAt: null, lastCommit: null }), "pending", "První synchronizace začíná."],
      [response({}, { enabled: false }), "disabled", "Zálohu zapněte a uložte."],
    ];
    for (const [next, state, text] of cases) {
      current = next;
      window.dispatchEvent(new Event("focus"));
      await flushPromises();
      expect(view.get("[data-git-status]").attributes("data-state")).toBe(state);
      expect(view.get("[data-git-status]").text()).toContain(text);
      expect(view.find("[data-git-error]").exists()).toBe(false);
    }
    expect(disabled(view, "[data-git-sync]")).toBe(true);
  });

  it("clears the shell's badge once a sync repairs the backup", async () => {
    let current = response({ lastError: "Push byl odmítnut.", warning: "Záloha do Gitu selhala: Push byl odmítnut." });
    api((method, path) => (path === "/admin/backups/git/sync" ? (current = response()) : undefined), () => current);
    const { wrapper: view, context } = await open();
    expect(context.warning).toHaveBeenLastCalledWith("Záloha do Gitu selhala: Push byl odmítnut.");
    await view.get("[data-git-sync]").trigger("click");
    await flushPromises();
    expect(view.get("[data-git-status]").attributes("data-state")).toBe("ok");
    expect(context.warning).toHaveBeenLastCalledWith(null);
    // The archive warning, which also carries the git one, is read again after the sync.
    expect(calls.filter((call) => call.path === "/admin/backups" && call.method === "GET")).toHaveLength(2);
  });

  it("refreshes on focus without replacing the draft, and never polls", async () => {
    vi.useFakeTimers();
    let refreshed = false;
    api(undefined, () => (refreshed ? response({ warning: "Synchronizace selhává.", pendingPages: 2 }, { hostKeys: ["ssh-ed25519 SHA256:novy"], remoteUrl: "ssh://git@jinde/repo.git" }) : response()));
    const { wrapper: view, context } = await open();
    await view.get("[data-git-remote-url]").setValue("git@draft.example.test:atlas/backup.git");
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(calls.filter((call) => call.path === "/admin/backups/git")).toHaveLength(1);
    refreshed = true;
    window.dispatchEvent(new Event("focus"));
    await flushPromises();
    expect(input(view, "[data-git-remote-url]").value).toBe("git@draft.example.test:atlas/backup.git");
    expect(view.get('[data-stat="pending"]').text()).toContain("2 stránky");
    expect(view.get("[data-git-host-keys]").text()).toContain("SHA256:novy");
    expect(context.warning).toHaveBeenLastCalledWith("Synchronizace selhává.");
  });
});

describe("git backup deploy key", () => {
  it("generates the first key at once and shows it to copy", async () => {
    api(
      (method, path) => (path === "/admin/backups/git/key" ? response({ lastSyncAt: null, lastCommit: null }, { enabled: false, remoteUrl: "", hostKeys: [] }) : undefined),
      () => response({ lastSyncAt: null, lastCommit: null }, { enabled: false, remoteUrl: "", publicKey: null, hostKeys: [] }),
    );
    const { wrapper: view, flash } = await open();
    expect(view.find("[data-git-public-key]").exists()).toBe(false);
    expect(view.find("[data-git-host-keys]").exists()).toBe(false);
    expect(disabled(view, "[data-git-test]")).toBe(true);
    expect(view.get("[data-git-generate-key]").text()).toBe("Vygenerovat klíč");
    await view.get("[data-git-generate-key]").trigger("click");
    await flushPromises();
    expect(confirmOpen()).toBe(false);
    expect(calls.filter((call) => call.path === "/admin/backups/git/key")).toEqual([{ method: "POST", path: "/admin/backups/git/key", body: undefined }]);
    expect(view.get("[data-git-public-key]").element.textContent).toBe(publicKey);
    expect(flash().message.value?.text).toContain("Vložte ho do repozitáře na Git serveru");
  });

  it("replaces a key only after a ConfirmDialog that says the old deploy key stops working", async () => {
    const replaced = "ssh-ed25519 AAAAnew atlas@wiki.example.test";
    api((method, path) => (path === "/admin/backups/git/key" ? response({}, { publicKey: replaced }) : undefined));
    const { wrapper: view } = await open();
    await view.get("[data-git-generate-key]").trigger("click");
    await flushPromises();
    expect(calls.some((call) => call.path === "/admin/backups/git/key")).toBe(false);
    expect(confirmOpen()).toBe(true);
    expect(document.body.textContent).toContain("Starý deploy klíč přestane fungovat");
    document.body.querySelector<HTMLButtonElement>("[data-confirm-cancel]")!.click();
    await flushPromises();
    expect(calls.some((call) => call.path === "/admin/backups/git/key")).toBe(false);
    await view.get("[data-git-generate-key]").trigger("click");
    await flushPromises();
    document.body.querySelector<HTMLButtonElement>("[data-confirm-action]")!.click();
    await flushPromises();
    expect(calls.filter((call) => call.path === "/admin/backups/git/key")).toHaveLength(1);
    expect(view.get("[data-git-public-key]").element.textContent).toBe(replaced);
    expect(confirmOpen()).toBe(false);
  });
});

describe("git backup repository", () => {
  it("tests the unsaved address and branch, shows the result and then the server key it trusted", async () => {
    let tested = false;
    api(
      (method, path) => {
        if (path !== "/admin/backups/git/test") return undefined;
        tested = true;
        return { message: "Čtení i zápis fungují." };
      },
      () => response({}, { hostKeys: tested ? ["ssh-ed25519 SHA256:prvni-spojeni"] : [] }),
    );
    const { wrapper: view } = await open();
    await view.get("[data-git-remote-url]").setValue(" git@git.example.test:Infrastructure/novy.git ");
    await view.get("[data-git-branch]").setValue("zalohy");
    await view.get("[data-git-test]").trigger("click");
    await flushPromises();
    expect(calls.find((call) => call.path === "/admin/backups/git/test")!.body).toEqual({ remoteUrl: "git@git.example.test:Infrastructure/novy.git", branch: "zalohy" });
    expect(view.get("[data-git-test-result]").attributes("data-ok")).toBe("true");
    expect(view.get("[data-git-test-result]").text()).toBe("Čtení i zápis fungují.");
    expect(view.get("[data-git-host-keys]").text()).toContain("SHA256:prvni-spojeni");
    expect(input(view, "[data-git-remote-url]").value).toBe(" git@git.example.test:Infrastructure/novy.git ");
    // A result belongs to the address it was run with.
    await view.get("[data-git-branch]").setValue("jina");
    expect(view.find("[data-git-test-result]").exists()).toBe(false);
  });

  it("shows a failed test as a danger result with the server's message", async () => {
    api((method, path) => (path === "/admin/backups/git/test" ? problem(400, "Server odmítl klíč (Permission denied).") : undefined));
    const { wrapper: view } = await open();
    await view.get("[data-git-test]").trigger("click");
    await flushPromises();
    expect(view.get("[data-git-test-result]").attributes("data-ok")).toBe("false");
    expect(view.get("[data-git-test-result]").attributes("data-tone")).toBe("danger");
    expect(view.get("[data-git-test-result]").text()).toContain("Permission denied");
  });

  it("saves the draft with the SaveBar, keeps the form read-only while saving and blocks sync while dirty", async () => {
    api(
      (method, path) => (path === "/admin/backups/git" && method === "PUT" ? response({ pendingPages: 5, lastSyncAt: null, lastCommit: null }, { remoteUrl: "git@git.example.test:atlas.git", branch: "zalohy" }) : undefined),
      () => response({ lastSyncAt: null, lastCommit: null }, { enabled: false, remoteUrl: "", hostKeys: [] }),
    );
    const { wrapper: view, guard } = await open();
    expect(input(view, "[data-git-branch]").value).toBe("main");
    await view.get("[data-git-remote-url]").setValue(" git@git.example.test:atlas.git ");
    await view.get("[data-git-branch]").setValue("zalohy");
    await view.get("[data-git-enabled]").trigger("click");
    expect(guard.dirty.value).toBe(true);
    expect(disabled(view, "[data-git-sync]")).toBe(true);
    expect(view.get("[data-git-sync]").attributes("title")).toBe("Před synchronizací uložte změny.");
    const release = hold("/admin/backups/git", "PUT");
    await view.get("[data-git-save] [data-save-bar-save]").trigger("click");
    await flushPromises();
    for (const selector of ["[data-git-remote-url]", "[data-git-branch]", "[data-git-enabled]", "[data-git-test]", "[data-git-generate-key]"]) expect(disabled(view, selector), selector).toBe(true);
    release();
    await flushPromises();
    expect(calls.find((call) => call.method === "PUT")!.body).toEqual({ enabled: true, remoteUrl: "git@git.example.test:atlas.git", branch: "zalohy" });
    expect(guard.dirty.value).toBe(false);
    expect(view.get("[data-git-status]").attributes("data-state")).toBe("pending");
    expect(disabled(view, "[data-git-sync]")).toBe(false);
    expect(disabled(view, "[data-git-remote-url]")).toBe(false);
  });

  it("asks for a fresh sign-in when the server refuses a new repository, and keeps the draft (SEC1-2)", async () => {
    api((method, path) => (path === "/admin/backups/git" && method === "PUT" ? problem(403, "Potvrďte změnu novým přihlášením.", "reauth_required") : undefined));
    const { wrapper: view, guard } = await open();
    await view.get("[data-git-remote-url]").setValue("git@git.example.test:jinam/kopie.git");
    await view.get("[data-git-save] [data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(document.body.querySelector("[data-reauth-dialog]")).not.toBeNull();
    expect(document.body.querySelector("[data-backup-destination-reauth]")!.textContent).toContain("soukromých poznámek");
    expect(input(view, "[data-git-remote-url]").value).toBe("git@git.example.test:jinam/kopie.git");
    expect(guard.dirty.value).toBe(true);
    expect(calls.filter((call) => call.method === "PUT")).toHaveLength(1);
  });
  it("refuses to save a backup that is on without an address, or with an HTTPS one, next to the field", async () => {
    api(undefined, () => response({}, { enabled: false, remoteUrl: "" }));
    const { wrapper: view, guard } = await open();
    await view.get("[data-git-enabled]").trigger("click");
    await view.get("[data-git-save] [data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(view.text()).toContain("Zadejte adresu repozitáře.");
    await view.get("[data-git-remote-url]").setValue("https://git.example.test/atlas.git");
    await view.get("[data-git-save] [data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(view.text()).toContain("HTTPS se nepodporuje");
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
    await view.get("[data-git-save] [data-save-bar-cancel]").trigger("click");
    await flushPromises();
    expect(input(view, "[data-git-remote-url]").value).toBe("");
    expect(guard.dirty.value).toBe(false);
  });

  it("keeps the draft and says why after a failed save", async () => {
    api((method, path) => (path === "/admin/backups/git" && method === "PUT" ? problem(400, "Adresa repozitáře musí používat ssh nebo file.") : undefined));
    const { wrapper: view, guard } = await open();
    await view.get("[data-git-remote-url]").setValue("ext::sh -c touch /tmp/x");
    await view.get("[data-git-save] [data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(view.get(".save-bar").text()).toContain("musí používat ssh nebo file");
    expect(input(view, "[data-git-remote-url]").value).toBe("ext::sh -c touch /tmp/x");
    expect(guard.dirty.value).toBe(true);
  });
});

describe("git backup sync", () => {
  it("shows the run while the call is open and the returned state when it is done", async () => {
    api((method, path) => (path === "/admin/backups/git/sync" ? response({ lastSyncAt: "2026-09-29T11:00:00Z", lastCommit: "abcdef0123456789" }) : undefined), () => response({ pendingPages: 2 }));
    const { wrapper: view, flash } = await open();
    expect(view.get("[data-git-status]").attributes("data-state")).toBe("pending");
    const release = hold("/admin/backups/git/sync");
    await view.get("[data-git-sync]").trigger("click");
    await flushPromises();
    expect(view.get("[data-git-status]").attributes("data-state")).toBe("running");
    expect(view.get("[data-git-sync]").attributes("aria-busy")).toBe("true");
    expect(disabled(view, "[data-git-sync]")).toBe(true);
    release();
    await flushPromises();
    expect(calls.filter((call) => call.path === "/admin/backups/git/sync")).toEqual([{ method: "POST", path: "/admin/backups/git/sync", body: undefined }]);
    expect(view.get("[data-git-status]").attributes("data-state")).toBe("ok");
    expect(view.get("[data-git-last-commit]").text()).toBe("abcdef0");
    expect(flash().message.value?.text).toContain("abcdef0");
  });

  it("shows a failed run from the returned state and a refused call inline", async () => {
    let refuse = false;
    api((method, path) => {
      if (path !== "/admin/backups/git/sync") return undefined;
      return refuse ? problem(409, "Synchronizace už běží.") : response({ lastError: "Rebase skončil konfliktem.", warning: "Záloha do Gitu selhala." });
    });
    const { wrapper: view, context, flash } = await open();
    await view.get("[data-git-sync]").trigger("click");
    await flushPromises();
    expect(view.get("[data-git-status]").attributes("data-state")).toBe("failed");
    expect(view.get("[data-git-error]").text()).toBe("Rebase skončil konfliktem.");
    expect(context.warning).toHaveBeenLastCalledWith("Záloha do Gitu selhala.");
    expect(flash().message.value).toBeNull();
    refuse = true;
    await view.get("[data-git-sync]").trigger("click");
    await flushPromises();
    expect(view.get("[data-git-action-error]").text()).toBe("Synchronizace už běží.");
    expect(view.get("[data-git-action-error]").attributes("role")).toBe("alert");
  });

  it("cannot sync or test without a key", async () => {
    api(undefined, () => response({}, { publicKey: null }));
    const { wrapper: view } = await open();
    expect(disabled(view, "[data-git-sync]")).toBe(true);
    expect(view.get("[data-git-sync]").attributes("title")).toBe("Nejprve vygenerujte deploy klíč.");
    expect(disabled(view, "[data-git-test]")).toBe(true);
    expect(view.get("[data-git-test]").attributes("title")).toBe("Nejprve vygenerujte deploy klíč.");
  });
});
