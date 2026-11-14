import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { crossFade } from "./crossFade";

const root = document.documentElement;
type Transition = { ready: Promise<void>; finished: Promise<void>; updateCallbackDone: Promise<void> };
const unhandled: unknown[] = [];
const onUnhandled = (reason: unknown) => { unhandled.push(reason); };
beforeEach(() => {
  unhandled.length = 0;
  process.on("unhandledRejection", onUnhandled);
});
afterEach(() => {
  process.off("unhandledRejection", onUnhandled);
  delete (document as { startViewTransition?: unknown }).startViewTransition;
  root.removeAttribute("data-atlas-reduced-motion");
  root.removeAttribute("data-crossfade-reduced");
});
/** Lets the platform report what nobody handled. */
const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 20));

/**
 * A view transition as the browser makes one: the update runs once; if it fails, ready, finished and updateCallbackDone all reject
 * with its reason (three promises, each reported if nobody handles it); `skipped` is a transition the browser gave up on (a
 * hidden tab, a resize, a newer transition): its ready rejects with an AbortError, the update still runs.
 */
function install(options: { skipped?: boolean; finished?: Promise<void> } = {}) {
  document.startViewTransition = ((update: () => Promise<void> | void) => {
    const updated = Promise.resolve().then(update);
    const ready = options.skipped ? Promise.reject(new DOMException("Transition was skipped", "AbortError")) : updated.then(() => undefined);
    const finished = options.finished ?? updated.then(() => undefined);
    return { ready, finished, updateCallbackDone: updated.then(() => undefined) } as Transition;
  }) as never;
}

describe("crossFade", () => {
  it("runs the change at once where the browser has no view transitions", async () => {
    const change = vi.fn();
    const done = crossFade(change);
    expect(change).toHaveBeenCalledTimes(1);
    await done;
  });

  it("runs the change inside the transition", async () => {
    const change = vi.fn();
    install();
    await crossFade(change);
    expect(change).toHaveBeenCalledTimes(1);
  });

  it("keeps the page's own reduced motion for the length of the transition, although the change removes it", async () => {
    root.setAttribute("data-atlas-reduced-motion", "");
    let finish!: () => void;
    install({ finished: new Promise<void>((resolve) => { finish = resolve; }) });
    // A sign-out resets the appearance inside the update, which removes the attribute.
    await crossFade(() => root.removeAttribute("data-atlas-reduced-motion"));
    expect(root.hasAttribute("data-atlas-reduced-motion")).toBe(false);
    expect(root.hasAttribute("data-crossfade-reduced")).toBe(true);
    finish();
    await Promise.resolve(); await Promise.resolve();
    expect(root.hasAttribute("data-crossfade-reduced")).toBe(false);
  });

  it("does not mark a transition of somebody who did not ask for less motion", async () => {
    install();
    const marking = crossFade(() => undefined);
    expect(root.hasAttribute("data-crossfade-reduced")).toBe(false);
    await marking;
  });

  it("applies the change once and reports nothing when the browser skips the transition", async () => {
    root.setAttribute("data-atlas-reduced-motion", "");
    install({ skipped: true });
    const change = vi.fn();
    await crossFade(change);
    await settle();
    expect(change).toHaveBeenCalledTimes(1);
    expect(unhandled).toEqual([]);
    expect(root.hasAttribute("data-crossfade-reduced")).toBe(false);
  });

  it("lets a failing change reach the caller once, and releases the mark, inside a transition", async () => {
    root.setAttribute("data-atlas-reduced-motion", "");
    install();
    const change = vi.fn(() => { throw new Error("reset failed"); });
    await expect(crossFade(change)).rejects.toThrow("reset failed");
    await settle();
    expect(change).toHaveBeenCalledTimes(1);
    expect(unhandled).toEqual([]);
    expect(root.hasAttribute("data-crossfade-reduced")).toBe(false);
  });

  it("lets a failing change reach the caller once when the transition was skipped as well", async () => {
    install({ skipped: true });
    const change = vi.fn(() => { throw new Error("reset failed"); });
    await expect(crossFade(change)).rejects.toThrow("reset failed");
    await settle();
    expect(change).toHaveBeenCalledTimes(1);
    expect(unhandled).toEqual([]);
  });

  it("lets a failing change reject without view transitions, as it did before the cross-fade existed", async () => {
    const change = vi.fn(() => { throw new Error("reset failed"); });
    await expect(crossFade(change)).rejects.toThrow("reset failed");
    expect(change).toHaveBeenCalledTimes(1);
  });

  describe("a click during the fade", () => {
    // Chromium hit-tests the live document as <html> for the whole fade (also with pointer-events: none on the overlay), so a
    // click on a control of the arriving screen lands on <html> and does nothing.
    let finish!: () => void;
    const skip = vi.fn();
    const elementFromPoint = vi.fn();
    beforeEach(() => {
      skip.mockClear();
      elementFromPoint.mockReset();
      (document as { elementFromPoint?: unknown }).elementFromPoint = elementFromPoint;
      document.startViewTransition = ((update: () => Promise<void> | void) => {
        const updated = Promise.resolve().then(update);
        return { ready: updated.then(() => undefined), finished: new Promise<void>((resolve) => { finish = resolve; }), updateCallbackDone: updated.then(() => undefined), skipTransition: skip } as never;
      }) as never;
    });
    afterEach(() => { delete (document as { elementFromPoint?: unknown }).elementFromPoint; });
    const nextFrame = () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));

    it("skips the fade and activates the control under the pointer when the click landed on the document", async () => {
      const button = document.createElement("button");
      const clicked = vi.fn();
      button.addEventListener("click", clicked);
      document.body.append(button);
      elementFromPoint.mockReturnValue(button);
      await crossFade(() => undefined);
      root.dispatchEvent(new MouseEvent("click", { bubbles: true, clientX: 120, clientY: 80 }));
      expect(skip).toHaveBeenCalledTimes(1);
      await nextFrame();
      expect(elementFromPoint).toHaveBeenCalledWith(120, 80);
      expect(clicked).toHaveBeenCalledTimes(1);
      finish();
      button.remove();
    });

    it("activates a control whose icon is under the pointer, and keeps the click's place and modifier keys", async () => {
      // elementFromPoint answers the icon inside the control: an SVG element, which has no click() of its own.
      const link = document.createElement("a");
      const icon = document.createElementNS("http://www.w3.org/2000/svg", "svg");
      const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
      icon.append(path);
      link.append(icon);
      const clicks: MouseEvent[] = [];
      link.addEventListener("click", (event) => { event.preventDefault(); clicks.push(event); });
      document.body.append(link);
      elementFromPoint.mockReturnValue(path);
      await crossFade(() => undefined);
      root.dispatchEvent(new MouseEvent("click", { bubbles: true, clientX: 27, clientY: 153, ctrlKey: true }));
      await nextFrame();
      expect(clicks).toHaveLength(1);
      expect(clicks[0]!.target).toBe(path);
      expect([clicks[0]!.clientX, clicks[0]!.clientY, clicks[0]!.ctrlKey]).toEqual([27, 153, true]);
      finish();
      link.remove();
    });

    it("toggles a checkbox under the pointer instead of only focusing it", async () => {
      const box = document.createElement("input");
      box.type = "checkbox";
      document.body.append(box);
      elementFromPoint.mockReturnValue(box);
      await crossFade(() => undefined);
      root.dispatchEvent(new MouseEvent("click", { bubbles: true, clientX: 10, clientY: 10 }));
      await nextFrame();
      expect(box.checked).toBe(true);
      finish();
      box.remove();
    });

    it("focuses a field under the pointer instead of clicking it", async () => {
      const field = document.createElement("input");
      document.body.append(field);
      elementFromPoint.mockReturnValue(field);
      await crossFade(() => undefined);
      root.dispatchEvent(new MouseEvent("click", { bubbles: true, clientX: 10, clientY: 10 }));
      await nextFrame();
      expect(document.activeElement).toBe(field);
      finish();
      field.remove();
    });

    it("leaves a click that reached a control alone and stops listening when the fade is over", async () => {
      const button = document.createElement("button");
      const clicked = vi.fn();
      button.addEventListener("click", clicked);
      document.body.append(button);
      await crossFade(() => undefined);
      button.dispatchEvent(new MouseEvent("click", { bubbles: true, clientX: 5, clientY: 5 }));
      await nextFrame();
      expect(skip).not.toHaveBeenCalled();
      expect(clicked).toHaveBeenCalledTimes(1);
      finish();
      await settle();
      root.dispatchEvent(new MouseEvent("click", { bubbles: true, clientX: 5, clientY: 5 }));
      expect(skip).not.toHaveBeenCalled();
      button.remove();
    });
  });

  it("runs the change directly when the browser refuses to start a transition", async () => {
    root.setAttribute("data-atlas-reduced-motion", "");
    document.startViewTransition = (() => { throw new DOMException("Document is not fully active", "InvalidStateError"); }) as never;
    const change = vi.fn();
    await crossFade(change);
    expect(change).toHaveBeenCalledTimes(1);
    expect(root.hasAttribute("data-crossfade-reduced")).toBe(false);
  });
});
