// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { clearCsrf } from "../../../api";
import { fakeApi } from "../general/testing";
import type { LogApi } from "../../../logs/logApi";
import { useLogsIssue, useOutboxIssue } from "./issues";

beforeEach(() => clearCsrf());
afterEach(() => vi.unstubAllGlobals());

it("names dead background messages with their last error and retries them in place (ARCH1-13)", async () => {
  let dead = 2;
  const calls = fakeApi((method, path) => {
    if (path === "/admin/outbox" && method === "GET") return { pending: 0, dead, oldestPendingAt: null, lastFailedAt: "2026-10-29T10:00:00Z", lastError: "System.InvalidOperationException: boom" };
    if (path === "/admin/outbox/retry" && method === "POST") return { retried: (dead = 0, 2) };
    return undefined;
  });
  const outbox = useOutboxIssue();
  expect(outbox.issue()).toBeNull();
  await outbox.load();
  const issue = outbox.issue()!;
  expect(issue.title).toBe("2 zprávy na pozadí se nepodařilo zpracovat");
  expect(issue.detail).toContain("boom");
  await issue.run!();
  expect(calls.some((call) => call.method === "POST" && call.path === "/admin/outbox/retry")).toBe(true);
  expect(outbox.issue()).toBeNull();
});

it("shows nothing when the health cannot be read", async () => {
  fakeApi(() => new Response("{}", { status: 403 }));
  const outbox = useOutboxIssue();
  await outbox.load();
  expect(outbox.issue()).toBeNull();
});

it("counts the errors of the last hour in the system log and links to the log filtered to errors", async () => {
  const summary = (errorsHour: number) => ({ errorsHour, warningsHour: 0, errorsDay: errorsHour, warningsDay: 0, dropped: 0 });
  let errors = 0;
  const asked: unknown[] = [];
  const api: LogApi = { async list(filter, _cursor, take) { asked.push([filter, take]); return { entries: [], more: false, summary: summary(errors) }; }, exportUrl: () => "" };
  const logs = useLogsIssue(api);
  await logs.load();
  expect(logs.issue()).toBeNull();
  errors = 3;
  await logs.load();
  expect(asked.at(-1)).toEqual([{ level: "error" }, 1]);
  expect(logs.errorsHour.value).toBe(3);
  expect(logs.issue()!.title).toBe("3 chyby v systémovém logu za poslední hodinu");
  expect(logs.issue()!.target).toEqual({ hash: "#/settings/general/logs?uroven=error" });
  await useLogsIssue({ ...api, list: async () => { throw new Error("403"); } }).load();
});
