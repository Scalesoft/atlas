import { afterEach, describe, expect, it, vi } from "vitest";
import { appShortcuts, chordLabel, createShortcutMatcher, helpSections, type AppShortcut } from "./shortcuts";

afterEach(() => document.body.replaceChildren());

function press(target: Element, key: string, init: KeyboardEventInit = {}) {
  const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...init });
  Object.defineProperty(event, "target", { value: target });
  return event;
}
function element(html: string) {
  const host = document.createElement("div");
  host.innerHTML = html;
  document.body.append(host);
  return host.firstElementChild!;
}
const ids = (list: readonly AppShortcut[]) => list.map(item => item.id);

describe("one shortcut registry", () => {
  it("gives every shortcut a unique id and chord", () => {
    expect(new Set(ids(appShortcuts)).size).toBe(appShortcuts.length);
    expect(new Set(appShortcuts.map(item => item.chord)).size).toBe(appShortcuts.length);
  });

  it("offers a G shortcut for every part of Knihovna that has a sidebar entry", () => {
    const goTo = Object.fromEntries(appShortcuts.filter(item => item.group === "Přejít na").map(item => [item.label, item.chord]));
    expect(goTo).toMatchObject({ "Můj prostor": "g u", "Sdíleno se mnou": "g d", "Oznámení": "g i", "Kontakty": "g c" });
  });

  it("claims no browser or Windows key a page must leave alone", () => {
    // Ctrl+T/W/N/L/R/D/H/P/Tab, Alt+F4, F5 and the like stay the browser's and the system's. Ctrl+F is the owner's exception (3.5 J):
    // on an open page it opens Hledat ve stránce, which also finds hidden text; elsewhere it stays the browser's find.
    const reserved = ["Mod+t", "Mod+w", "Mod+n", "Mod+l", "Mod+r", "Mod+d", "Mod+h", "Mod+p", "Mod+Tab", "F5", "Alt+F4", "Mod+Shift+t", "Mod+Shift+n"];
    for (const shortcut of appShortcuts) expect(reserved).not.toContain(shortcut.chord);
    expect(appShortcuts.filter(shortcut => shortcut.chord === "Mod+f").map(shortcut => shortcut.id)).toEqual(["findInPage"]);
  });

  it("names chords the Windows way", () => {
    expect(chordLabel("Mod+k")).toBe("Ctrl K");
    expect(chordLabel("Mod+Shift+p")).toBe("Ctrl Shift P");
    expect(chordLabel("g k")).toBe("G, pak K");
    expect(chordLabel("?")).toBe("?");
  });

  it("names chords the Mac way on a Mac", () => {
    vi.spyOn(navigator, "platform", "get").mockReturnValue("MacIntel");
    try {
      expect(chordLabel("Mod+k")).toBe("⌘ K");
      expect(chordLabel("Alt+c")).toBe("⌥ C");
    } finally { vi.restoreAllMocks(); }
  });

  it("groups the help by context, with the editor's own keymap as its section", () => {
    const sections = helpSections([{ keys: "Ctrl B", label: "Tučně", group: "Formát" }]);
    expect(sections.map(section => section.title)).toEqual(["Kdekoli", "Přejít na", "Stránka", "Seznamy, stromy a nabídky", "Editor"]);
    expect(sections.at(-1)!.entries).toEqual([{ keys: "Ctrl B", label: "Tučně", group: "Formát" }]);
    // Without the editor's export the help still lists the application's keys.
    expect(helpSections([]).map(section => section.title)).not.toContain("Editor");
  });

  it("lists the requirement blocks' keys as „Požadavky“ after the editor, only when given (PR-4)", () => {
    const row = { keys: "Shift Tab", label: "Vynořit", group: "Požadavky" };
    expect(helpSections([{ keys: "Ctrl B", label: "Tučně", group: "Formát" }], [row]).slice(-2).map(section => section.title)).toEqual(["Editor", "Požadavky"]);
    expect(helpSections([], [row]).at(-1)).toEqual({ title: "Požadavky", entries: [row] });
    expect(helpSections([]).map(section => section.title)).not.toContain("Požadavky");
  });

  it("closes with the board's keymap under „Tabule“, also without the editor's", () => {
    const board = [{ keys: "N", label: "Poznámka", group: "Tabule" }];
    expect(helpSections([], [], board).at(-1)).toEqual({ title: "Tabule", entries: board });
    expect(helpSections([{ keys: "Ctrl B", label: "Tučně", group: "Formát" }], [], board).slice(-2).map(section => section.title)).toEqual(["Editor", "Tabule"]);
  });
});

describe("matching a key press", () => {
  it("matches Ctrl+K anywhere, also in a field and in the editor", () => {
    const match = createShortcutMatcher(appShortcuts);
    const field = element("<input>");
    const editor = element('<div contenteditable="true"></div>');
    expect(match(press(field, "k", { ctrlKey: true }))?.id).toBe("search");
    expect(match(press(editor, "K", { ctrlKey: true }))?.id).toBe("search");
    expect(match(press(field, "P", { ctrlKey: true, shiftKey: true }))?.id).toBe("commands");
    // The search's dialog ⇄ page switch; App runs it only while the search is open.
    expect(match(press(field, "Enter", { ctrlKey: true, shiftKey: true }))?.id).toBe("searchView");
    expect(match(press(field, "Enter", { ctrlKey: true }))).toBeUndefined();
    expect(chordLabel("Mod+Shift+Enter")).toBe("Ctrl Shift Enter");
  });

  it("fires single keys only outside fields, the editor and widgets with their own letters", () => {
    const match = createShortcutMatcher(appShortcuts);
    expect(match(press(document.body, "?", { shiftKey: true }))?.id).toBe("help");
    expect(match(press(document.body, "n"))?.id).toBe("newPage");
    for (const html of ["<input>", "<textarea></textarea>", '<div contenteditable="true"></div>', '<div role="tree"><a role="treeitem">x</a></div>', '<div role="menu"></div>', '<div data-board-surface tabindex="0"></div>', '<select></select>']) {
      const target = element(html);
      expect(match(press(target.querySelector("a") ?? target, "n")), html).toBeUndefined();
    }
  });

  it("refuses a letter with Alt (AltGr writes characters on a Czech keyboard) or with Ctrl", () => {
    const match = createShortcutMatcher(appShortcuts);
    expect(match(press(document.body, "e", { altKey: true }))).toBeUndefined();
    expect(match(press(document.body, "e", { ctrlKey: true, altKey: true }))).toBeUndefined();
    expect(match(press(document.body, "n", { ctrlKey: true }))).toBeUndefined();
  });

  it("opens the page's discussion with Alt C by the physical key, also in the editor, and never with AltGr or another modifier", () => {
    const match = createShortcutMatcher(appShortcuts);
    const alt = (target: Element, init: KeyboardEventInit = {}) => press(target, "c", { altKey: true, code: "KeyC", ...init });
    expect(match(alt(document.body))?.id).toBe("discussion");
    expect(match(alt(element('<div contenteditable="true"></div>')))?.id).toBe("discussion");
    expect(match(press(document.body, "ç", { altKey: true, code: "KeyC" }))?.id).toBe("discussion");
    expect(match(alt(document.body, { ctrlKey: true }))).toBeUndefined();
    expect(match(alt(document.body, { shiftKey: true }))).toBeUndefined();
    expect(match(press(document.body, "c", { code: "KeyC" }))).toBeUndefined();
    expect(chordLabel("Alt+c")).toBe("Alt C");
  });

  it("opens the help with Ctrl+/ outside the editor only", () => {
    const match = createShortcutMatcher(appShortcuts);
    expect(match(press(document.body, "/", { ctrlKey: true }))?.id).toBe("helpAlt");
    expect(match(press(element("<input>"), "/", { ctrlKey: true }))?.id).toBe("helpAlt");
    expect(match(press(element('<div contenteditable="true"></div>'), "/", { ctrlKey: true }))).toBeUndefined();
    // A board leaves Ctrl+/ alone (its / is the command panel), so the help opens there too.
    expect(match(press(element('<div data-board-surface tabindex="0"></div>'), "/", { ctrlKey: true }))?.id).toBe("helpAlt");
  });

  it("follows a G with its letter within a moment, and forgets it after", () => {
    const match = createShortcutMatcher(appShortcuts, { sequenceMs: 1500 });
    expect(match(press(document.body, "g"), 1000)).toBeUndefined();
    expect(match(press(document.body, "k"), 1800)?.id).toBe("goLibrary");
    expect(match(press(document.body, "g"), 5000)).toBeUndefined();
    expect(match(press(document.body, "k"), 7000)).toBeUndefined();
  });

  it("does not let an unrelated key complete a sequence", () => {
    const match = createShortcutMatcher(appShortcuts);
    expect(match(press(document.body, "g"), 0)).toBeUndefined();
    expect(match(press(document.body, "x"), 10)).toBeUndefined();
    expect(match(press(document.body, "k"), 20)).toBeUndefined();
  });

  it("ignores key presses of an input method that is composing", () => {
    const match = createShortcutMatcher(appShortcuts);
    expect(match(press(document.body, "n", { isComposing: true }))).toBeUndefined();
  });
});
