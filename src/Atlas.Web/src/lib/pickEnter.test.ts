import { expect, it, vi } from "vitest";
import { focusFirst, isPickEnter, pickOnEnter } from "./pickEnter";

const key = (init: KeyboardEventInit) => new KeyboardEvent("keydown", { cancelable: true, ...init });

it("picks on a plain Enter only: not an IME composition, not with a modifier, not another key", () => {
  expect(isPickEnter(key({ key: "Enter" }))).toBe(true);
  expect(isPickEnter(key({ key: "Enter", isComposing: true }))).toBe(false);
  // Safari reports the composition's Enter by keyCode 229 alone.
  expect(isPickEnter(key({ key: "Enter", keyCode: 229 }))).toBe(false);
  for (const modifier of ["ctrlKey", "metaKey", "altKey", "shiftKey"]) expect(isPickEnter(key({ key: "Enter", [modifier]: true }))).toBe(false);
  expect(isPickEnter(key({ key: " " }))).toBe(false);
});

it("pickOnEnter takes a plain Enter while something is typed and picks; otherwise it leaves the key alone", () => {
  const pick = vi.fn();
  const plain = key({ key: "Enter" });
  pickOnEnter(plain, true, pick);
  expect(pick).toHaveBeenCalledTimes(1);
  expect(plain.defaultPrevented).toBe(true);

  const untyped = key({ key: "Enter" });
  const modified = key({ key: "Enter", ctrlKey: true });
  pickOnEnter(untyped, false, pick);
  pickOnEnter(modified, true, pick);
  expect(pick).toHaveBeenCalledTimes(1);
  expect(untyped.defaultPrevented || modified.defaultPrevented).toBe(false);
});

it("focusFirst focuses the preselected [data-first] row, else the fallback, and is silent without a container", () => {
  const box = document.createElement("div");
  box.innerHTML = "<button data-row>one</button><button data-row data-first>two</button>";
  document.body.append(box);
  focusFirst(box, "[data-row]");
  expect(document.activeElement?.textContent).toBe("two");
  box.querySelector("[data-first]")!.removeAttribute("data-first");
  focusFirst(box, "[data-row]");
  expect(document.activeElement?.textContent).toBe("one");
  expect(() => focusFirst(undefined, "[data-row]")).not.toThrow();
  box.remove();
});
