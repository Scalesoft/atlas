import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import CopyPageLink from "./CopyPageLink.vue";
import { FEEDBACK_HOLD_MS } from "../motion/aiMotion";
const originalClipboard = Object.getOwnPropertyDescriptor(navigator, "clipboard");
afterEach(() => {
  vi.restoreAllMocks(); history.replaceState(null, "", "/");
  if (originalClipboard) Object.defineProperty(navigator, "clipboard", originalClipboard);
  else Reflect.deleteProperty(navigator, "clipboard");
});
describe("copy article link", () => {
  it("copies the current article without search terms or a stale section fragment", async () => {
    history.replaceState(null, "", "/?search=1&q=private#/pages/old?heading=section");
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
    const app = mount(CopyPageLink, { props: { pageId: "page-2" } });
    const button = app.get("button");
    expect(button.get('svg').attributes("data-icon")).toBe("link");
    expect(button.text()).toBe("");
    expect(button.attributes("aria-label")).toBe("Kopírovat odkaz na stránku");
    await button.trigger("click"); await flushPromises();
    expect(writeText).toHaveBeenCalledWith(`${location.origin}/#/pages/page-2`);
    expect(app.get('[role="status"]').text()).toBe("Odkaz zkopírován");
    expect(app.get("svg").attributes("data-icon")).toBe("check");
    expect(location.search).toContain("search=1"); app.unmount();
  });
  it("copies an explicit section URL with its text action and expires feedback when the selection changes", async () => {
    let finish!: () => void;
    const writeText = vi.fn(() => new Promise<void>(resolve => { finish = resolve; }));
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
    const href = `${location.origin}/#/pages/page-1?heading=access`;
    const app = mount(CopyPageLink, { props: { href, label: "Kopírovat odkaz na sekci", text: true } });
    const button = app.get("button");
    expect(button.text()).toBe("Kopírovat odkaz na sekci");
    await button.trigger("click");
    expect(writeText).toHaveBeenCalledWith(href);
    await app.setProps({ href: `${location.origin}/#/pages/page-1?heading=next` });
    finish(); await flushPromises();
    expect(app.find('[role="status"]').exists()).toBe(false);
    app.unmount();
  });

  it("reverts the check icon to link on its own after a short accent tint", async () => {
    vi.useFakeTimers();
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: vi.fn().mockResolvedValue(undefined) } });
    const app = mount(CopyPageLink, { props: { pageId: "page-1" } });
    await app.get("button").trigger("click");
    await vi.advanceTimersByTimeAsync(0);
    expect(app.get("svg").attributes("data-icon")).toBe("check");
    await vi.advanceTimersByTimeAsync(FEEDBACK_HOLD_MS);
    expect(app.get("svg").attributes("data-icon")).toBe("link");
    expect(app.find('[role="status"]').exists()).toBe(false);
    app.unmount(); vi.useRealTimers();
  });
  it("offers a selected manual fallback on clipboard denial and restores focus on Escape", async () => {
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: vi.fn().mockRejectedValue(new Error("Denied")) } });
    const app = mount(CopyPageLink, { props: { pageId: "page-1" }, attachTo: document.body });
    await app.get("button").trigger("click"); await flushPromises();
    await new Promise(resolve => setTimeout(resolve, 50));
    // The fallback is portalled now, so it lives in the document rather than
    // inside this wrapper. Every assertion below is unchanged.
    const field = () => document.querySelector<HTMLInputElement>('input[aria-label="Odkaz na stránku"]');
    const input = field()!;
    expect(document.activeElement).toBe(input); expect(input.selectionEnd).toBe(input.value.length);
    expect(input.readOnly).toBe(true);
    input.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    await flushPromises();
    await new Promise(resolve => setTimeout(resolve, 50));
    expect(field()).toBeNull(); expect(document.activeElement).toBe(app.get("button").element); app.unmount();
  });
  it("returns focus to where the copy was asked from when the button is hidden", async () => {
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: vi.fn().mockRejectedValue(new Error("Denied")) } });
    const workspace = document.createElement("main");
    workspace.tabIndex = -1;
    document.body.append(workspace);
    const app = mount(CopyPageLink, { props: { pageId: "page-1" }, attachTo: document.body });
    // A phone hides the button (display: none in refresh-article.css), so it takes no focus.
    vi.spyOn(app.get("button").element, "focus").mockImplementation(() => {});
    workspace.focus();
    await (app.vm as unknown as { copy: () => Promise<void> }).copy(); await flushPromises();
    await new Promise(resolve => setTimeout(resolve, 50));
    const input = document.querySelector<HTMLInputElement>('input[aria-label="Odkaz na stránku"]')!;
    expect(document.activeElement).toBe(input);
    input.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    await flushPromises();
    await new Promise(resolve => setTimeout(resolve, 50));
    expect(document.activeElement).toBe(workspace);
    app.unmount(); workspace.remove();
  });
  it("does not display success for another page after a late clipboard completion", async () => {
    let finish!: () => void;
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: vi.fn(() => new Promise<void>(resolve => { finish = resolve; })) } });
    const app = mount(CopyPageLink, { props: { pageId: "page-1" } });
    await app.get("button").trigger("click"); await app.setProps({ pageId: "page-2" });
    finish(); await flushPromises(); expect(app.find('[role="status"]').exists()).toBe(false); app.unmount();
  });
});

describe("copy link manual fallback surface", () => {
  it("portals the fallback out of the trigger's own subtree", async () => {
    document.body.innerHTML = '<div class="atlas"></div>';
    const denied = vi.fn().mockRejectedValue(new Error("denied"));
    vi.stubGlobal("navigator", { clipboard: { writeText: denied } });
    const view = mount(CopyPageLink, {
      props: { pageId: "page-1" },
      attachTo: document.querySelector(".atlas") as HTMLElement,
    });
    await view.get("button").trigger("click");
    await flushPromises();
    await new Promise(resolve => setTimeout(resolve, 50));
    const field = document.querySelector<HTMLInputElement>('input[aria-label="Odkaz na stránku"]');
    expect(field).not.toBeNull();
    // Outside the control's own element: the popover is portalled, which is what
    // frees it from the action row's overflow. Tokens reach it because they are
    // declared on :root, not on the shell.
    expect(field!.closest(".page-link-control")).toBeNull();
    view.unmount();
  });

  it("keeps the copy button reachable by tooltip rather than a native title", async () => {
    const view = mount(CopyPageLink, { props: { pageId: "page-1" } });
    expect(view.get("button").attributes("title")).toBeUndefined();
    expect(view.get("button").attributes("aria-label")).toBe("Kopírovat odkaz na stránku");
  });
});
