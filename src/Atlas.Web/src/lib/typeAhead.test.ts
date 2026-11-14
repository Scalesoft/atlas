import { expect, it } from "vitest";
import { captureTypeAhead } from "./typeAhead";

const press = (key: string, more: KeyboardEventInit = {}) => {
  const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...more });
  document.body.dispatchEvent(event);
  return event.defaultPrevented;
};

it("holds back what is typed until the picker takes it, Enter included, and then lets keys through", () => {
  const typed = captureTypeAhead(document);
  for (const key of "jan kovarx") press(key);
  press("Backspace");
  for (const key of "ik") press(key);
  expect(press("c", { ctrlKey: true })).toBe(false);
  expect(press("Enter")).toBe(true);
  expect(typed.take()).toEqual({ text: "jan kovarik", enter: true });
  expect(press("x")).toBe(false);
});

it("holds back text put in without keys too (insertText), so it reaches the picker and not the paragraph", () => {
  const input = (inputType: string, data: string | null = null) => {
    const event = new InputEvent("beforeinput", { inputType, data, bubbles: true, cancelable: true });
    document.body.dispatchEvent(event);
    return event.defaultPrevented;
  };
  const typed = captureTypeAhead(document);
  expect(input("insertText", "jan ")).toBe(true);
  press("k");
  input("insertText", "ovarx");
  input("deleteContentBackward");
  input("insertText", "ik");
  expect(input("insertParagraph")).toBe(true);
  expect(typed.take()).toEqual({ text: "jan kovarik", enter: true });
  expect(input("insertText", "x")).toBe(false);
});
