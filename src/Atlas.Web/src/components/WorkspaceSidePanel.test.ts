import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import { defineComponent, nextTick, ref } from "vue";
import WorkspaceSidePanel from "./WorkspaceSidePanel.vue";

const KEY = "atlas:reading:u1:panel-width";

function host(storageKey: string | undefined = KEY, attachTo: HTMLElement = document.body) {
  return mount(defineComponent({
    components: { WorkspaceSidePanel },
    setup: () => ({ open: ref(false), clicks: ref(0), storageKey }),
    template: `<div>
      <button id="opener" @click="open = true">Otevřít</button>
      <article><button id="page" @click="clicks++">Stránka</button><p>{{ clicks }}</p></article>
      <WorkspaceSidePanel v-model:open="open" label="Asistent" :storage-key="storageKey">
        <template #header>Hlavička</template>
        <textarea id="field" data-autofocus />
      </WorkspaceSidePanel>
    </div>`,
  }), { attachTo });
}
/** A workspace row: the page column with one centred block, and the panel beside it. */
function workspaceRow(props: { open: boolean; autoFocus?: boolean }) {
  return mount(defineComponent({
    components: { WorkspaceSidePanel },
    props: { open: Boolean, autoFocus: { type: Boolean, default: true } },
    template: `<div class="row"><div class="column"><div id="block">Článek</div></div>
      <WorkspaceSidePanel :open="open" label="Panel" :auto-focus="autoFocus" /></div>`,
  }), { props, attachTo: document.body });
}
const at = (left: number, width = 720) => ({ left, width, height: width ? 40 : 0 }) as DOMRect;
function setViewport(width: number) {
  Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: width });
  window.dispatchEvent(new Event("resize"));
}

const memory = new Map<string, string>();
const storage = { getItem: (key: string) => memory.get(key) ?? null, setItem: (key: string, value: string) => void memory.set(key, value), clear: () => memory.clear() };
beforeEach(() => vi.stubGlobal("localStorage", storage));
afterEach(() => { document.body.innerHTML = ""; memory.clear(); setViewport(1280); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe("WorkspaceSidePanel", () => {
  it('returns focus to the workspace when a breakpoint has made its opener inert', async () => {
    const main = document.createElement('main'); main.tabIndex = -1; document.body.append(main);
    const wrapper = host(KEY, main);
    const opener = wrapper.get('#opener').element as HTMLElement;
    opener.focus(); await wrapper.get('#opener').trigger('click'); await nextTick();
    opener.setAttribute('inert', '');
    await wrapper.get('#field').trigger('keydown', { key: 'Escape' }); await nextTick();
    expect(document.activeElement).toBe(main);
    wrapper.unmount(); main.remove();
  });
  it('stops a resize when hidden or switched to overlay without overwriting the preferred width', async () => {
    setViewport(1280);
    const wrapper = host();
    const pointer = (type: string, x: number, id = 1) => {
      const event = new Event(type, { bubbles: true, cancelable: true });
      Object.assign(event, { clientX: x, pointerId: id, button: 0 });
      return event;
    };
    await wrapper.get('#opener').trigger('click'); await nextTick();
    wrapper.get('[role=separator]').element.dispatchEvent(pointer('pointerdown', 800));
    window.dispatchEvent(pointer('pointermove', 740)); await nextTick();
    expect(wrapper.get('aside').attributes('style')).toContain('460px');
    window.dispatchEvent(pointer('pointercancel', 740, 2)); await nextTick();
    expect(wrapper.get('aside').attributes('style')).toContain('460px');
    setViewport(700); await nextTick();
    window.dispatchEvent(pointer('pointerup', 700)); await nextTick();
    expect(wrapper.get('aside').attributes('style')).toContain('400px');
    expect(localStorage.getItem(KEY)).toBeNull();
    setViewport(1280); await nextTick();
    wrapper.get('[role=separator]').element.dispatchEvent(pointer('pointerdown', 800));
    window.dispatchEvent(pointer('pointermove', 740)); await nextTick();
    await wrapper.get('.workspace-panel-close').trigger('click'); await nextTick();
    window.dispatchEvent(pointer('pointerup', 700));
    await wrapper.get('#opener').trigger('click'); await nextTick();
    expect(wrapper.get('aside').attributes('style')).toContain('400px');
    expect(localStorage.getItem(KEY)).toBeNull();
    wrapper.unmount();
  });

  it("reserves reading space in its container and restores the preferred width without persisting a constraint", async () => {
    let observed!: (entries: unknown[]) => void;
    vi.stubGlobal('ResizeObserver', class {
      constructor(callback: (entries: unknown[]) => void) { observed = callback; }
      observe() {} disconnect() {}
    });
    localStorage.setItem(KEY, '480');
    const wrapper = host();
    let available = 1000, reads = 0;
    Object.defineProperty(wrapper.element, 'clientWidth', { get: () => { reads++; return available; } });
    // The observer's own size, not a layout read: one there costs a layout of the page beside it as editing starts.
    const resize = () => { reads = 0; observed([{ borderBoxSize: [{ inlineSize: available }] }]); expect(reads).toBe(0); };
    await wrapper.get('#opener').trigger('click');
    await nextTick();
    expect(wrapper.get('aside').attributes('style')).toContain('width: 400px');
    expect(wrapper.get('[role=separator]').attributes('aria-valuenow')).toBe('400');
    available = 850; resize(); await nextTick();
    expect(wrapper.get('aside').attributes('data-mode')).toBe('overlay');
    expect(wrapper.find('[role=separator]').exists()).toBe(false);
    available = 1200; resize(); await nextTick();
    expect(wrapper.get('aside').attributes('data-mode')).toBe('beside');
    expect(wrapper.get('aside').attributes('style')).toContain('width: 480px');
    expect(localStorage.getItem(KEY)).toBe('480');
    wrapper.unmount();
  });

  it("reverses a close from the visible frame and ignores completion of the cancelled exit", async () => {
    const calls: { el: HTMLElement; frames: Keyframe[]; finish: () => void }[] = [];
    Object.defineProperty(HTMLElement.prototype, 'animate', { configurable: true, value(this: HTMLElement, frames: Keyframe[]) {
      let finish!: () => void;
      const finished = new Promise<void>(resolve => { finish = resolve; });
      calls.push({ el: this, frames, finish });
      return { finished, cancel() {} };
    } });
    const wrapper = workspaceRow({ open: true });
    const block = wrapper.get('#block').element as HTMLElement;
    const place = vi.spyOn(block, 'getBoundingClientRect').mockReturnValue(at(100));
    try {
      await nextTick();
      // Closing gives the room back at once: the block belongs at 270 and travels there from 100.
      place.mockReturnValueOnce(at(100)).mockReturnValue(at(270));
      await wrapper.setProps({ open: false });
      const exit = calls.at(-1)!;
      expect(calls.at(-2)).toMatchObject({ el: block, frames: [{ translate: '-170px 0px' }, { translate: '0px 0px' }] });
      // Reopened midway: the block is at 177 on screen and the surface is 40 % visible. Both continue from there.
      place.mockReturnValueOnce(at(177)).mockReturnValue(at(100));
      (wrapper.get('.workspace-panel-surface').element as HTMLElement).style.opacity = '0.4';
      await wrapper.setProps({ open: true });
      await nextTick();
      expect(calls.at(-2)).toMatchObject({ el: block, frames: [{ translate: '77px 0px' }, { translate: '0px 0px' }] });
      expect(calls.at(-1)!.frames[0]).toMatchObject({ opacity: '0.4' });
      exit.finish(); await nextTick();
      expect(wrapper.find('aside').exists()).toBe(true);
      expect(wrapper.get('.workspace-panel-surface').attributes('inert')).toBeUndefined();
      expect((wrapper.get('aside').element as HTMLElement).style.marginLeft).toBe('');
    } finally { wrapper.unmount(); Reflect.deleteProperty(HTMLElement.prototype, 'animate'); }
  });

  it("arrives with its page when the reader did not ask for it, and moves the page when they did", async () => {
    const moved: { el: HTMLElement; frames: Keyframe[] }[] = [];
    Object.defineProperty(HTMLElement.prototype, 'animate', { configurable: true, value(this: HTMLElement, frames: Keyframe[]) {
      moved.push({ el: this, frames });
      return { finished: Promise.resolve(), cancel() {} };
    } });
    // Without the panel's column the block would stand at 270; with it at 100.
    const rects = vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      return this.id === 'block' ? at(this.closest('.row')?.querySelector<HTMLElement>('aside')?.style.marginLeft ? 270 : 100) : at(0, 0);
    });
    try {
      const remembered = workspaceRow({ open: true, autoFocus: false });
      expect(moved.map(({ el }) => el.className)).toEqual(['workspace-panel-surface']);
      remembered.unmount();
      moved.length = 0;
      const asked = workspaceRow({ open: true });
      expect(moved[0]).toMatchObject({ el: asked.get('#block').element, frames: [{ translate: '170px 0px' }, { translate: '0px 0px' }] });
      expect((asked.get('aside').element as HTMLElement).style.marginLeft).toBe('');
      asked.unmount();
    } finally { rects.mockRestore(); Reflect.deleteProperty(HTMLElement.prototype, 'animate'); }
  });

  it("slides out of the right edge on opening and back into it on closing, and only fades under reduced motion", async () => {
    const calls: Keyframe[][] = [];
    HTMLElement.prototype.animate = vi.fn((frames: Keyframe[]) => { calls.push(frames); return { finished: Promise.resolve(), cancel() {} } as unknown as Animation; }) as unknown as typeof HTMLElement.prototype.animate;
    const slides = () => calls.filter((frames) => frames.some((frame) => "transform" in frame));
    try {
      const wrapper = workspaceRow({ open: false });
      await wrapper.setProps({ open: true });
      await nextTick();
      expect(slides()).toContainEqual([{ opacity: 0, transform: "translateX(100%)" }, { opacity: 1, transform: "none" }]);
      calls.length = 0;
      await wrapper.setProps({ open: false });
      expect(slides().map((frames) => frames.at(-1))).toContainEqual({ opacity: 0, transform: "translateX(100%)" });
      wrapper.unmount();

      calls.length = 0;
      vi.mocked(window.matchMedia).mockImplementation((query: string) => ({ matches: query.includes("reduce"), media: query, addEventListener: vi.fn(), removeEventListener: vi.fn() }) as unknown as MediaQueryList);
      const reduced = workspaceRow({ open: false });
      await reduced.setProps({ open: true });
      await nextTick();
      await reduced.setProps({ open: false });
      expect(calls.length).toBeGreaterThan(0);
      expect(slides()).toEqual([]);
      reduced.unmount();
    } finally {
      vi.mocked(window.matchMedia).mockImplementation((query: string) => ({ matches: false, media: query, addEventListener: vi.fn(), removeEventListener: vi.fn() }) as unknown as MediaQueryList);
      delete (HTMLElement.prototype as { animate?: unknown }).animate;
    }
  });

  it("stands at the right edge of the workspace however wide it is", async () => {
    const wrapper = workspaceRow({ open: false });
    Object.defineProperty(wrapper.element, 'clientWidth', { get: () => 2400 });
    await wrapper.setProps({ open: true });
    await nextTick();
    const aside = wrapper.get('aside');
    expect(aside.attributes('data-mode')).toBe('beside');
    expect(aside.classes()).not.toContain('is-grouped');
    expect((aside.element as HTMLElement).style.translate ?? '').toBe('');
    wrapper.unmount();
  });

  it('finishes its exit animation when closed in the middle of a resize', async () => {
    setViewport(1280);
    let finish!: () => void;
    Object.defineProperty(HTMLElement.prototype, 'animate', { configurable: true, value: () => ({
      finished: new Promise<void>(resolve => { finish = resolve; }), cancel() {},
    }) });
    const wrapper = mount(WorkspaceSidePanel, { props: { open: true, label: 'Panel' }, attachTo: document.body });
    try {
      await nextTick();
      const down = new Event('pointerdown'); Object.assign(down, { button: 0, clientX: 800, pointerId: 1 });
      wrapper.get('[role=separator]').element.dispatchEvent(down);
      const move = new Event('pointermove'); Object.assign(move, { clientX: 740, pointerId: 1 });
      window.dispatchEvent(move); await nextTick();
      await wrapper.setProps({ open: false });
      expect(wrapper.find('aside').exists()).toBe(true);
      expect(wrapper.get('.workspace-panel-surface').attributes('inert')).toBeDefined();
      finish(); await nextTick(); await nextTick();
      expect(wrapper.find('aside').exists()).toBe(false);
    } finally { wrapper.unmount(); Reflect.deleteProperty(HTMLElement.prototype, 'animate'); }
  });

  it("is complementary and non-modal, so the page beside it stays interactive", async () => {
    const wrapper = host();
    expect(wrapper.find("aside").exists()).toBe(false);
    await wrapper.find("#opener").trigger("click");
    await nextTick();
    const surface = wrapper.find("[role=complementary]");
    expect(surface.attributes("aria-label")).toBe("Asistent");
    expect(surface.attributes("aria-modal")).toBeUndefined();
    expect(wrapper.find("dialog").exists()).toBe(false);
    // Nothing around the panel is inert and the page still reacts.
    expect(wrapper.find("article").element.closest("[inert]")).toBeNull();
    await wrapper.find("#page").trigger("click");
    expect(wrapper.find("article p").text()).toBe("1");
    // Focus moved in (to the marked field) without trapping it: the page button is focusable.
    expect(document.activeElement?.id).toBe("field");
    (wrapper.find("#page").element as HTMLElement).focus();
    expect(document.activeElement?.id).toBe("page");
    expect(wrapper.find("aside").attributes("data-mode")).toBe("beside");
    wrapper.unmount();
  });

  it("closes on escape only when focus is inside and returns focus to its trigger", async () => {
    const wrapper = host();
    const opener = wrapper.find("#opener").element as HTMLButtonElement;
    opener.focus();
    await opener.click();
    await nextTick();
    // Escape outside the panel does nothing.
    (wrapper.find("#page").element as HTMLElement).focus();
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    wrapper.find("#page").element.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    await nextTick();
    expect(wrapper.find("aside").exists()).toBe(true);
    // Inside: closes and focus goes back to the opener.
    (wrapper.find("#field").element as HTMLElement).focus();
    await wrapper.find("#field").trigger("keydown", { key: "Escape" });
    await nextTick(); await nextTick();
    expect(wrapper.find("aside").exists()).toBe(false);
    expect(document.activeElement).toBe(opener);
    wrapper.unmount();
  });

  it("lets an inner handler claim escape first and closes with the button, returning focus", async () => {
    const wrapper = host();
    const opener = wrapper.find("#opener").element as HTMLButtonElement;
    opener.focus();
    await wrapper.find("#opener").trigger("click");
    await nextTick();
    wrapper.find("#field").element.addEventListener("keydown", (event) => event.preventDefault());
    await wrapper.find("#field").trigger("keydown", { key: "Escape" });
    expect(wrapper.find("aside").exists()).toBe(true);
    await wrapper.find(".workspace-panel-close").trigger("click");
    await nextTick(); await nextTick();
    expect(wrapper.find("aside").exists()).toBe(false);
    expect(document.activeElement).toBe(opener);
    wrapper.unmount();
  });

  it("opens and closes from v-model:open and reflects the initial state", async () => {
    const wrapper = mount(WorkspaceSidePanel, { props: { open: true, label: "Panel" }, slots: { default: "<p>Obsah</p>" }, attachTo: document.body });
    expect(wrapper.text()).toContain("Obsah");
    await wrapper.find(".workspace-panel-close").trigger("click");
    expect(wrapper.emitted("update:open")?.[0]).toEqual([false]);
    await wrapper.setProps({ open: false });
    await nextTick();
    expect(wrapper.find("aside").exists()).toBe(false);
    wrapper.unmount();
  });

  it("remembers its width per user and survives blocked storage", async () => {
    localStorage.setItem(KEY, "9999");
    let wrapper = mount(WorkspaceSidePanel, { props: { open: true, label: "P", storageKey: KEY }, attachTo: document.body });
    expect(wrapper.find("aside").attributes("style")).toContain("width: 480px");
    await wrapper.find("[role=separator]").trigger("keydown", { key: "Home" });
    expect(localStorage.getItem(KEY)).toBe("360");
    wrapper.unmount();
    wrapper = mount(WorkspaceSidePanel, { props: { open: true, label: "P", storageKey: "atlas:reading:u2:panel-width" }, attachTo: document.body });
    expect(wrapper.find("aside").attributes("style")).toContain("width: 400px");
    wrapper.unmount();

    vi.stubGlobal("localStorage", { getItem: () => { throw new Error("blocked"); }, setItem: () => { throw new Error("blocked"); } });
    wrapper = mount(WorkspaceSidePanel, { props: { open: true, label: "P", storageKey: KEY }, attachTo: document.body });
    expect(wrapper.find("aside").attributes("style")).toContain("width: 400px");
    await wrapper.find("[role=separator]").trigger("keydown", { key: "ArrowLeft" });
    expect(wrapper.find("aside").attributes("style")).toContain("width: 416px");
    wrapper.unmount();
  });

  it("covers the screen below 760 px", async () => {
    setViewport(700);
    const wrapper = mount(WorkspaceSidePanel, { props: { open: true, label: "P" }, attachTo: document.body });
    expect(wrapper.find("aside").attributes("data-mode")).toBe("overlay");
    expect(wrapper.find("aside").classes()).toContain("is-overlay");
    expect(wrapper.find("[role=separator]").exists()).toBe(false);
    setViewport(1200);
    await nextTick();
    expect(wrapper.find("aside").attributes("data-mode")).toBe("beside");
    wrapper.unmount();
  });

  // 2.6: the column takes its room at once and the page beside it travels with a transform. An animated width
  // made a long article reflow on every frame (measured: frames over 32 ms at 4× CPU throttle).
  it("changes the layout once, moves the page on motion tokens and keeps only the fade under reduced motion", async () => {
    const calls: Keyframe[][] = [];
    const animate = vi.fn((frames: Keyframe[]) => { calls.push(frames); return { finished: Promise.resolve(), cancel() {} } as unknown as Animation; });
    HTMLElement.prototype.animate = animate as unknown as typeof HTMLElement.prototype.animate;
    try {
      const wrapper = workspaceRow({ open: false });
      vi.spyOn(wrapper.get("#block").element, "getBoundingClientRect").mockReturnValueOnce(at(270)).mockReturnValue(at(100));
      await wrapper.setProps({ open: true });
      await nextTick();
      expect(wrapper.get("aside").attributes("style")).toContain("width: 400px");
      expect(calls.some((frames) => frames.some((frame) => "width" in frame))).toBe(false);
      expect(calls).toContainEqual([{ translate: "170px 0px" }, { translate: "0px 0px" }]);
      expect(calls.some((frames) => frames.some((frame) => "transform" in frame))).toBe(true);
      wrapper.unmount();

      calls.length = 0;
      vi.mocked(window.matchMedia).mockImplementation((query: string) => ({ matches: query.includes("reduce"), media: query, addEventListener: vi.fn(), removeEventListener: vi.fn() }) as unknown as MediaQueryList);
      const reduced = workspaceRow({ open: false });
      vi.spyOn(reduced.get("#block").element, "getBoundingClientRect").mockReturnValueOnce(at(270)).mockReturnValue(at(100));
      await reduced.setProps({ open: true });
      await nextTick();
      expect(calls.length).toBeGreaterThan(0);
      for (const frames of calls) for (const frame of frames) {
        expect(frame).not.toHaveProperty("width");
        expect(frame).not.toHaveProperty("transform");
        expect(frame).not.toHaveProperty("translate");
      }
      reduced.unmount();
    } finally {
      delete (HTMLElement.prototype as { animate?: unknown }).animate;
    }
  });
});

it("animates a panel already open when mounted by its owner", () => {
  const animate = vi.fn(() => ({ finished: Promise.resolve(), cancel() {} } as unknown as Animation));
  Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, value: animate });
  const wrapper = mount(WorkspaceSidePanel, { props: { open: true, label: "Panel" }, attachTo: document.body });
  try { expect(animate).toHaveBeenCalled(); }
  finally { wrapper.unmount(); Reflect.deleteProperty(HTMLElement.prototype, "animate"); }
});
describe("WorkspaceSidePanel tabs (page dock)", () => {
  const tabs = [{ id: "outline", label: "Obsah" }, { id: "assistant", label: "Asistent", icon: "sparkles" }, { id: "history", label: "Historie" }];
  it('keeps one content fade when tabs are switched before the previous fade finishes', async () => {
    const fades: { cancel: ReturnType<typeof vi.fn>; frames: Keyframe[] }[] = [];
    Object.defineProperty(HTMLElement.prototype, 'animate', { configurable: true, value: function(this: HTMLElement, frames: Keyframe[]) {
      const animation = { cancel: vi.fn(), frames, playState: 'running', finished: new Promise(() => {}) };
      if (this.classList.contains('workspace-panel-body')) fades.push(animation);
      return animation;
    } });
    const wrapper = mount(WorkspaceSidePanel, { props: { open: true, label: 'Panel', tabs, activeTab: 'outline' }, attachTo: document.body });
    try {
      await wrapper.setProps({ activeTab: 'assistant' });
      (wrapper.get('.workspace-panel-body').element as HTMLElement).style.opacity = '.45';
      await wrapper.setProps({ activeTab: 'history' });
      expect(fades).toHaveLength(2);
      expect(fades[0]!.cancel).toHaveBeenCalledOnce();
      expect(fades[1]!.frames[0]!.opacity).toBe('0.45');
      await wrapper.setProps({ open: false });
      expect(fades[1]!.cancel).toHaveBeenCalledOnce();
    } finally { wrapper.unmount(); Reflect.deleteProperty(HTMLElement.prototype, 'animate'); }
  });
  it("turns the header into a tab row whose arrows choose the next tab, with the header slot under it", async () => {
    const wrapper = mount(WorkspaceSidePanel, {
      props: { open: true, label: "Boční panel stránky", tabs, activeTab: "outline", "onUpdate:activeTab": (id: string) => wrapper.setProps({ activeTab: id }) },
      slots: { default: "<p>Obsah panelu</p>", header: "<span class='sub'>Asistent ⋯</span>" },
      attachTo: document.body,
    });
    const list = wrapper.get("[role=tablist]");
    expect(list.attributes("aria-label")).toBe("Nástroje stránky");
    const tab = (id: string) => wrapper.get(`[role=tab][data-tab=${id}]`);
    expect(tab("outline").attributes("aria-selected")).toBe("true");
    expect(tab("assistant").attributes("tabindex")).toBe("-1");
    const body = wrapper.get("[role=tabpanel]");
    expect(body.attributes("aria-labelledby")).toBe(tab("outline").attributes("id"));
    expect(tab("outline").attributes("aria-controls")).toBe(body.attributes("id"));
    expect(wrapper.find(".workspace-panel-subheader .sub").exists()).toBe(true);
    // Focus went to the selected tab, the first stop of a tabbed panel.
    expect(document.activeElement).toBe(tab("outline").element);
    await tab("outline").trigger("keydown", { key: "ArrowRight" });
    await nextTick(); await nextTick();
    expect(wrapper.emitted("update:activeTab")?.[0]).toEqual(["assistant"]);
    expect(tab("assistant").attributes("aria-selected")).toBe("true");
    expect(document.activeElement).toBe(tab("assistant").element);
    // The pill is measured by the shared tab indicator (useTabIndicator) and stands under the selected tab.
    expect(wrapper.find(".workspace-panel-tab-pill").exists()).toBe(true);
    await tab("assistant").trigger("keydown", { key: "End" });
    expect(wrapper.emitted("update:activeTab")?.[1]).toEqual(["history"]);
    await tab("outline").trigger("click");
    expect(wrapper.emitted("update:activeTab")?.[2]).toEqual(["outline"]);
    wrapper.unmount();
  });

  it("keeps focus where it was when the owner opens it without asking, and uses its own width range", async () => {
    const outside = document.createElement("button");
    document.body.append(outside);
    outside.focus();
    const wrapper = mount(WorkspaceSidePanel, { props: { open: true, label: "Dock", tabs, activeTab: "outline", autoFocus: false, minWidth: 300, defaultWidth: 340 }, attachTo: document.body });
    expect(document.activeElement).toBe(outside);
    expect(wrapper.find("aside").attributes("style")).toContain("width: 340px");
    await wrapper.find("[role=separator]").trigger("keydown", { key: "Home" });
    expect(wrapper.find("aside").attributes("style")).toContain("width: 300px");
    wrapper.unmount();
  });

  it("is never narrower than a minimum that grows while it is open, and gives the reader's width back", async () => {
    const wrapper = mount(WorkspaceSidePanel, { props: { open: true, label: "Dock", tabs, activeTab: "outline", autoFocus: false, minWidth: 300, defaultWidth: 340 }, attachTo: document.body });
    await wrapper.find("[role=separator]").trigger("keydown", { key: "Home" });
    expect(wrapper.find("aside").attributes("style")).toContain("width: 300px");
    // A page with a fourth tab asks for more room: the remembered 300 px would cut its last tab.
    await wrapper.setProps({ minWidth: 340 });
    expect(wrapper.find("aside").attributes("style")).toContain("width: 340px");
    await wrapper.setProps({ minWidth: 300 });
    expect(wrapper.find("aside").attributes("style")).toContain("width: 300px");
    wrapper.unmount();
  });
});
