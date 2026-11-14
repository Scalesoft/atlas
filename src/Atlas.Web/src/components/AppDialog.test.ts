import { afterEach, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import { Popover, PopoverContent, PopoverTrigger } from "./ui/popover";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import AppDialog from "./AppDialog.vue";
import ActionDisclosure from "./ActionDisclosure.vue";
import ActionDisclosureItem from "./ActionDisclosureItem.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  document.body.replaceChildren();
  vi.restoreAllMocks();
});

function open(props: Record<string, unknown> = {}, slots: Record<string, string> = {}) {
  wrapper = mount(AppDialog, {
    attachTo: document.body,
    props: { title: "Historie stránky", open: true, ...props },
    slots: { default: "<p>Obsah</p>", ...slots },
  });
  return wrapper.get("dialog").element as HTMLDialogElement;
}

it("opens a modal dialog labelled by its single title", () => {
  const dialog = open();
  expect(dialog.open).toBe(true);
  expect(dialog.classList.contains("app-dialog")).toBe(true);
  expect(dialog.dataset.size).toBe("default");
  const heading = wrapper!.get(".app-dialog-header h2");
  expect(heading.text()).toBe("Historie stránky");
  expect(dialog.getAttribute("aria-labelledby")).toBe(heading.attributes("id"));
  expect(dialog.getAttribute("aria-label")).toBe(null);
  expect(wrapper!.findAll("h2")).toHaveLength(1);
  expect(wrapper!.find(".eyebrow").exists()).toBe(false);
  expect(wrapper!.text()).toContain("Obsah");
});

it("asks to close through one icon button with the shared label", async () => {
  const dialog = open();
  const close = wrapper!.get('.app-dialog-header button[aria-label="Zavřít"]');
  expect(close.attributes("type")).toBe("button");
  expect(close.attributes("data-slot")).toBe("button");
  expect(close.find("svg").exists()).toBe(true);
  await close.trigger("click");
  expect(wrapper!.emitted("close")).toHaveLength(1);
  // The owner decides — a busy or dirty dialog stays open until it lowers the prop.
  expect(dialog.open).toBe(true);
  await wrapper!.setProps({ open: false });
  expect(dialog.open).toBe(false);
});

it("closes on a SPA route change", async () => {
  history.replaceState(null, "", "#/pages/a");
  const dialog = open();
  history.replaceState(null, "", "#/pages/b");
  window.dispatchEvent(new HashChangeEvent("hashchange"));
  await wrapper!.vm.$nextTick();
  expect(dialog.open).toBe(false);
  expect(wrapper!.emitted("close")).toHaveLength(1);
});

// U2-2: the browser's Back fires popstate, then hashchange. The leave question opens on popstate and puts the address back; the
// hashchange that follows is no new route, so the question stays.
it("stays open through a hashchange that leaves the address where it opened", async () => {
  history.replaceState(null, "", "#/pages/a");
  const dialog = open();
  window.dispatchEvent(new HashChangeEvent("hashchange"));
  await wrapper!.vm.$nextTick();
  expect(dialog.open).toBe(true);
  expect(wrapper!.emitted("close")).toBeUndefined();
});

it("asks to close on Escape without letting the browser dismiss it first", async () => {
  const dialog = open();
  await wrapper!.get("dialog").trigger("cancel");
  expect(wrapper!.emitted("close")).toHaveLength(1);
  expect(dialog.open).toBe(true);
});

it("closes when the open prop turns false without emitting again", async () => {
  const dialog = open();
  await wrapper!.setProps({ open: false });
  expect(dialog.open).toBe(false);
  expect(wrapper!.emitted("close")).toBeUndefined();
});

it("accepts a wide size, a custom close label and an explicit aria-label", () => {
  const dialog = open({ size: "wide", closeLabel: "Zavřít koš", ariaLabel: "Koš" });
  expect(dialog.dataset.size).toBe("wide");
  expect(dialog.getAttribute("aria-label")).toBe("Koš");
  expect(dialog.getAttribute("aria-labelledby")).toBe(null);
  expect(wrapper!.find('button[aria-label="Zavřít koš"]').exists()).toBe(true);
});

it("renders the footer slot only when it is provided", () => {
  open();
  expect(wrapper!.find(".app-dialog-footer").exists()).toBe(false);
  wrapper!.unmount();
  wrapper = mount(AppDialog, {
    attachTo: document.body,
    props: { title: "Nová stránka", open: true },
    slots: { default: "<p>Obsah</p>", footer: "<button>Vytvořit stránku</button>" },
  });
  expect(wrapper.get(".app-dialog-footer").text()).toContain("Vytvořit stránku");
});

it("focuses inside on open and restores the opener on close", async () => {
  const opener = document.createElement("button");
  document.body.append(opener);
  opener.focus();
  wrapper = mount(AppDialog, {
    attachTo: document.body,
    props: { title: "Nová stránka", open: true },
    slots: { default: '<input aria-label="Název" />' },
  });
  await wrapper.vm.$nextTick();
  // The logical place is the field to type in (keyboard pass 2026-10-27), not the close button before it.
  expect(document.activeElement).toBe(wrapper.get('input[aria-label="Název"]').element);
  await wrapper.setProps({ open: false });
  expect(document.activeElement).toBe(opener);
});

it("starts a question without a field on its first control, never on a hidden one, and on the close button only when nothing else is there", async () => {
  wrapper = mount(AppDialog, {
    attachTo: document.body,
    props: { title: "Přesunout do koše", open: true },
    slots: { default: '<p>Stránka půjde do koše.</p><input type="hidden" name="id"><button>Zrušit</button><button>Přesunout do koše</button>' },
  });
  await wrapper.vm.$nextTick();
  expect(document.activeElement?.textContent).toBe("Zrušit");
  wrapper.unmount();
  wrapper = mount(AppDialog, { attachTo: document.body, props: { title: "Čtenost", open: true }, slots: { default: "<p>Bez ovládání</p>" } });
  await wrapper.vm.$nextTick();
  expect(document.activeElement).toBe(wrapper.get('button[aria-label="Zavřít"]').element);
});

it("restores focus to the menu trigger when opened from a menu item that goes away", async () => {
  const trigger = document.createElement("button");
  trigger.id = "space-menu-trigger";
  trigger.setAttribute("aria-haspopup", "menu");
  const menu = document.createElement("div");
  menu.setAttribute("role", "menu");
  menu.setAttribute("aria-labelledby", trigger.id);
  const item = document.createElement("div");
  item.setAttribute("role", "menuitem");
  item.tabIndex = -1;
  menu.append(item);
  document.body.append(trigger, menu);
  item.focus();
  wrapper = mount(AppDialog, { attachTo: document.body, props: { title: "Nový prostor", open: true } });
  await wrapper.vm.$nextTick();
  menu.remove();
  await wrapper.setProps({ open: false });
  expect(document.activeElement).toBe(trigger);
});

it("restores the menu trigger when the closed menu has already dropped focus to the body", async () => {
  const trigger = document.createElement("button");
  trigger.id = "history-menu-trigger";
  trigger.setAttribute("aria-haspopup", "menu");
  const menu = document.createElement("div");
  menu.setAttribute("role", "menu");
  menu.setAttribute("aria-labelledby", trigger.id);
  const item = document.createElement("div");
  item.setAttribute("role", "menuitem");
  item.tabIndex = -1;
  menu.append(item);
  document.body.append(trigger, menu);
  wrapper = mount(AppDialog, { attachTo: document.body, props: { title: "Historie", open: false } });
  await wrapper.vm.$nextTick();
  // The item is activated, its menu goes inert and focus falls to <body>; the owner opens the dialog afterwards.
  item.dispatchEvent(new MouseEvent("click", { bubbles: true }));
  menu.remove();
  expect(document.activeElement).toBe(document.body);
  await wrapper.setProps({ open: true });
  await wrapper.setProps({ open: false });
  expect(document.activeElement).toBe(trigger);
});

it("prefers an autofocus field over the close button", async () => {
  wrapper = mount(AppDialog, {
    attachTo: document.body,
    props: { title: "Nová stránka", open: true },
    slots: { default: '<input autofocus aria-label="Název" />' },
  });
  await wrapper.vm.$nextTick();
  expect(document.activeElement).toBe(wrapper.get('input[aria-label="Název"]').element);
});

function animation() {
  const target = Object.assign(new EventTarget(), { cancel: vi.fn(), finished: Promise.resolve() });
  return target as unknown as Animation & EventTarget;
}

it("a palette unfolds out of its trigger and folds away before it closes", async () => {
  const trigger = document.createElement("button");
  trigger.className = "launch";
  document.body.append(trigger);
  const animations: Animation[] = [];
  const animate = vi.fn(() => { const next = animation(); animations.push(next); return next; });
  Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, writable: true, value: animate });
  try {
    const dialog = open({ open: false, unfoldTrigger: ".launch" });
    await wrapper!.setProps({ open: true });
    expect(dialog.open).toBe(true);
    expect(animate).toHaveBeenCalledTimes(1);
    expect(Object.keys((animate.mock.calls[0] as unknown as [Keyframe[]])[0][0]!)).toContain("opacity");
    await wrapper!.setProps({ open: false });
    // It folds in place: inert at once, closed when the fold ends.
    expect(animate).toHaveBeenCalledTimes(2);
    expect(dialog.open).toBe(true);
    expect(dialog.hasAttribute("inert")).toBe(true);
    expect(wrapper!.emitted("closed")).toBeUndefined();
    animations[1]!.dispatchEvent(new Event("finish"));
    expect(dialog.open).toBe(false);
    expect(dialog.hasAttribute("inert")).toBe(false);
    expect(animations[1]!.cancel).toHaveBeenCalled();
    expect(wrapper!.emitted("closed")).toHaveLength(1);
  } finally { Reflect.deleteProperty(HTMLElement.prototype, "animate"); }
});

it("opening again while a palette folds cancels the fold and keeps it open", async () => {
  const animations: Animation[] = [];
  Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, writable: true, value: vi.fn(() => { const next = animation(); animations.push(next); return next; }) });
  try {
    const dialog = open({ open: false, unfoldTrigger: ".none" });
    await wrapper!.setProps({ open: true });
    await wrapper!.setProps({ open: false });
    await wrapper!.setProps({ open: true });
    expect(animations[1]!.cancel).toHaveBeenCalled();
    expect(wrapper!.emitted("closed")).toBeUndefined();
    expect(dialog.open).toBe(true);
    expect(dialog.hasAttribute("inert")).toBe(false);
  } finally { Reflect.deleteProperty(HTMLElement.prototype, "animate"); }
});

it("reverses a partly visible dialog from its current frame and cancels the old entrance", async () => {
  const animations: Animation[] = [];
  const animate = vi.fn(() => { const next = animation(); animations.push(next); return next; });
  Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, value: animate });
  try {
    const dialog = open({ size: "side" });
    dialog.style.opacity = ".35";
    dialog.style.transform = "translateX(12px)";
    await wrapper!.setProps({ open: false });
    expect(animations[0]!.cancel).toHaveBeenCalled();
    expect((animate.mock.calls[1] as unknown as [Keyframe[]])[0][0]).toMatchObject({ opacity: "0.35", transform: "translateX(12px)" });
    await wrapper!.setProps({ open: true });
    expect((animate.mock.calls[2] as unknown as [Keyframe[]])[0][0]).toMatchObject({ opacity: "0.35", transform: "translateX(12px)" });
    animations[1]!.dispatchEvent(new Event("finish"));
    expect(dialog.open).toBe(true);
    expect(dialog.hasAttribute("inert")).toBe(false);
  } finally { Reflect.deleteProperty(HTMLElement.prototype, "animate"); }
});

it("a palette reversed out of a clipped fold returns to a clip that holds its shadow", async () => {
  const animate = vi.fn(() => animation());
  Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, writable: true, value: animate });
  try {
    const dialog = open({ open: false, unfoldTrigger: ".none" });
    dialog.style.boxShadow = "rgba(20, 47, 59, 0.28) 0px 24px 64px 0px";
    await wrapper!.setProps({ open: true });
    // The unfolding was interrupted: the fold keeps the clip it had reached, and so does the frame a reversal starts from.
    dialog.style.clipPath = "inset(0px 164px 99px 0px round 15px)";
    await wrapper!.setProps({ open: false });
    await wrapper!.setProps({ open: true });
    const frames = (animate.mock.calls.at(-1) as unknown as [Keyframe[]])[0];
    expect(frames[0]).toMatchObject({ clipPath: "inset(0px 164px 99px 0px round 15px)" });
    // Ending on the border box would cut the shadow until the animation is removed and show it in one frame then.
    expect(frames[1]).toMatchObject({ opacity: 1, transform: "none", clipPath: "inset(-72px -96px -120px -96px round 14px)" });
  } finally { Reflect.deleteProperty(HTMLElement.prototype, "animate"); }
});

it("keeps a dropdown in its native modal and lets Escape close only the menu", async () => {
  const controls = defineComponent({
    components: { ActionDisclosure, ActionDisclosureItem },
    template: '<ActionDisclosure label="Dialog actions"><ActionDisclosureItem>Rename</ActionDisclosureItem></ActionDisclosure>',
  });
  wrapper = mount(AppDialog, { attachTo: document.body, props: { title: "Actions", open: true }, slots: { default: () => h(controls) } });
  await flushPromises();
  await wrapper.get('[aria-label="Dialog actions"]').trigger("keydown", { key: "ArrowDown" });
  await flushPromises();
  const menu = document.querySelector<HTMLElement>('[role="menu"]')!;
  expect(menu.closest("dialog")).toBe(wrapper.get("dialog").element);
  menu.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
  await flushPromises();
  expect(document.querySelector('[role="menu"][data-state="open"]')).toBeNull();
  expect((wrapper.get("dialog").element as HTMLDialogElement).open).toBe(true);
  expect(wrapper.emitted("close")).toBeUndefined();
});

it("the side panel slides in and a plain dialog has a short entrance", async () => {
  const animate = vi.fn(() => animation());
  Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, writable: true, value: animate });
  try {
    open({ size: "side" });
    expect(animate).toHaveBeenCalledTimes(1);
    expect((animate.mock.calls[0] as unknown as [Keyframe[]])[0][0]).toMatchObject({ opacity: 0, transform: "translateX(24px)" });
    wrapper!.unmount();
    animate.mockClear();
    open();
    expect(animate).toHaveBeenCalledOnce();
    expect((animate.mock.calls[0] as unknown as [Keyframe[]])[0][0]).toMatchObject({ opacity: 0, transform: "translateY(8px) scale(.98)" });
  } finally { Reflect.deleteProperty(HTMLElement.prototype, "animate"); }
});

it("a plain dialog leaves on the way it came and a side panel slides back to its edge, both shorter than the entrance", async () => {
  const animate = vi.fn(() => animation());
  Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, writable: true, value: animate });
  try {
    const dialog = open();
    animate.mockClear();
    await wrapper!.setProps({ open: false });
    const leaving = animate.mock.calls.at(-1) as unknown as [Keyframe[], KeyframeAnimationOptions];
    expect(leaving[0].at(-1)).toMatchObject({ opacity: 0, transform: "translateY(8px) scale(.98)" });
    expect(leaving[1].fill).toBe("forwards");
    expect(dialog.hasAttribute("inert")).toBe(true);
    wrapper!.unmount();
    const side = open({ size: "side" });
    animate.mockClear();
    await wrapper!.setProps({ open: false });
    expect((animate.mock.calls.at(-1) as unknown as [Keyframe[]])[0].at(-1)).toMatchObject({ opacity: 0, transform: "translateX(24px)" });
    expect(side.hasAttribute("inert")).toBe(true);
  } finally { Reflect.deleteProperty(HTMLElement.prototype, "animate"); }
});

it("owns nested popover Escape order while composing text and native selects keep their keys", async () => {
  const controls = defineComponent({
    components: { Popover, PopoverContent, PopoverTrigger },
    template: '<Popover><PopoverTrigger as-child><button data-outer-trigger>Outer</button></PopoverTrigger><PopoverContent data-outer-preview><Popover><PopoverTrigger as-child><button data-inner-trigger>Inner</button></PopoverTrigger><PopoverContent data-inner-preview><button data-inner-action>Action</button><select><option>Native</option></select></PopoverContent></Popover></PopoverContent></Popover>',
  });
  const owner = vi.fn();
  wrapper = mount(AppDialog, { attachTo: document.body, props: { title: "Nested controls", open: true, onKeydown: owner }, slots: { default: () => h(controls) } });
  const dialog = wrapper.get("dialog").element as HTMLDialogElement;
  await flushPromises();
  (wrapper.get("[data-outer-trigger]").element as HTMLElement).focus();
  await wrapper.get("[data-outer-trigger]").trigger("click"); await flushPromises();
  await wrapper.get("[data-inner-trigger]").trigger("click"); await flushPromises();
  const preview = document.querySelector<HTMLElement>("[data-inner-preview]")!;
  expect(preview.closest("dialog")).toBe(dialog);
  const composing = new KeyboardEvent("keydown", { key: "Escape", isComposing: true, bubbles: true, cancelable: true });
  preview.dispatchEvent(composing); await flushPromises();
  expect(composing.defaultPrevented).toBe(false);
  expect(preview.dataset.state).toBe("open");
  const selectEscape = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
  preview.querySelector("select")!.dispatchEvent(selectEscape); await flushPromises();
  expect(selectEscape.defaultPrevented).toBe(false);
  expect(preview.dataset.state).toBe("open");
  preview.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true })); await flushPromises();
  expect(document.querySelector("[data-inner-preview][data-state=open]")).toBeNull();
  expect(document.querySelector("[data-outer-preview][data-state=open]")).not.toBeNull();
  await vi.waitFor(() => expect(document.activeElement).toBe(wrapper!.get("[data-inner-trigger]").element));
  await wrapper.get("[data-inner-trigger]").trigger("keydown", { key: "Escape" }); await flushPromises();
  expect(document.querySelector("[data-outer-preview][data-state=open]")).toBeNull();
  expect(owner).not.toHaveBeenCalled();
  await wrapper.get("[data-outer-trigger]").trigger("keydown", { key: "Escape" });
  expect(owner).toHaveBeenCalledOnce(); // Closed layers unregister before the owner's next Escape.
});

it("keeps a closing side panel inert until its exit finishes and safely reopens", async () => {
  const animations: Animation[] = [];
  Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, value: vi.fn(() => { const next = animation(); animations.push(next); return next; }) });
  try {
    const dialog = open({ size: "side" });
    await wrapper!.setProps({ open: false });
    expect(dialog.open).toBe(true);
    expect(dialog.hasAttribute("inert")).toBe(true);
    const closing = animations.at(-1)!;
    await wrapper!.setProps({ open: true });
    closing.dispatchEvent(new Event("finish"));
    expect(dialog.open).toBe(true);
    expect(dialog.hasAttribute("inert")).toBe(false);
    await wrapper!.setProps({ open: false });
    animations.at(-1)!.dispatchEvent(new Event("finish"));
    expect(dialog.open).toBe(false);
  } finally { Reflect.deleteProperty(HTMLElement.prototype, "animate"); }
});

it("finishes closing if its exit animation is cancelled externally", async () => {
  const animations: Animation[] = [];
  Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, value: vi.fn(() => { const next = animation(); animations.push(next); return next; }) });
  try {
    const dialog = open();
    await wrapper!.setProps({ open: false });
    animations.at(-1)!.dispatchEvent(new Event("cancel"));
    expect(dialog.open).toBe(false);
    expect(dialog.hasAttribute("inert")).toBe(false);
    expect(wrapper!.emitted("closed")).toHaveLength(1);
  } finally { Reflect.deleteProperty(HTMLElement.prototype, "animate"); }
});

it("hands a lazy dialog's placeholder over to the real dialog without a second entrance", () => {
  const placeholder = open({ "data-loading": "" });
  expect(placeholder.hasAttribute("data-animated-backdrop")).toBe(false);
  wrapper!.unmount();
  const real = open();
  expect(real.hasAttribute("data-animated-backdrop")).toBe(true);
});
