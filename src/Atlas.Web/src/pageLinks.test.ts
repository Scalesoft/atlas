import { describe, expect, it } from "vitest";
import { isPlainNavigationClick } from "./pageLinks";

describe("navigation clicks", () => {
  it("accepts only an unhandled, unmodified primary-button click", () => {
    expect(isPlainNavigationClick(new MouseEvent("click", { button: 0 }))).toBe(true);
    expect(isPlainNavigationClick(new MouseEvent("click", { button: 1 }))).toBe(false);
    expect(isPlainNavigationClick(new MouseEvent("click", { button: 0, ctrlKey: true }))).toBe(false);
    expect(isPlainNavigationClick(new MouseEvent("click", { button: 0, metaKey: true }))).toBe(false);
    expect(isPlainNavigationClick(new MouseEvent("click", { button: 0, shiftKey: true }))).toBe(false);
    expect(isPlainNavigationClick(new MouseEvent("click", { button: 0, altKey: true }))).toBe(false);

    const prevented = new MouseEvent("click", { button: 0, cancelable: true });
    prevented.preventDefault();
    expect(isPlainNavigationClick(prevented)).toBe(false);
  });
});
