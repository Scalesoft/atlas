// @vitest-environment node
import { expect, it, vi } from "vitest";
import { createContactApi, linkedSnapshot } from "./contactApi";

const ids = ["a1b2c3d4-0000-4000-8000-000000000001", "a1b2c3d4-0000-4000-8000-000000000002", "a1b2c3d4-0000-4000-8000-000000000003"] as const;
function fake(items: Record<string, unknown>) {
  const post = vi.fn(async (_path: string, _body: unknown) => ({ items }));
  return { post, api: createContactApi({ get: vi.fn(), post: post as <T>(path: string, body: unknown) => Promise<T> }) };
}

it("asks for every id of one tick in one request and keeps the answers", async () => {
  const { post, api } = fake({ [ids[0]]: { state: "ok", contact: { name: "Jana" } }, [ids[1].toUpperCase()]: { state: "trashed" } });
  const [first, second, third] = await Promise.all([api.live(ids[0]), api.live(ids[1]), api.live(ids[2]), api.live(ids[0].toUpperCase())]);
  expect(post).toHaveBeenCalledOnce();
  expect(post).toHaveBeenCalledWith("/contacts/live", { ids: [ids[0], ids[1], ids[2]] });
  expect(first).toEqual({ state: "ok", contact: { name: "Jana" } });
  expect(second).toEqual({ state: "trashed" });
  // An id the server did not answer is gone.
  expect(third).toEqual({ state: "missing" });
  await api.live(ids[0]);
  expect(post).toHaveBeenCalledOnce();
  api.forget(ids[0]);
  await api.live(ids[0]);
  expect(post).toHaveBeenCalledTimes(2);
});

it("follows a merged contact to the one it went into", async () => {
  const { api } = fake({ [ids[0]]: { state: "ok", contactId: ids[1], contact: { name: "Jana", phones: ["+420777123456"] } } });
  expect(await api.live(ids[0])).toEqual({ state: "ok", contact: { name: "Jana", phones: ["+420777123456"], objectId: ids[1] } });
});

it("fails the callers of a failed request and asks again next time; an aborted caller stops waiting", async () => {
  const post = vi.fn().mockRejectedValueOnce(new Error("500")).mockResolvedValue({ items: { [ids[0]]: { state: "hidden" } } });
  const api = createContactApi({ get: vi.fn(), post });
  await expect(api.live(ids[0])).rejects.toThrow("500");
  expect(await api.live(ids[0])).toEqual({ state: "hidden" });
  const controller = new AbortController();
  api.forget();
  const waiting = api.live(ids[0], { signal: controller.signal });
  controller.abort();
  await expect(waiting).rejects.toBeDefined();
});

it("keeps the page's own role, kind and account on a linked snapshot", () => {
  const snapshot = { name: "Jan", role: "PM projektu", kind: "internal" as const, internalUser: { id: "u1", name: "Jan" }, phones: ["1"] };
  expect(linkedSnapshot(snapshot, ids[0], { name: "Jan Novák", phones: ["+420777123456"], emails: ["jan@example.cz"] }))
    .toEqual({ name: "Jan Novák", role: "PM projektu", kind: "internal", internalUser: { id: "u1", name: "Jan" }, phones: ["+420777123456"], emails: ["jan@example.cz"], objectId: ids[0] });
});
