import { describe, expect, it, vi } from "vitest";
import { nextTick, ref } from "vue";
import { clearOnEscape, useSearchKeys } from "./searchKeys";

const key = (name: string, init: KeyboardEventInit = {}) => new KeyboardEvent("keydown", { key: name, cancelable: true, bubbles: true, ...init });

function setup(more: Partial<Parameters<typeof useSearchKeys>[0]> = {}) {
  const text = ref(""), taken: string[] = [];
  const keys = useSearchKeys({ text: () => text.value, clear: () => { text.value = ""; }, take: () => { taken.push(text.value); }, ...more });
  return { text, taken, keys };
}

describe("clearOnEscape", () => {
  it("empties a typed text and swallows the Esc; with nothing typed the Esc goes on", () => {
    const clear = vi.fn();
    const typed = key("Escape");
    expect(clearOnEscape(typed, "ab", clear)).toBe(true);
    expect(clear).toHaveBeenCalledTimes(1);
    expect(typed.defaultPrevented).toBe(true);
    const empty = key("Escape");
    expect(clearOnEscape(empty, "", clear)).toBe(false);
    expect(empty.defaultPrevented).toBe(false);
    expect(clearOnEscape(key("Escape", { ctrlKey: true }), "ab", clear)).toBe(false);
    expect(clearOnEscape(key("Enter"), "ab", clear)).toBe(false);
  });
});

describe("useSearchKeys", () => {
  it("Esc empties the text first and leaves the next Esc to the owner", () => {
    const { text, keys } = setup();
    text.value = "jan";
    expect(keys.onKeydown(key("Escape"))).toBe(true);
    expect(text.value).toBe("");
    const second = key("Escape");
    expect(keys.onKeydown(second)).toBe(false);
    expect(second.defaultPrevented).toBe(false);
  });

  it("Enter takes the first result once something is typed, never while composing or with a modifier, and nothing from an empty field", () => {
    const { text, taken, keys } = setup();
    const empty = key("Enter");
    expect(keys.onKeydown(empty)).toBe(false);
    expect(empty.defaultPrevented).toBe(false);
    text.value = "  ";
    expect(keys.onKeydown(key("Enter"))).toBe(false);
    text.value = "jan";
    expect(keys.onKeydown(key("Enter", { isComposing: true }))).toBe(false);
    expect(keys.onKeydown(key("Enter", { ctrlKey: true }))).toBe(false);
    expect(keys.onKeydown(key("Enter", { shiftKey: true }))).toBe(false);
    const plain = key("Enter");
    expect(keys.onKeydown(plain)).toBe(true);
    expect(plain.defaultPrevented).toBe(true);
    expect(taken).toEqual(["jan"]);
  });

  it("an empty field's Enter is the owner's when it says so (confirms what is ticked), and swallowed", () => {
    const empty = vi.fn(() => true);
    const { keys } = setup({ empty });
    const event = key("Enter");
    expect(keys.onKeydown(event)).toBe(true);
    expect(empty).toHaveBeenCalledTimes(1);
    expect(event.defaultPrevented).toBe(true);
  });

  it("once the arrows moved, Enter takes that row with nothing typed, and `lit` says a row is highlighted only then (or once something is typed)", () => {
    const empty = vi.fn(() => true);
    const { text, taken, keys } = setup({ empty });
    expect(keys.lit.value).toBe(false);
    keys.move();
    expect(keys.lit.value).toBe(true);
    const enter = key("Enter");
    expect(keys.onKeydown(enter)).toBe(true);
    expect(enter.defaultPrevented).toBe(true);
    expect(taken).toEqual([""]);
    expect(empty).not.toHaveBeenCalled();
    // Typing starts the next search: the moved row is forgotten, then the typed text lights the first row.
    text.value = "j";
    text.value = "";
    expect(keys.lit.value).toBe(false);
    keys.onKeydown(key("Enter"));
    expect(empty).toHaveBeenCalledTimes(1);
    text.value = "jan";
    expect(keys.lit.value).toBe(true);
  });

  it("Tab takes the first result and goes on to the next field; keepFocus keeps it where it is; Shift+Tab and an empty field take nothing", () => {
    const { text, taken, keys } = setup();
    expect(keys.onKeydown(key("Tab"))).toBe(false);
    text.value = "jan";
    expect(keys.onKeydown(key("Tab", { shiftKey: true }))).toBe(false);
    const tab = key("Tab");
    expect(keys.onKeydown(tab)).toBe(true);
    expect(tab.defaultPrevented).toBe(false);
    expect(taken).toEqual(["jan"]);
    const kept = setup({ keepFocus: true });
    kept.text.value = "jan";
    const held = key("Tab");
    expect(kept.keys.onKeydown(held)).toBe(true);
    expect(held.defaultPrevented).toBe(true);
    const off = setup({ tab: false });
    off.text.value = "jan";
    expect(off.keys.onKeydown(key("Tab"))).toBe(false);
    expect(off.taken).toEqual([]);
  });

  it("a result that is not there takes nothing and the Tab still goes on", () => {
    const { text, keys } = setup({ take: () => false });
    text.value = "zzz";
    const tab = key("Tab");
    expect(keys.onKeydown(tab)).toBe(false);
    expect(tab.defaultPrevented).toBe(false);
  });

  it("an Enter given while the answer is on its way waits for it (a reactive flag), and typing on forgets it", async () => {
    const loading = ref(false);
    const { text, taken, keys } = setup({ waiting: () => loading.value });
    text.value = "jan";
    loading.value = true;
    await nextTick();
    expect(keys.onKeydown(key("Enter"))).toBe(true);
    expect(taken).toEqual([]);
    loading.value = false;
    await nextTick();
    expect(taken).toEqual(["jan"]);

    loading.value = true;
    await nextTick();
    keys.onKeydown(key("Enter"));
    text.value = "janu";
    loading.value = false;
    await nextTick();
    expect(taken).toEqual(["jan"]);
  });

  it("a wait on a plain flag ends with settled(); ahead() is an Enter that came before the picker did", () => {
    let waiting = true;
    const { text, taken, keys } = setup({ waiting: () => waiting });
    text.value = "jan";
    keys.onKeydown(key("Enter"));
    waiting = false;
    keys.settled();
    keys.settled();
    expect(taken).toEqual(["jan"]);
    text.value = "ma";
    keys.ahead();
    keys.settled();
    expect(taken).toEqual(["jan", "ma"]);
  });

  it("with `fresh` the wait ends when the rows were replaced, not when the flag dropped for an instant between debounce and request", async () => {
    const loading = ref(true), options = ref<string[]>(["old"]);
    const { text, taken, keys } = setup({ waiting: () => loading.value, fresh: () => options.value, take: () => { taken.push(options.value.join()); } });
    text.value = "jan";
    keys.onKeydown(key("Enter"));
    loading.value = false;
    await nextTick();
    expect(taken).toEqual([]);
    loading.value = true;
    await nextTick();
    options.value = ["new"];
    loading.value = false;
    await nextTick();
    expect(taken).toEqual(["new"]);
  });
});
