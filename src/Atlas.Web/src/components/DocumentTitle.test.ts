import { afterEach, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { nextTick } from "vue";
import DocumentTitle from "./DocumentTitle.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); vi.unstubAllGlobals(); });

function title(modelValue = "Příručka pro české uživatele") {
  wrapper = mount(DocumentTitle, {
    props: { modelValue, "onUpdate:modelValue": (value: string) => wrapper!.setProps({ modelValue: value }) },
    attachTo: document.body,
  });
  return wrapper;
}

describe("document title editing", () => {
  it('renders the optional icon without changing the heading text', () => {
    wrapper = mount(DocumentTitle, { props: { modelValue: 'Dokumentace', readonly: true, decoration: { revisionId: 'r1', icon: 'book-open', color: 'blue' } } });
    expect(wrapper.get('h1').text()).toBe('Dokumentace');
    expect(wrapper.get('h1 .page-decoration-icon').attributes('data-color')).toBe('blue');
  });
  it("exposes a wrapping field and renders the full title as a heading in read mode", async () => {
    const view = title();
    expect(view.get('textarea[aria-label="Název stránky"]').attributes("rows")).toBe("1");
    await view.setProps({ readonly: true });
    expect(view.get("h1").text()).toBe("Příručka pro české uživatele");
    expect(view.find("textarea").exists()).toBe(false);
    expect(view.emitted("update:modelValue")).toBeUndefined();
  });
  it("preserves spaces, Czech text, focus and a cursor within the title", async () => {
    const view = title("Průvodce");
    const input = view.get("textarea");
    input.element.focus();
    input.element.value = "  Český průvodce  ";
    input.element.setSelectionRange(7, 7);
    await input.trigger("input");
    expect(view.emitted("update:modelValue")!.at(-1)).toEqual(["  Český průvodce  "]);
    expect(document.activeElement).toBe(input.element);
    expect(input.element.selectionStart).toBe(7);
    await input.trigger("blur");
    expect(input.element.value).toBe("  Český průvodce  ");
    await view.setProps({ modelValue: "Jiná stránka" });
    expect(input.element.value).toBe("Jiná stránka");
  });
  it("normalizes multiline paste and preserves the insertion point before following text", async () => {
    const view = title("Začátek konec");
    const input = view.get("textarea");
    input.element.value = "Začátek první\n\ndruhý\ntřetí konec";
    input.element.setSelectionRange(26, 26);
    await input.trigger("input", { inputType: "insertFromPaste" });
    expect(input.element.value).toBe("Začátek první druhý třetí konec");
    expect(input.element.selectionStart).toBe(25);
    expect(view.emitted("update:modelValue")!.at(-1)).toEqual(["Začátek první druhý třetí konec"]);
  });
  it("prevents only Enter newline outside composition", async () => {
    const view = title();
    const input = view.get("textarea");
    const enter = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true });
    input.element.dispatchEvent(enter);
    expect(enter.defaultPrevented).toBe(true);
    const composing = new KeyboardEvent("keydown", { key: "Enter", isComposing: true, bubbles: true, cancelable: true });
    input.element.dispatchEvent(composing);
    expect(composing.defaultPrevented).toBe(false);
  });
  it("moves on to the text with Enter, or with Down at the end of the title (keyboard pass)", async () => {
    const view = title("Návod");
    const input = view.get("textarea");
    input.element.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    expect(view.emitted("continue")).toHaveLength(1);
    input.element.setSelectionRange(2, 2);
    input.element.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true, cancelable: true }));
    expect(view.emitted("continue")).toHaveLength(1);
    input.element.setSelectionRange(5, 5);
    const down = new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true, cancelable: true });
    input.element.dispatchEvent(down);
    expect(down.defaultPrevented).toBe(true);
    expect(view.emitted("continue")).toHaveLength(2);
    input.element.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", isComposing: true, bubbles: true, cancelable: true }));
    expect(view.emitted("continue")).toHaveLength(2);
  });
  it("leaves an IME composition untouched until it commits", async () => {
    const view = title("Text");
    const input = view.get("textarea");
    await input.trigger("compositionstart");
    input.element.value = "日本\n語";
    await input.trigger("input", { isComposing: true });
    expect(view.emitted("update:modelValue")).toBeUndefined();
    expect(input.element.value).toBe("日本\n語");
    const enter = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true });
    input.element.dispatchEvent(enter);
    expect(enter.defaultPrevented).toBe(false);
    await input.trigger("compositionend");
    expect(view.emitted("update:modelValue")!.at(-1)).toEqual(["日本 語"]);
  });
  it("resizes for content and available width, then releases the observer, where CSS cannot (no field-sizing)", async () => {
    vi.stubGlobal("CSS", { supports: () => false });
    let resized!: ResizeObserverCallback;
    const observe = vi.fn(), disconnect = vi.fn();
    vi.stubGlobal("ResizeObserver", class {
      constructor(callback: ResizeObserverCallback) { resized = callback; }
      observe = observe;
      disconnect = disconnect;
    });
    const view = title();
    await nextTick();
    const input = view.get("textarea").element;
    let height = 96;
    Object.defineProperty(input, "scrollHeight", { get: () => height });
    await view.setProps({ modelValue: "Delší název stránky" });
    expect(input.style.height).toBe("96px");
    height = 144;
    resized([{ target: input, contentRect: new DOMRect(0, 0, 320, 144),
      borderBoxSize: [], contentBoxSize: [], devicePixelContentBoxSize: [],
    }], {} as ResizeObserver);
    expect(input.style.height).toBe("96px");
    await new Promise(frame => requestAnimationFrame(frame));
    expect(input.style.height).toBe("144px");
    expect(observe).toHaveBeenCalledWith(input);
    view.unmount();
    wrapper = undefined;
    expect(disconnect).toHaveBeenCalled();
  });
  it("leaves the height to CSS where field-sizing is supported: nothing measured, nothing observed (perf guard)", async () => {
    vi.stubGlobal("CSS", { supports: (property: string, value: string) => property === "field-sizing" && value === "content" });
    const observe = vi.fn();
    vi.stubGlobal("ResizeObserver", class { observe = observe; disconnect = vi.fn(); });
    const view = title();
    await view.setProps({ modelValue: "Delší název stránky" });
    expect(view.get("textarea").element.style.height).toBe("");
    expect(observe).not.toHaveBeenCalled();
  });
});

describe("document title root", () => {
  it("puts class and style on the root in both modes and the other attributes on the field", async () => {
    wrapper = mount(DocumentTitle, { props: { modelValue: "Plán" }, attrs: { class: "custom", style: "margin: 0", maxlength: "200" }, attachTo: document.body });
    expect(wrapper.get(".document-title-input").classes()).toContain("custom");
    expect(wrapper.get(".document-title-input").attributes("style")).toContain("margin");
    expect(wrapper.get("textarea").classes()).not.toContain("custom");
    expect(wrapper.get("textarea").attributes("maxlength")).toBe("200");
    await wrapper.setProps({ readonly: true });
    expect(wrapper.get("h1").classes()).toContain("custom");
  });
});
