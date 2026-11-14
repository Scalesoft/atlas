import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, describe, it, expect, vi } from "vitest";
import { ApiError, put } from "./api";
import { ref } from "vue";
import { documentLimits, documentSizeError, putWithCollaborationRetry, untilProjected } from "./savePage";
vi.mock("./api", async (original) => ({
  ...(await original<object>()),
  put: vi.fn(),
}));
afterEach(() => {
  vi.useRealTimers();
  vi.clearAllMocks();
});
const payload = {
  title: "Captured title",
  contentJson: "captured",
  markdown: "captured",
  expectedVersion: 7,
  collaborationGeneration: "generation",
};
describe("bounded collaborative projection wait", () => {
  it("retries the identical captured payload after 80 ms and reports the wait", async () => {
    vi.useFakeTimers();
    const notify = vi.fn();
    vi.mocked(put)
      .mockRejectedValueOnce(
        new ApiError(409, "Pending", "collaboration_pending"),
      )
      .mockResolvedValue({ version: 8 });
    const result = putWithCollaborationRetry("/pages/page", payload, notify);
    await vi.advanceTimersByTimeAsync(79);
    expect(put).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(await result).toEqual({ version: 8 });
    expect(notify).toHaveBeenCalledOnce();
    expect(vi.mocked(put).mock.calls.every((call) => call[1] === payload)).toBe(
      true,
    );
  });
  it.each([
    new ApiError(409, "Version conflict"),
    new ApiError(409, "Content mismatch", "collaboration_conflict"),
    // A live snapshot the server refused (D-M3): its reason at once, not three seconds of waiting.
    new ApiError(400, "Společné změny nejde uložit, server je odmítl: …", "invalid_document"),
    new ApiError(403, "Revoked"),
    new Error("Offline"),
  ])(
    "does not retry a conflict, denied access or network failure",
    async (error) => {
      vi.mocked(put).mockRejectedValue(error);
      await expect(
        putWithCollaborationRetry("/pages/page", payload),
      ).rejects.toBe(error);
      expect(put).toHaveBeenCalledOnce();
    },
  );
  it("stops after three seconds and preserves the original pending error", async () => {
    vi.useFakeTimers();
    const error = new ApiError(409, "Pending", "collaboration_pending");
    vi.mocked(put).mockRejectedValue(error);
    const result = putWithCollaborationRetry("/pages/page", payload).catch(
      (e) => e,
    );
    await vi.runAllTimersAsync();
    expect(await result).toBe(error);
    expect(put).toHaveBeenCalledTimes(38);
  });
  it("never retries an ordinary noncollaborative save", async () => {
    const error = new ApiError(409, "Pending", "collaboration_pending");
    vi.mocked(put).mockRejectedValue(error);
    await expect(
      putWithCollaborationRetry("/pages/page", { expectedVersion: 7 }),
    ).rejects.toBe(error);
    expect(put).toHaveBeenCalledOnce();
  });
});
// U4-11: Hotovo right after a few state changes was refused four times (collaboration_pending) while the server's projection of
// the shared log caught up; the save waits for the projection the editor reports instead.
describe("untilProjected", () => {
  it("waits while the collaborative projection is pending, and no longer than its bound", async () => {
    vi.useFakeTimers();
    const status = ref<{ enabled: boolean; projectionSave: string } | null>({ enabled: true, projectionSave: "pending" });
    let settled = false;
    void untilProjected(() => status.value).then(() => { settled = true; });
    await vi.advanceTimersByTimeAsync(500);
    expect(settled).toBe(false);
    status.value = { enabled: true, projectionSave: "current" };
    await vi.advanceTimersByTimeAsync(0);
    expect(settled).toBe(true);

    status.value = { enabled: true, projectionSave: "pending" };
    settled = false;
    void untilProjected(() => status.value, 3000).then(() => { settled = true; });
    await vi.advanceTimersByTimeAsync(3000);
    expect(settled).toBe(true);
    // Nothing to wait for without a live session.
    await expect(untilProjected(() => null)).resolves.toBeUndefined();
  });
});

describe("stored page size", () => {
  it("uses the server's limits", () => {
    const rules = readFileSync(resolve(__dirname, "../../Atlas.Server/Platform/Validation/Rules.cs"), "utf8");
    expect(rules).toContain(`MaxDocumentJsonLength = ${documentLimits.json.toLocaleString("en").replaceAll(",", "_")};`);
    expect(rules).toContain(`MaxDocumentMarkdownLength = ${documentLimits.markdown.toLocaleString("en").replaceAll(",", "_")};`);
  });
  it("refuses an oversized page before the upload and names the limit it exceeds", async () => {
    await expect(putWithCollaborationRetry("/pages/page", { ...payload, contentJson: "x".repeat(2_300_000) }))
      .rejects.toThrow("Stránka je příliš velká: uložený obsah má 2,3 MB, limit je 2 MB. Rozdělte velké tabulky do více stránek.");
    expect(documentSizeError("{}", "x".repeat(1_000_001))).toBe("Stránka je příliš velká: text stránky má 1,1 MB, limit je 1 MB. Rozdělte obsah do více stránek.");
    expect(documentSizeError("x".repeat(documentLimits.json), "x".repeat(documentLimits.markdown))).toBe("");
    expect(put).not.toHaveBeenCalled();
  });
});
