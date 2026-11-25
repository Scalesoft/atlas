// @vitest-environment node
import { beforeEach, describe, expect, it, vi } from "vitest";
import { toast } from "vue-sonner";
import { useToast } from "./useToast";

vi.mock("vue-sonner", () => ({
  toast: Object.assign(vi.fn(), { success: vi.fn((_message: string, options?: { id?: string }) => options?.id ?? 1), error: vi.fn(), dismiss: vi.fn() }),
}));

describe("useToast", () => {
  beforeEach(() => vi.clearAllMocks());

  it("announces a success", () => {
    useToast().success("Vzhled uložen.");
    expect(toast.success).toHaveBeenCalledWith("Vzhled uložen.", expect.anything());
  });

  it("keeps errors distinguishable from successes", () => {
    useToast().error("Uložení selhalo.");
    expect(toast.error).toHaveBeenCalledWith("Uložení selhalo.", expect.anything());
    expect(toast.success).not.toHaveBeenCalled();
  });

  it("offers an undo action only when one is supplied", () => {
    const undo = vi.fn();
    useToast().success("Stránka přesunuta.", { undo });
    const options = vi.mocked(toast.success).mock.calls[0]![1] as { action?: { onClick: () => void } };
    expect(options.action).toBeDefined();
    options.action!.onClick();
    expect(undo).toHaveBeenCalledOnce();

    vi.clearAllMocks();
    useToast().success("Uloženo.");
    const plain = vi.mocked(toast.success).mock.calls[0]![1] as { action?: unknown };
    expect(plain.action).toBeUndefined();
  });

  it("passes an id on, so a toast of the same id replaces the one shown", () => {
    useToast().success("Stránka byla přesunuta.", { id: "tree-move" });
    expect(toast.success).toHaveBeenCalledWith("Stránka byla přesunuta.", expect.objectContaining({ id: "tree-move" }));
  });

  it("gives an error longer on screen than a confirmation", () => {
    // A confirmation is glanceable; a failure is something the reader has to act
    // on, so it must not vanish while they are still reading it.
    useToast().success("Uloženo.");
    useToast().error("Nepodařilo se uložit.");
    const ok = vi.mocked(toast.success).mock.calls[0]![1] as { duration: number };
    const bad = vi.mocked(toast.error).mock.calls[0]![1] as { duration: number };
    expect(bad.duration).toBeGreaterThan(ok.duration);
  });

  it("runs the „Zpět“ of the undo toast still on screen once, as its button does, and nothing once it is gone or replaced", () => {
    const toaster = useToast();
    const undo = vi.fn();
    toaster.success("Stránka přesunuta.", { id: "tree-move", undo });
    expect(toaster.undoLatest()).toBe(true);
    expect(toast.dismiss).toHaveBeenCalledWith("tree-move");
    expect(undo).toHaveBeenCalledOnce();
    expect(toaster.undoLatest()).toBe(false);

    // The button ran it: Ctrl+Z does not run it again.
    toaster.success("Stránka přesunuta.", { id: "tree-move", undo });
    (vi.mocked(toast.success).mock.calls.at(-1)![1] as { action: { onClick: () => void } }).action.onClick();
    expect(toaster.undoLatest()).toBe(false);
    expect(undo).toHaveBeenCalledTimes(2);

    // Its time ran out, or a toast of the same id without „Zpět“ took its place.
    toaster.success("Stránka přesunuta.", { id: "tree-move", undo });
    (vi.mocked(toast.success).mock.calls.at(-1)![1] as { onAutoClose: (shown: { id: string }) => void }).onAutoClose({ id: "tree-move" });
    expect(toaster.undoLatest()).toBe(false);
    toaster.success("Seřazeno.", { id: "tree-sort", undo });
    toaster.success("Seřazeno abecedně.", { id: "tree-sort" });
    expect(toaster.undoLatest()).toBe(false);
    expect(undo).toHaveBeenCalledTimes(2);
  });
});
