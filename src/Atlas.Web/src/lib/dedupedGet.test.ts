// @vitest-environment node
import { expect, it, vi } from "vitest";
import { dedupedGet } from "./dedupedGet";

it("runs one request for readers that ask at the same moment, and a new one after it settled", async () => {
  const start = vi.fn(async () => "answer");
  const [a, b] = await Promise.all([dedupedGet("k", start), dedupedGet("k", start)]);
  expect([a, b]).toEqual(["answer", "answer"]);
  expect(start).toHaveBeenCalledTimes(1);
  await dedupedGet("k", start);
  expect(start).toHaveBeenCalledTimes(2);
});

it("lets nobody join a request whose reader gave up", async () => {
  const start = vi.fn(() => new Promise<string>(() => {}));
  const gone = new AbortController();
  void dedupedGet("k2", start, gone.signal);
  gone.abort();
  void dedupedGet("k2", start);
  expect(start).toHaveBeenCalledTimes(2);
});

it("refuses a reader that gave up already, so it neither starts nor holds a request", async () => {
  let request: AbortSignal | undefined;
  const start = vi.fn((signal: AbortSignal) => { request = signal; return new Promise<string>(() => {}); });
  const gone = new AbortController();
  gone.abort(new Error("gone"));
  await expect(dedupedGet("k4", start, gone.signal)).rejects.toThrow("gone");
  expect(start).not.toHaveBeenCalled();
  // Joining a running request, it would have kept it alive after its only other reader left.
  const reader = new AbortController();
  void dedupedGet("k4", start, reader.signal);
  await expect(dedupedGet("k4", start, gone.signal)).rejects.toThrow("gone");
  reader.abort();
  expect(request!.aborted).toBe(true);
});

it("lets go of a reader's signal once the answer settled", async () => {
  const reader = new AbortController();
  const remove = vi.spyOn(reader.signal, "removeEventListener");
  await expect(dedupedGet("k5", async () => "answer", reader.signal)).resolves.toBe("answer");
  expect(remove).toHaveBeenCalledWith("abort", expect.any(Function));
});

it("aborts the request once its last reader gave up, not before", () => {
  let request: AbortSignal | undefined;
  const start = vi.fn((signal: AbortSignal) => { request = signal; return new Promise<string>(() => {}); });
  const [first, second] = [new AbortController(), new AbortController()];
  void dedupedGet("k3", start, first.signal);
  void dedupedGet("k3", start, second.signal);
  first.abort();
  expect(request!.aborted).toBe(false);
  second.abort();
  expect(request!.aborted).toBe(true);
  expect(start).toHaveBeenCalledOnce();
});
