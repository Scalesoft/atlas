import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../api";
import { createFakeLiveFeed } from "../../../live/fakeLiveFeed";
import BackupsPage from "./BackupsPage.vue";
import type { BackupJobRun, BackupState } from "./backups";
import type { GitBackupStatus } from "./gitBackup";
import { fakeApi, mountPage, problem, type FakeCall, type FakeHandler } from "./testing";

const destination = "smb://files.example.test/wiki-backups";
const run = (id: string, status: string, extra: Record<string, unknown> = {}) => ({ id, startedAt: "2026-09-10T12:00:00Z", finishedAt: "2026-09-10T12:00:04Z", status, trigger: "scheduled", mode: "full", destination, fileName: null, size: 0, message: "", ...extra });
/** The ZIP job's last run, as `GET /admin/backups/run` answers it. */
const jobRun = (state: BackupJobRun["state"], extra: Partial<BackupJobRun> = {}): BackupJobRun => ({ id: 41, jobId: "backups.zip", state, at: "2026-09-10T12:00:00Z", message: null, ...extra });
function response(): BackupState {
  return {
    settings: { enabled: true, intervalMinutes: 1440, mode: "full", destination, username: "backup-service", domain: "EXAMPLE", hasPassword: true, retentionCount: 10 },
    // The archives only: the run state of each check is the job's.
    runs: [
      run("success", "succeeded", { startedAt: "2026-09-09T11:00:00Z", fileName: "atlas-20260909.zip", size: 1536, message: "Záloha byla vytvořena." }),
      run("expired", "succeeded", { startedAt: "2026-09-08T11:00:00Z", size: 1024, message: "Záloha byla vytvořena. Archiv odstraněn podle nastavené retence." }),
    ],
    nextRunAt: "2026-09-11T12:00:00Z",
    lastSuccessAt: "2026-09-09T11:00:04Z",
    warning: "Poslední záloha selhala.",
  } as BackupState;
}

/** The git backup beside the archives: off and never set up. */
function gitResponse(extra: Partial<GitBackupStatus> = {}): GitBackupStatus {
  return {
    settings: { enabled: false, remoteUrl: "", branch: "main", publicKey: null, hostKeys: [] },
    running: false, progress: null, pendingPages: 0, lastSyncAt: null, lastCommit: null, lastError: null, lastErrorAt: null, warning: null,
    ...extra,
  };
}

let wrapper: VueWrapper | undefined;
let calls: FakeCall[] = [];
/** What `GET /admin/backups/run` answers; a Response for none (204). */
let lastRun: () => BackupJobRun | Response = () => jobRun("running", { message: "Zálohování probíhá." });
function api(extra?: FakeHandler, state: () => BackupState = response, git: () => GitBackupStatus = () => gitResponse()) {
  calls = fakeApi((method, path, body) => {
    const custom = extra?.(method, path, body);
    if (custom !== undefined) return custom;
    if (path === "/storage-profiles" && method === "GET") return [];
    if (path === "/admin/backups" && method === "GET") return state();
    if (path === "/admin/backups/git" && method === "GET") return git();
    if (path === "/admin/backups/run" && method === "GET") return lastRun();
    return problem(404, `unexpected ${method} ${path}`);
  });
}
/** /hubs/live as the page hears it: `changed()` is a JobRunChanged (of the ZIP job unless named). */
let feed = createFakeLiveFeed();
const changed = async (jobId = "backups.zip") => { feed.emit("jobs", { runId: 41, jobId, state: "running", spaceId: null }); await flushPromises(); };
async function open(part?: string) {
  feed = createFakeLiveFeed();
  const mounted = mountPage(BackupsPage, { part }, { hash: `#/settings/general/backups${part ? `/${part}` : ""}`, feed: feed.source });
  wrapper = mounted.wrapper;
  await flushPromises();
  return mounted;
}
async function chooseFile(view: VueWrapper, file: File) {
  const input = view.get('[data-backup-import] input[type="file"]');
  Object.defineProperty(input.element, "files", { configurable: true, value: [file] });
  await input.trigger("change");
}

beforeEach(() => {
  clearCsrf();
  lastRun = () => jobRun("running", { message: "Zálohování probíhá." });
});
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  vi.useRealTimers();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

describe("one page for every backup", () => {
  it("shows both ways at a glance and links the archives, git and restore as tabs", async () => {
    api();
    const { wrapper: view } = await open();
    const archives = view.get('[data-backup-method="archives"]');
    expect(archives.attributes("data-state")).toBe("running");
    expect(archives.get("[data-backup-state]").text()).toBe("Probíhá");
    expect(view.get('[data-backup-method="git"]').attributes("data-state")).toBe("disabled");
    expect(view.get('[data-backup-method="git"]').text()).toContain("Nastavte repozitář a zálohu zapněte.");
    expect(view.findAll("[data-tab]").map((tab) => [tab.attributes("data-tab"), tab.attributes("href"), tab.attributes("aria-current")])).toEqual([
      ["archives", "#/settings/general/backups", "page"],
      ["git", "#/settings/general/backups/git", undefined],
      ["restore", "#/settings/general/backups/restore", undefined],
    ]);
    expect(view.find("[data-archive-backup]").exists()).toBe(true);
    expect(view.find("[data-git-backup]").exists()).toBe(false);
    expect(view.find("[data-backup-import]").exists()).toBe(false);
    // The last run twice: the first read and the one the live subscription asks for (it covers what came between them).
    expect(calls.map((call) => `${call.method} ${call.path}`).sort()).toEqual(["GET /admin/backups", "GET /admin/backups/git", "GET /admin/backups/run", "GET /admin/backups/run", "GET /storage-profiles"]);
  });

  it("gives the shell the archives' own warning first and the git one otherwise, each on its own card", async () => {
    // GET /admin/backups answers the git warning when the archives have none of their own.
    const gitFailure = "Záloha do Gitu selhala: Push byl odmítnut.";
    let own: string | null = "Poslední záloha selhala.";
    lastRun = () => new Response(null, { status: 204 });
    api(undefined, () => ({ ...response(), runs: [], warning: own ?? gitFailure }), () => gitResponse({ warning: gitFailure }));
    const { wrapper: view, context } = await open();
    expect(view.get('[data-backup-method="archives"] [data-backup-warning]').text()).toBe("Poslední záloha selhala.");
    expect(view.get('[data-backup-method="git"] [data-git-warning]').text()).toBe(gitFailure);
    expect(context.warning).toHaveBeenLastCalledWith("Poslední záloha selhala.");
    own = null;
    window.dispatchEvent(new Event("focus"));
    await flushPromises();
    expect(view.find('[data-backup-method="archives"] [data-backup-warning]').exists()).toBe(false);
    expect(view.get('[data-backup-method="archives"]').attributes("data-state")).toBe("ok");
    expect(context.warning).toHaveBeenLastCalledWith(gitFailure);
  });

  it("brings the tabs to the top when an address opens Git or Obnovení, not for the default tab", async () => {
    const scroll = vi.fn();
    HTMLElement.prototype.scrollIntoView = scroll;
    api();
    await open("restore");
    expect(scroll).toHaveBeenCalledTimes(1);
    expect((scroll.mock.contexts[0] as HTMLElement).classList.contains("backup-tabs-anchor")).toBe(true);
    wrapper?.unmount();
    scroll.mockClear();
    await open();
    expect(scroll).not.toHaveBeenCalled();
  });

  it("opens the git and restore tabs without mounting the archive form", async () => {
    api(undefined, response, () => gitResponse({ settings: { enabled: true, remoteUrl: "git@git.example.test:atlas.git", branch: "main", publicKey: "ssh-ed25519 AAAA atlas", hostKeys: [] } }));
    const { wrapper: view } = await open("git");
    expect(view.get('[data-tab="git"]').attributes("aria-current")).toBe("page");
    expect(view.find("[data-git-backup]").exists()).toBe(true);
    expect(view.find("[data-archive-backup]").exists()).toBe(false);
    // Both manual runs stay on the overview, whichever tab is open.
    expect(view.get("[data-backup-run]").attributes("disabled")).toBeUndefined();
    expect(view.get("[data-git-sync]").attributes("disabled")).toBeUndefined();
    wrapper!.unmount();
    const restore = (await open("restore")).wrapper;
    expect(restore.find("[data-backup-import]").exists()).toBe(true);
    expect(restore.find('[data-restore="zip"]').exists()).toBe(true);
    expect(restore.find('[data-restore="git"]').exists()).toBe(true);
    expect(restore.find("[data-archive-backup]").exists()).toBe(false);
  });
});

describe("archive state and history", () => {
  it("shows the state and the job's last run on the card, reports the warning to the shell and lists the archives", async () => {
    api();
    const { wrapper: view, context } = await open();
    expect(view.get("[data-backup-warning]").text()).toContain("Poslední záloha selhala.");
    expect(view.get("[data-backup-warning]").attributes("role")).toBe("alert");
    expect(context.warning).toHaveBeenCalledWith("Poslední záloha selhala.");
    expect(view.get('[data-stat="destination"]').text()).toContain("Síťová složka SMB");
    expect(view.get('[data-stat="next"]').text()).toContain("Každý den");
    expect(view.get('[data-stat="run"]').text()).toContain("Probíhá");
    expect(view.get('[data-stat="run"]').text()).toContain("Zálohování probíhá.");
    expect(view.get("[data-backup-run-detail]").attributes("href")).toBe("#/settings/general/jobs/backups.zip/runs/41");
    const history = view.get("[data-backup-history]").text();
    for (const label of ["Dokončeno", "Archiv odstraněn podle nastavené retence.", "1,5 kB"]) expect(history).toContain(label);
    expect(view.get('[data-backup-download="success"]').attributes("href")).toBe("/api/admin/backups/runs/success/download");
    expect(view.findAll("[data-backup-download]")).toHaveLength(1);
  });

  it("keeps the newest successful archive downloadable after it falls out of the recent history", async () => {
    const lastBackup = { ...response().runs[0]!, id: "preserved", fileName: "atlas-preserved.zip" };
    api(undefined, () => ({ ...response(), runs: [response().runs[1]!], lastBackup }));
    const { wrapper: view } = await open();
    expect(view.get('[data-backup-download="preserved"]').text()).toContain("Stáhnout ZIP");
  });

  it("asks for a fresh sign-in before a complete archive goes out (SEC3-1)", async () => {
    const complete = run("complete", "succeeded", { mode: "complete", fileName: "wiki-complete.zip", size: 2048 });
    api((method, path) => (path === "/admin/backups/runs/complete/download/authorize" ? problem(403, "Potvrďte novým přihlášením.", "reauth_required") : undefined),
      () => ({ ...response(), runs: [...response().runs, complete] }) as BackupState);
    const { wrapper: view } = await open();
    expect(view.get('[data-backup-download="complete"]').attributes("href")).toBe("/api/admin/backups/runs/complete/download");
    await view.get('[data-backup-download="complete"]').trigger("click");
    await flushPromises();
    expect(calls.filter((call) => call.path.includes("/authorize"))).toEqual([{ method: "POST", path: "/admin/backups/runs/complete/download/authorize", body: undefined }]);
    expect(document.body.querySelector("[data-reauth-dialog]")).not.toBeNull();
    expect(document.body.querySelector("[data-backup-destination-reauth]")!.textContent).toContain("Stažení potvrďte novým přihlášením");
  });

  it("starts a manual run and refreshes the server-owned history", async () => {
    let started = false;
    api((method, path) => {
      if (path === "/admin/backups/run" && method === "POST") {
        started = true;
        return { runId: 42 };
      }
      if (path === "/admin/backups/run" && started) return jobRun("queued", { id: 42, at: new Date().toISOString() });
      return undefined;
    });
    const { wrapper: view, flash } = await open();
    await view.get("[data-backup-run]").trigger("click");
    await flushPromises();
    expect(calls.filter((call) => call.method === "POST")).toEqual([{ method: "POST", path: "/admin/backups/run", body: undefined }]);
    expect(calls.filter((call) => call.path === "/admin/backups")).toHaveLength(2);
    expect(view.get('[data-stat="run"]').text()).toContain("Ve frontě");
    expect(view.get("[data-backup-run-detail]").attributes("href")).toBe("#/settings/general/jobs/backups.zip/runs/42");
    expect(view.findAll(".activity-item")).toHaveLength(2);
    expect(flash().message.value?.text).toContain("Záloha je spuštěná");
  });

  it("allows a manual run while scheduling is off, but not with unsaved changes", async () => {
    api(undefined, () => ({ ...response(), settings: { ...response().settings, enabled: false }, nextRunAt: null }));
    const { wrapper: view } = await open();
    expect(view.get('[data-stat="next"]').text()).toContain("Vypnuto");
    expect(view.get("[data-backup-run]").attributes("disabled")).toBeUndefined();
    await view.get("[data-backup-retention]").setValue("11");
    expect(view.get("[data-backup-run]").attributes("disabled")).toBeDefined();
  });

  it("refreshes the warning and history on focus without replacing the draft or a typed password", async () => {
    let refreshed = false;
    api((method, path) => (!refreshed ? undefined : path === "/admin/backups" ? { ...response(), warning: "Záloha je po termínu." }
      : path === "/admin/backups/run" ? jobRun("failed", { id: 43, message: "Nové selhání." }) : undefined));
    const { wrapper: view, context } = await open();
    await view.get("[data-backup-destination]").setValue("smb://draft/new-place");
    await view.get("[data-backup-password]").setValue("draft-secret");
    refreshed = true;
    window.dispatchEvent(new Event("focus"));
    await flushPromises();
    expect((view.get("[data-backup-destination]").element as HTMLInputElement).value).toBe("smb://draft/new-place");
    expect((view.get("[data-backup-password]").element as HTMLInputElement).value).toBe("draft-secret");
    expect(view.text()).toContain("Záloha je po termínu.");
    expect(view.text()).toContain("Nové selhání.");
    expect(context.warning).toHaveBeenLastCalledWith("Záloha je po termínu.");
  });

  it("reads only the last run, when the server tells a ZIP run changed and never on a timer; its end brings the archives once more", async () => {
    vi.useFakeTimers();
    let state: BackupJobRun["state"] = "running";
    lastRun = () => jobRun(state, { message: state === "succeeded" ? "Záloha byla úspěšně vytvořena." : null });
    api();
    const { wrapper: view } = await open();
    const runs = () => calls.filter((call) => call.path === "/admin/backups/run").length;
    expect(view.get("[data-backup-last-run]").attributes("data-run-state")).toBe("running");
    // The first read and the one the subscription asks for.
    expect(runs()).toBe(2);
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(runs()).toBe(2);
    await changed("backups.git");
    expect(runs()).toBe(2);
    state = "succeeded";
    await changed();
    expect(runs()).toBe(3);
    expect(calls.filter((call) => call.path === "/admin/backups")).toHaveLength(2);
    expect(calls.filter((call) => call.path === "/admin/backups/git")).toHaveLength(1);
    expect(view.get("[data-backup-last-run]").attributes("data-run-state")).toBe("succeeded");
    expect(view.get('[data-stat="run"]').text()).toContain("Záloha byla úspěšně vytvořena.");
  });

  it("leaves a queued run waiting quietly, and says when the live feed is lost with a manual refresh", async () => {
    vi.useFakeTimers();
    lastRun = () => jobRun("queued");
    api();
    const { wrapper: view } = await open();
    const asked = calls.filter((call) => call.path === "/admin/backups/run").length;
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(calls.filter((call) => call.path === "/admin/backups/run")).toHaveLength(asked);
    feed.fail("jobs");
    await flushPromises();
    await view.get("[data-live-update-refresh]").trigger("click");
    await flushPromises();
    expect(calls.filter((call) => call.path === "/admin/backups/run")).toHaveLength(asked + 1);
  });

  it("paints the cards and tabs at once and loads the last run on its own", async () => {
    let answer!: (run: BackupJobRun) => void;
    const pending = new Promise<BackupJobRun>((resolve) => { answer = resolve; });
    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    // Held answers: fakeApi answers at once, so this test stubs fetch itself.
    vi.stubGlobal("fetch", vi.fn(async (input: string) => {
      const path = new URL(String(input), "http://atlas.test").pathname.replace(/^\/api/, "");
      if (path === "/auth/csrf") return Response.json({ token: "csrf" });
      if (path === "/admin/backups/run") return Response.json(await pending);
      await gate;
      return Response.json(path === "/admin/backups" ? response() : path === "/admin/backups/git" ? gitResponse() : []);
    }));
    const { wrapper: view } = await open();
    // Nothing answered yet: the cards, their run buttons and the tabs stand, sized skeletons hold the facts.
    expect(view.find('[data-backup-method="archives"]').exists()).toBe(true);
    expect(view.find('[data-backup-method="archives"] .loading-skeleton').exists()).toBe(true);
    expect(view.find('[data-backup-method="git"] .loading-skeleton').exists()).toBe(true);
    expect(view.findAll("[data-tab]")).toHaveLength(3);
    expect(view.get("[data-backup-run]").attributes("disabled")).toBeDefined();
    release();
    await flushPromises();
    // The states arrived; the last run still loads in its own row.
    expect(view.find('[data-backup-method="archives"] .loading-skeleton').exists()).toBe(false);
    expect(view.get('[data-stat="run"]').text()).toContain("Načítání posledního běhu");
    answer(jobRun("failed", { message: "Cíl není dostupný." }));
    await flushPromises();
    expect(view.get('[data-stat="run"]').text()).toContain("Cíl není dostupný.");
    expect(view.get('[data-backup-method="archives"]').attributes("data-state")).toBe("failed");
  });
});

describe("backup schedule", () => {
  it("saves the interval chosen as a pill and the retention with the SaveBar; a blank password keeps the stored one", async () => {
    api((method, path) => (path === "/admin/backups" && method === "PUT" ? { ...response(), settings: { ...response().settings, intervalMinutes: 360, retentionCount: 25 } } : undefined));
    const { wrapper: view, guard } = await open();
    await view.get('[data-backup-interval] [data-value="360"]').trigger("click");
    await view.get("[data-backup-retention]").setValue("25");
    expect(guard.dirty.value).toBe(true);
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    const body = calls.find((call) => call.method === "PUT")!.body as Record<string, unknown>;
    expect(body).toMatchObject({ intervalMinutes: 360, retentionCount: 25, password: "", destination, enabled: true, mode: "full" });
    expect(body).not.toHaveProperty("hasPassword");
    expect(guard.dirty.value).toBe(false);
  });

  it("recommends the whole installation, saves it and shows the new backup key once (GAP2-1)", async () => {
    const key = "k".repeat(43);
    api((method, path) => (path === "/admin/backups" && method === "PUT"
      ? { ...response(), settings: { ...response().settings, mode: "complete" }, newBackupKey: { key, keyId: "a1b2", createdAt: "2026-10-30T10:00:00Z" } }
      : undefined));
    const { wrapper: view } = await open();
    expect(view.get('[data-backup-mode] [data-value="complete"]').text()).toContain("Doporučeno");
    await view.get('[data-backup-mode] [role="radio"][value="complete"]').trigger("click");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(calls.find((call) => call.method === "PUT")!.body).toMatchObject({ mode: "complete" });
    expect(view.get("[data-backup-new-key]").text()).toContain(key);
  });

  it("reveals a custom interval and refuses an invalid one next to the field", async () => {
    api((method, path) => (path === "/admin/backups" && method === "PUT" ? response() : undefined));
    const { wrapper: view } = await open();
    expect(view.find("[data-backup-custom-minutes]").exists()).toBe(false);
    await view.get('[data-backup-interval] [data-value="custom"]').trigger("click");
    await view.get("[data-backup-custom-minutes]").setValue("3");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
    expect(view.text()).toContain("od 5 do 525 600 minut");
    await view.get("[data-backup-custom-minutes]").setValue("90");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(calls.find((call) => call.method === "PUT")!.body).toMatchObject({ intervalMinutes: 90 });
  });

  it("tests the unsaved SMB draft and keeps a new password from the test through the first save", async () => {
    api((method, path) => {
      if (path === "/admin/backups/test") return { message: "Cíl je dostupný. Testovací soubor byl vytvořen, přečten, přejmenován a odstraněn." };
      if (path === "/admin/backups" && method === "PUT") return response();
      return undefined;
    });
    const { wrapper: view } = await open();
    expect(view.get("[data-backup-password]").attributes("placeholder")).toContain("Uložené heslo");
    await view.get("[data-backup-password]").setValue("first-setup-secret");
    await view.get("[data-backup-test]").trigger("click");
    await flushPromises();
    expect(calls.find((call) => call.path === "/admin/backups/test")!.body).toMatchObject({ destination, username: "backup-service", domain: "EXAMPLE", password: "first-setup-secret" });
    expect(view.get("[data-backup-test-result]").text()).toContain("přejmenován a odstraněn");
    expect((view.get("[data-backup-password]").element as HTMLInputElement).value).toBe("first-setup-secret");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(calls.find((call) => call.method === "PUT")!.body).toMatchObject({ password: "first-setup-secret" });
    expect((view.get("[data-backup-password]").element as HTMLInputElement).value).toBe("");
  });

  it("keeps a typed password after a failed test and a failed save so the administrator can retry", async () => {
    api((method, path) => {
      if (path === "/admin/backups/test") return problem(400, "Test selhal.");
      if (path === "/admin/backups" && method === "PUT") return problem(400, "Uložení selhalo.");
      return undefined;
    });
    const { wrapper: view, guard } = await open();
    await view.get("[data-backup-password]").setValue("retry-secret");
    await view.get("[data-backup-test]").trigger("click");
    await flushPromises();
    expect(view.get("[data-backup-test-result]").attributes("data-tone")).toBe("danger");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(view.get(".save-bar").text()).toContain("Uložení selhalo.");
    expect((view.get("[data-backup-password]").element as HTMLInputElement).value).toBe("retry-secret");
    expect(guard.dirty.value).toBe(true);
  });

  it("switches the destination kind as tiles and clears SMB credentials for a local folder", async () => {
    api((method, path) => (path === "/admin/backups" && method === "PUT" ? response() : undefined));
    const { wrapper: view } = await open();
    await view.get('[data-backup-destination-kind] [role="radio"][value="local"]').trigger("click");
    await flushPromises();
    expect(view.find("[data-backup-username]").exists()).toBe(false);
    await view.get("[data-backup-destination]").setValue("/var/lib/atlas/backups");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(calls.find((call) => call.method === "PUT")!.body).toMatchObject({ destination: "/var/lib/atlas/backups", username: "", domain: "", password: "" });
  });

  it("recognizes an uppercase SMB scheme returned by the server", async () => {
    api(undefined, () => ({ ...response(), settings: { ...response().settings, destination: "SMB://files.example.test/wiki-backups" } }));
    const { wrapper: view } = await open();
    expect(view.find("[data-backup-username]").exists()).toBe(true);
  });
});

describe("backup import", () => {
  it("previews add mode with the actual suffix and adds copies without a destructive confirmation", async () => {
    api((method, path, body) => {
      if (path === "/admin/backups/import/preview") {
        expect((body as FormData).get("mode")).toBe("add");
        expect(((body as FormData).get("file") as File).name).toBe("portable.zip");
        return { suffix: " (obnoveno 10. 9. 2026)", pages: 12, attachments: 4, message: "Záloha je připravená." };
      }
      if (path === "/admin/backups/import") return { pages: 12, attachments: 4, message: "Kopie byly přidány." };
      return undefined;
    });
    const { wrapper: view, context } = await open("restore");
    await chooseFile(view, new File(["zip"], "portable.zip", { type: "application/zip" }));
    await view.get("[data-backup-import-check]").trigger("click");
    await flushPromises();
    const disclosure = view.get(".backup-import-disclosure").text();
    for (const text of ["vytvoří kopie stránek a příloh", "(obnoveno 10. 9. 2026)", "Interní odkazy se přesměrují na nové URL", "Původní obsah zůstane beze změny", "Účty, přihlášení a oprávnění nemění"]) expect(disclosure).toContain(text);
    await view.get("[data-backup-import-run]").trigger("click");
    await flushPromises();
    expect((calls.find((call) => call.path === "/admin/backups/import")!.body as FormData).get("acknowledge")).toBe("true");
    expect(view.text()).toContain("Kopie byly přidány.");
    expect(context.imported).toHaveBeenCalledWith("");
  });

  it("lists invalid documents and offers no import of such an archive", async () => {
    api((method, path) => {
      if (path === "/admin/backups/import/preview") return { suffix: " (kopie)", pages: 2, attachments: 0, message: "Záloha je připravená.", invalid: 1,
        invalidDocuments: [{ pageId: "p1", version: 3, title: "Runbook", path: "document.content[0].attrs.nope", reason: "není atribut uzlu" }] };
      return undefined;
    });
    const { wrapper: view } = await open("restore");
    await chooseFile(view, new File(["zip"], "portable.zip", { type: "application/zip" }));
    await view.get("[data-backup-import-check]").trigger("click");
    await flushPromises();
    expect(view.get("[data-backup-import-invalid]").text()).toContain("Runbook (verze 3, p1): document.content[0].attrs.nope není atribut uzlu");
    expect(view.get("[data-backup-import-run]").attributes("disabled")).toBeDefined();
    await view.get("[data-backup-import-run]").trigger("click");
    await flushPromises();
    expect(calls.some((call) => call.path === "/admin/backups/import")).toBe(false);
  });

  it("lists the Atlas 2.x repairs the import makes and still offers the import", async () => {
    api((method, path) => {
      if (path === "/admin/backups/import/preview") return { suffix: "", pages: 2, attachments: 0, message: "Obnova je připravená.", invalid: 0, repaired: 2,
        repairedDocuments: [{ pageId: "p1", version: 3, title: "Služby", path: "document.content[0].content[0].marks[0]", reason: "ponechána vnitřní, vynechána vnější: https://example.com" }] };
      return undefined;
    });
    const { wrapper: view } = await open("restore");
    await chooseFile(view, new File(["zip"], "portable.zip", { type: "application/zip" }));
    await view.get("[data-backup-import-check]").trigger("click");
    await flushPromises();
    const repaired = view.get("[data-backup-import-repaired]").text();
    expect(repaired).toContain("2× import opraví zápis z Atlasu 2.x");
    expect(repaired).toContain("Služby (verze 3): document.content[0].content[0].marks[0] ponechána vnitřní, vynechána vnější: https://example.com");
    expect(repaired).toContain("a další (1)");
    expect(view.find("[data-backup-import-invalid]").exists()).toBe(false);
    expect(view.get("[data-backup-import-run]").attributes("disabled")).toBeUndefined();
  });

  it("restores only after a preview and a ConfirmDialog", async () => {
    api((method, path, body) => {
      if (path === "/admin/backups/import/preview") return { suffix: "", pages: 12, attachments: 4, message: "Obnova je připravená." };
      if (path === "/admin/backups/import") {
        expect((body as FormData).get("mode")).toBe("restore");
        return { pages: 12, attachments: 4, message: "Wiki byla obnovena." };
      }
      return undefined;
    });
    const { wrapper: view } = await open("restore");
    await view.get('[data-backup-import-mode] [role="radio"][value="restore"]').trigger("click");
    await flushPromises();
    await chooseFile(view, new File(["zip"], "portable.zip", { type: "application/zip" }));
    await view.get("[data-backup-import-check]").trigger("click");
    await flushPromises();
    expect(view.get(".backup-import-disclosure").text()).toContain("Další současné stránky zůstanou zachované");
    await view.get("[data-backup-import-run]").trigger("click");
    await flushPromises();
    expect(calls.some((call) => call.path === "/admin/backups/import")).toBe(false);
    const confirm = document.body.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-action]")!;
    expect(document.body.textContent).toContain("Server nejprve vytvoří bezpečnostní archiv");
    confirm.click();
    await flushPromises();
    expect(calls.filter((call) => call.path === "/admin/backups/import")).toHaveLength(1);
    expect(view.text()).toContain("Wiki byla obnovena.");
  });
});

it("selects a shared backup profile and saves only its relative folder, ignoring hidden legacy credentials", async () => {
  api((method, path, body) => {
    if (path === "/storage-profiles") return [{ id: "shared", name: "Sdílené archivy", enabled: true, allowBackups: true }];
    if (method === "PUT" && path === "/admin/backups") return { ...response(), settings: { ...response().settings, ...body as object, hasPassword: true } };
  });
  const { wrapper: view } = await open();
  await view.get("[data-backup-profile] [data-value=\"shared\"]").trigger("click");
  await view.get("[data-backup-folder]").setValue("wiki/archives");
  await view.get("[data-save-bar-save]").trigger("click"); await flushPromises();
  expect(calls.find(call => call.method === "PUT")?.body).toMatchObject({ storageProfileId: "shared", folder: "wiki/archives" });
  expect(calls.find(call => call.method === "PUT")?.body).not.toHaveProperty("password");
  expect(view.find("[data-backup-password]").exists()).toBe(false);
});


it("can stop scheduled ZIP backups when their shared profile has been disabled", async () => {
  const disabled = { ...response(), settings: { ...response().settings, storageProfileId: "disabled", folder: "archives" } };
  api((method, path, body) => {
    if (path === "/storage-profiles") return [{ id: "disabled", name: "Vypnuté úložiště", enabled: false, allowBackups: true }];
    if (path === "/admin/backups" && method === "GET") return disabled;
    if (path === "/admin/backups" && method === "PUT") return { ...disabled, settings: { ...disabled.settings, ...body as object } };
  });
  const { wrapper: view } = await open();
  await view.get("[data-backup-enabled]").trigger("click");
  await view.get("[data-save-bar-save]").trigger("click"); await flushPromises();
  expect(calls.find(call => call.method === "PUT")?.body).toMatchObject({ enabled: false, storageProfileId: "disabled" });
});
