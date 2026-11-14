import { afterEach, describe, expect, it } from "vitest";
import { rememberFocus } from "./focusReturn";

afterEach(() => document.body.replaceChildren());
const button = (text: string) => { const element = document.createElement("button"); element.textContent = text; document.body.append(element); return element; };

describe("rememberFocus", () => {
  it("gives the keyboard back to the opener, else to the fallback, never leaving it on <body>", () => {
    const opener = button("Otevřít"), fallback = button("Plocha");
    opener.focus();
    const restore = rememberFocus(() => fallback);
    button("Pole v panelu").focus();
    expect(restore()).toBe(true);
    expect(document.activeElement).toBe(opener);

    const gone = rememberFocus(() => fallback);
    opener.remove();
    expect(gone()).toBe(true);
    expect(document.activeElement).toBe(fallback);

    (document.activeElement as HTMLElement).blur();
    expect(rememberFocus()()).toBe(false);
  });
});
