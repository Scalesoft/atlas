import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, nextTick } from "vue";
import PageOutline from "./PageOutline.vue";

function article() {
  const root = document.createElement("section");
  root.innerHTML = `<div class="ProseMirror" contenteditable="false"><h2 data-heading-id="a">Úvod</h2><p>…</p><h3>Podrobnosti</h3><h2 data-heading-id="c">Závěr</h2></div>`;
  document.body.append(root);
  return root;
}
const frames = () => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
afterEach(() => { document.body.innerHTML = ""; vi.restoreAllMocks(); });

describe("PageOutline", () => {
  it("lists the headings in the dock and focuses the chosen one while reading", async () => {
    const root = article();
    const wrapper = mount(PageOutline, { props: { root }, attachTo: document.body });
    await flushPromises();
    expect(wrapper.get("nav").attributes("aria-label")).toBe("Na této stránce");
    const entries = wrapper.findAll(".page-outline-entry");
    expect(entries.map((entry) => [entry.text(), entry.attributes("data-level")])).toEqual([["Úvod", "0"], ["Podrobnosti", "1"], ["Závěr", "0"]]);
    const heading = root.querySelector<HTMLElement>("h3")!;
    const scroll = vi.fn();
    heading.scrollIntoView = scroll;
    await entries[1]!.trigger("click");
    expect(scroll).toHaveBeenCalledWith({ block: "start", behavior: expect.any(String) });
    expect(document.activeElement).toBe(heading);
    expect(wrapper.emitted("navigate")).toHaveLength(1);
    wrapper.unmount();
  });

  it("stands the stored document's headings in, inert, until the page's own are rendered, and does not unfold them again (dock CLS)", async () => {
    const root = document.createElement("section");
    document.body.append(root);
    const value = { format: "atlas-rich", version: 1, plugins: [], document: { type: "doc", content: [
      { type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: "Úvod" }] },
      { type: "callout", content: [{ type: "heading", attrs: { level: 3 }, content: [{ type: "text", text: "Podrobnosti" }] }] },
      { type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: "Závěr" }] },
    ] } };
    const animate = vi.fn(() => ({ finished: Promise.resolve(), cancel() {} }));
    Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, value: animate });
    try {
      const wrapper = mount(PageOutline, { props: { root, document: value as never }, attachTo: document.body });
      await flushPromises();
      const rows = () => wrapper.findAll(".page-outline-entry").map((entry) => [entry.text(), entry.attributes("data-level")]);
      expect(rows()).toEqual([["Úvod", "0"], ["Podrobnosti", "1"], ["Závěr", "0"]]);
      expect(wrapper.get("ol").attributes("inert")).toBeDefined();
      expect(wrapper.get("ol").attributes("aria-hidden")).toBe("true");
      // The page renders: its own headings take the rows' place, live, without the list leaving or unfolding.
      animate.mockClear();
      root.innerHTML = `<div class="ProseMirror"><h2 data-heading-id="a">Úvod</h2><h3>Podrobnosti</h3><h2 data-heading-id="c">Závěr</h2></div>`;
      await frames(); await flushPromises();
      await vi.waitFor(() => expect(wrapper.get("ol").attributes("inert")).toBeUndefined());
      expect(rows()).toEqual([["Úvod", "0"], ["Podrobnosti", "1"], ["Závěr", "0"]]);
      expect(animate).not.toHaveBeenCalledWith(expect.arrayContaining([expect.objectContaining({ height: expect.anything() })]), expect.anything());
      wrapper.unmount();
    } finally { Reflect.deleteProperty(HTMLElement.prototype, "animate"); }
  });

  it("keeps the chosen heading active until the reader scrolls the page themselves", async () => {
    // One of the last headings cannot reach the reading line: at the end of the page the position names the last one.
    const root = article();
    const tops = [-900, -400, 300];
    root.querySelectorAll<HTMLElement>("h2, h3").forEach((heading, index) => { heading.getBoundingClientRect = () => ({ top: tops[index]! }) as DOMRect; heading.scrollIntoView = vi.fn(); });
    const wrapper = mount(PageOutline, { props: { root }, attachTo: document.body });
    // The headings are measured once the frame that shows them has painted (no layout forced at open).
    await flushPromises(); await frames(); await new Promise(resolve => setTimeout(resolve));
    const current = () => wrapper.findAll(".page-outline-entry").findIndex((entry) => entry.attributes("aria-current") === "location");
    expect(current()).toBe(1);
    await wrapper.findAll(".page-outline-entry")[2]!.trigger("click");
    expect(current()).toBe(2);
    // The scroll the choice started reports itself; the choice holds.
    document.dispatchEvent(new Event("scroll"));
    await frames(); await nextTick();
    expect(current()).toBe(2);
    window.dispatchEvent(new Event("wheel"));
    await frames(); await nextTick();
    expect(current()).toBe(1);
    wrapper.unmount();
  });

  it("scrolls only its own column to keep the active heading in view", async () => {
    const root = article();
    const tops = [-900, 20, 900];
    root.querySelectorAll<HTMLElement>("h2, h3").forEach((heading, index) => { heading.getBoundingClientRect = () => ({ top: tops[index]! }) as DOMRect; });
    const column = document.createElement("div");
    column.style.overflowY = "auto";
    document.body.append(column);
    Object.defineProperties(column, { scrollHeight: { value: 2000 }, clientHeight: { value: 300 } });
    column.getBoundingClientRect = () => ({ top: 100, bottom: 400 }) as DOMRect;
    const scrollBy = column.scrollBy = vi.fn();
    const pageScroll = vi.spyOn(window, "scrollTo").mockImplementation(() => {});
    const wrapper = mount(PageOutline, { props: { root }, attachTo: column });
    await flushPromises();
    const entries = wrapper.findAll(".page-outline-entry");
    entries.forEach((entry, index) => { entry.element.getBoundingClientRect = () => ({ top: 120 + index * 400, bottom: 150 + index * 400 }) as DOMRect; });
    // The reader scrolls on: the last heading passes the reading line, its entry is below the column's view.
    tops[2] = 10;
    document.dispatchEvent(new Event("scroll"));
    await frames(); await nextTick(); await nextTick();
    expect(entries[2]!.attributes("aria-current")).toBe("location");
    expect(scrollBy).toHaveBeenCalledWith({ top: 950 - 400 + 36, behavior: expect.any(String) });
    expect(pageScroll).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it("steps aside as a collapsed disclosure and moves what is below it with a transform, not a jump", async () => {
    const root = article();
    const moves: { el: Element; frames: Keyframe[] }[] = [];
    Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, value(this: HTMLElement, frames: Keyframe[]) { moves.push({ el: this, frames }); return { finished: Promise.resolve(), cancel() {} }; } });
    // As in the page: the row is the last thing in the header, the article's body follows the header.
    const wrapper = mount(defineComponent({
      components: { PageOutline }, props: { collapsed: Boolean }, setup: () => ({ root }),
      template: `<div><header><h1>Titulek</h1><PageOutline :root="root" variant="disclosure" :collapsed="collapsed" /></header><section id="body" /></div>`,
    }), { props: { collapsed: false }, attachTo: document.body });
    const body = wrapper.get("#body").element as HTMLElement;
    // The body starts 64 px lower while the row is there.
    body.getBoundingClientRect = () => ({ top: wrapper.find(".page-outline-disclosure").exists() ? 264 : 200 }) as DOMRect;
    try {
      await flushPromises();
      expect(wrapper.find(".page-outline-disclosure").exists()).toBe(true);
      moves.length = 0;
      await wrapper.setProps({ collapsed: true }); await nextTick();
      expect(wrapper.find(".page-outline-disclosure").exists()).toBe(false);
      expect(moves).toEqual([{ el: body, frames: [{ translate: "0px 64px" }, { translate: "0px 0px" }] }]);
      moves.length = 0;
      await wrapper.setProps({ collapsed: false }); await nextTick();
      expect(wrapper.find(".page-outline-disclosure").exists()).toBe(true);
      expect(moves.find(({ el }) => el === body)?.frames).toEqual([{ translate: "0px -64px" }, { translate: "0px 0px" }]);
    } finally { wrapper.unmount(); Reflect.deleteProperty(HTMLElement.prototype, "animate"); }
  });

  it("lets the row fade out where it stood while the page below moves up, instead of vanishing at full opacity", async () => {
    const root = article();
    const fades: { el: Element; frames: Keyframe[]; finish: () => void }[] = [];
    Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, value(this: HTMLElement, keyframes: Keyframe[]) {
      let finish = () => {};
      const finished = new Promise<void>((resolve) => { finish = resolve; });
      fades.push({ el: this, frames: keyframes, finish });
      return { finished, cancel() {} };
    } });
    const wrapper = mount(defineComponent({
      components: { PageOutline }, props: { collapsed: Boolean }, setup: () => ({ root }),
      template: `<div><header><h1>Titulek</h1><PageOutline :root="root" variant="disclosure" :collapsed="collapsed" /></header><section id="body" /></div>`,
    }), { props: { collapsed: false }, attachTo: document.body, global: { stubs: { transition: false } } });
    try {
      await flushPromises();
      fades.length = 0;
      await wrapper.setProps({ collapsed: true }); await nextTick();
      const row = wrapper.find(".page-outline-disclosure");
      // Still there, but out of the flow (the page below is already moving into its place) and fading.
      expect(row.exists()).toBe(true);
      expect((row.element as HTMLElement).style.position).toBe("absolute");
      expect(row.attributes("inert")).toBeDefined();
      expect(fades.find(({ el }) => el === row.element)?.frames).toEqual([{ opacity: 1 }, { opacity: 0 }]);
      fades.find(({ el }) => el === row.element)!.finish();
      await flushPromises();
      expect(wrapper.find(".page-outline-disclosure").exists()).toBe(false);
    } finally { wrapper.unmount(); Reflect.deleteProperty(HTMLElement.prototype, "animate"); }
  });

  describe("the marker of the active heading", () => {
    // jsdom has no layout: an entry is 30 px high and the entries stand 32 px apart.
    function stubGeometry() {
      const row = (el: HTMLElement) => (el.tagName === "LI" ? [...el.parentElement!.children].indexOf(el) : -1);
      vi.spyOn(HTMLElement.prototype, "offsetTop", "get").mockImplementation(function (this: HTMLElement) { return row(this) * 32; });
      vi.spyOn(HTMLElement.prototype, "offsetHeight", "get").mockImplementation(function (this: HTMLElement) { return row(this) >= 0 ? 30 : 0; });
      vi.spyOn(HTMLElement.prototype, "offsetLeft", "get").mockImplementation(function (this: HTMLElement) { return row(this) >= 0 ? 8 : 0; });
      vi.spyOn(HTMLElement.prototype, "offsetWidth", "get").mockImplementation(function (this: HTMLElement) { return row(this) >= 0 ? 280 : 0; });
    }
    function scrolledArticle() {
      const root = article();
      const tops = [-900, -400, 300];
      root.querySelectorAll<HTMLElement>("h2, h3").forEach((heading, index) => { heading.getBoundingClientRect = () => ({ top: tops[index]! }) as DOMRect; heading.scrollIntoView = vi.fn(); });
      return root;
    }

    it("is one marker that moves to the chosen entry, instead of each entry recolouring itself", async () => {
      stubGeometry();
      const wrapper = mount(PageOutline, { props: { root: scrolledArticle() }, attachTo: document.body });
      await flushPromises(); await frames();
      const list = wrapper.get("ol");
      expect(list.attributes("data-marker")).toBeDefined();
      expect(list.attributes("style")).toContain("--outline-marker-y: 32px");
      expect(list.attributes("style")).toContain("--outline-marker-h: 30px");
      expect(list.attributes("style")).toContain("--outline-marker-x: 8px");
      expect(list.attributes("style")).toContain("--outline-marker-w: 280px");
      await wrapper.findAll(".page-outline-entry")[2]!.trigger("click");
      await nextTick(); await nextTick();
      expect(list.attributes("style")).toContain("--outline-marker-y: 64px");
      wrapper.unmount();
    });

    it("leaves the recolouring to the entries under reduced motion, which never snaps a marker", async () => {
      stubGeometry();
      document.documentElement.setAttribute("data-atlas-reduced-motion", "");
      try {
        const wrapper = mount(PageOutline, { props: { root: scrolledArticle() }, attachTo: document.body });
        await flushPromises(); await frames();
        expect(wrapper.get("ol").attributes("data-marker")).toBeUndefined();
        wrapper.unmount();
      } finally { document.documentElement.removeAttribute("data-atlas-reduced-motion"); }
    });
  });

  it("follows edits of the document", async () => {
    const root = article();
    const wrapper = mount(PageOutline, { props: { root }, attachTo: document.body });
    await flushPromises();
    root.querySelector(".ProseMirror")!.insertAdjacentHTML("beforeend", "<h2>Nová sekce</h2>");
    await frames(); await nextTick();
    expect(wrapper.findAll(".page-outline-entry").map((entry) => entry.text())).toEqual(["Úvod", "Podrobnosti", "Závěr", "Nová sekce"]);
    wrapper.unmount();
  });

  it("is one collapsed row as a disclosure, unfolds on demand and closes with Escape", async () => {
    const root = article();
    const wrapper = mount(PageOutline, { props: { root, variant: "disclosure" }, attachTo: document.body });
    await flushPromises();
    const toggle = wrapper.get(".page-outline-toggle");
    expect(toggle.text()).toContain("Na této stránce");
    expect(toggle.text()).toContain("3 sekce");
    expect(toggle.attributes("aria-expanded")).toBe("false");
    expect(wrapper.get("ol").attributes("hidden")).toBeDefined();
    await toggle.trigger("click");
    expect(toggle.attributes("aria-expanded")).toBe("true");
    expect(wrapper.get("ol").attributes("hidden")).toBeUndefined();
    await wrapper.get("ol").trigger("keydown", { key: "Escape" });
    expect(toggle.attributes("aria-expanded")).toBe("false");
    expect(document.activeElement).toBe(toggle.element);
    wrapper.unmount();
  });

  it("draws nothing for a page without enough headings", async () => {
    const root = document.createElement("section");
    root.innerHTML = `<div class="ProseMirror"><h2>Jediný</h2></div>`;
    const disclosure = mount(PageOutline, { props: { root, variant: "disclosure" } });
    await flushPromises();
    expect(disclosure.find("nav").exists()).toBe(false);
    const list = mount(PageOutline, { props: { root: null } });
    expect(list.find("nav").exists()).toBe(false);
    // Left mounted, its pending frame and timer ran after the test environment was gone (CI, 4.1.0).
    disclosure.unmount();
    list.unmount();
  });

  it("lets a heading with a stable id be dragged out as that section of the page (onto a board)", async () => {
    const pageId = "3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b";
    const wrapper = mount(PageOutline, { props: { root: article(), pageId }, attachTo: document.body });
    await flushPromises();
    const entries = wrapper.findAll(".page-outline-entry");
    expect(entries.map(entry => entry.attributes("draggable"))).toEqual(["true", undefined, "true"]);
    const data = new Map<string, string>();
    const dataTransfer = { setData: (type: string, value: string) => data.set(type, value), effectAllowed: "" };
    await entries[2]!.trigger("dragstart", { dataTransfer });
    expect(JSON.parse(data.get("application/x-atlas-page-section")!)).toEqual({ resourceId: pageId, anchor: "c" });
    expect(data.get("text/uri-list")).toMatch(new RegExp(`#/pages/${pageId}\\?heading=c$`));
    expect(dataTransfer.effectAllowed).toBe("copyLink");
    // Without the page nothing is draggable, and a click still navigates.
    const plain = mount(PageOutline, { props: { root: article() }, attachTo: document.body });
    await flushPromises();
    expect(plain.findAll(".page-outline-entry").map(entry => entry.attributes("draggable"))).toEqual([undefined, undefined, undefined]);
    wrapper.unmount();
    plain.unmount();
  });
});
