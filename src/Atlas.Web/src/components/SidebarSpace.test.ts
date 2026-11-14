import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import SidebarSpace from "./SidebarSpace.vue";

// The tree unfolds clipped to an animated height, and Chromium drops a native drag that starts inside it: the page tree
// could not be reordered for the first moments after a space opened (tree-navigation.spec.ts in the browser acceptance).
afterEach(() => { delete (Element.prototype as Partial<Element>).animate; vi.restoreAllMocks(); vi.useRealTimers(); });

it("finishes the unfolding of the tree as soon as the tree is pressed, so a page can be dragged at once", async () => {
  const finish = vi.fn();
  Element.prototype.animate = vi.fn(() => ({ finished: new Promise(() => {}), finish, cancel: vi.fn() }) as unknown as Animation) as unknown as Element["animate"];
  const space = { id: "s", name: "Provoz", icon: "book", color: "blue", role: "Admin" as const, pinPolicy: "None" as const, pinned: true, pageCount: 1, lastChangedAt: null };
  const page = { id: "p", spaceId: "s", parentId: null, title: "Návod", version: 1, isPublished: true, sortOrder: 1, canEdit: true };
  const wrapper = mount(SidebarSpace, { props: { space, pages: [page], expanded: false, canWrite: true }, attachTo: document.body, global: { stubs: { transition: false } } });
  await wrapper.setProps({ expanded: true });
  expect(Element.prototype.animate).toHaveBeenCalled();
  await wrapper.get(".sidebar-space-tree .tree-page").trigger("pointerdown");
  // The unfolding is the tree's height and, beside it, the card's room and frame: all of it ends at once.
  expect(finish).toHaveBeenCalledTimes(vi.mocked(Element.prototype.animate).mock.calls.length);
  wrapper.unmount();
});

// The owner's report (2.6.2): a space opened before its tree had loaded unfolded for „Načítání stránek…“ and then on to
// the pages „in the time it had left“. An answer that came late in the first unfolding (most of them, on production)
// sent a tree of hundreds of pixels across in the last few frames.
it("waits for the pages of a space opened before its tree has loaded and then unfolds once, to the whole tree", async () => {
  const motion = recordAnimations(0);
  const wrapper = mounted({ pages: undefined, expanded: false });
  await wrapper.setProps({ expanded: true });
  expect(wrapper.find(".sidebar-space-loading").exists()).toBe(true);
  const waiting = [...motion.started];
  expect(waiting.length).toBeGreaterThan(1);
  // Everything stands in its first frame: the tree at no height, the card not there yet.
  expect(waiting.every(({ animation }) => animation.pause.mock.calls.length === 1 && animation.play.mock.calls.length === 0)).toBe(true);
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockReturnValue({ top: 100, height: 600 } as DOMRect);
  await wrapper.setProps({ pages: [navod] });
  expect(waiting.every(({ animation }) => animation.cancel.mock.calls.length === 1)).toBe(true);
  const fresh = motion.started.slice(waiting.length), tree = motion.tree().at(-1)!;
  expect(fresh).toHaveLength(waiting.length);
  expect(tree.frames[0]).toMatchObject({ height: "0px", opacity: 0 });
  expect(tree.frames.at(-1)).toMatchObject({ height: "600px", opacity: 1 });
  expect(motion.frame().at(-1)!.frames).toEqual([{ opacity: 0 }, { opacity: 1 }]);
  // One clock again, and as long as 600 px take (the 1.25× cap).
  expect(fresh.map(({ options }) => options.duration)).toEqual(fresh.map(() => 250));
  expect(fresh.every(({ animation }) => animation.pause.mock.calls.length === 0)).toBe(true);
  wrapper.unmount();
});

it("unfolds for the loading line when the answer is slow, and then on to the pages as a motion of its own length", async () => {
  vi.useFakeTimers();
  const motion = recordAnimations(0.9);
  const wrapper = mounted({ pages: undefined, expanded: false });
  await wrapper.setProps({ expanded: true });
  expect(motion.started.every(({ animation }) => animation.play.mock.calls.length === 0)).toBe(true);
  // As long as any loading state waits before it shows.
  vi.advanceTimersByTime(150);
  expect(motion.started.every(({ animation }) => animation.play.mock.calls.length === 1)).toBe(true);
  const started = motion.started.length, line = motion.tree()[0]!;
  // The tree has reached 28 px of the line when 600 px of pages arrive.
  let read = 0;
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(() => ({ top: 100, height: read++ < 2 ? 28 : 600 } as DOMRect));
  await wrapper.setProps({ pages: [navod] });
  // Only the tree goes on; the card's room and frame are not restarted.
  expect(motion.started).toHaveLength(started + 1);
  expect(line.animation.cancel).toHaveBeenCalledTimes(1);
  const on = motion.tree().at(-1)!;
  expect(on.frames[0]).toMatchObject({ height: "28px" });
  expect(on.frames.at(-1)).toMatchObject({ height: "600px", opacity: 1 });
  // 572 px take their own time (the 1.25× cap), not what was left of the line's 220 ms.
  expect(on.options.duration).toBeCloseTo(250);
  wrapper.unmount();
});

it("unfolds once for the message when the load fails while the space waits", async () => {
  // Played on as they were, the held animations ended at the height of the loading line, and the taller message
  // pushed the rows below down in one frame.
  const motion = recordAnimations(0);
  const wrapper = mounted({ pages: undefined, expanded: false });
  await wrapper.setProps({ expanded: true });
  const waiting = [...motion.started];
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockReturnValue({ top: 100, height: 49 } as DOMRect);
  await wrapper.setProps({ loadFailed: true });
  expect(wrapper.find('[role="alert"]').exists()).toBe(true);
  expect(waiting.every(({ animation }) => animation.cancel.mock.calls.length === 1 && animation.play.mock.calls.length === 0)).toBe(true);
  expect(motion.tree().at(-1)!.frames).toMatchObject([{ height: "0px", opacity: 0 }, { height: "49px", opacity: 1 }]);
  wrapper.unmount();
});

it("has nothing to fold when the space is folded again while it still waits, and waits again the next time", async () => {
  const motion = recordAnimations(0);
  const wrapper = mounted({ pages: undefined, expanded: false });
  await wrapper.setProps({ expanded: true });
  const waiting = [...motion.started];
  await wrapper.setProps({ expanded: false });
  // No fold of a tree that never showed: it is gone at once and nothing stays paused on the item.
  expect(wrapper.find(".sidebar-space-tree").exists()).toBe(false);
  expect(motion.started).toHaveLength(waiting.length);
  expect(waiting.every(({ animation }) => animation.cancel.mock.calls.length === 1)).toBe(true);
  // Opened again, it is a first unfolding, not a turn of a fold: it waits for the pages as before.
  await wrapper.setProps({ expanded: true });
  const again = motion.started.slice(waiting.length);
  expect(again).toHaveLength(waiting.length);
  expect(again.every(({ animation }) => animation.pause.mock.calls.length === 1)).toBe(true);
  // An item that goes away while it waits takes its paused animations with it.
  wrapper.unmount();
  expect(again.every(({ animation }) => animation.cancel.mock.calls.length === 1)).toBe(true);
});

it("asks for the tree of a folded space that has not loaded when its arrow is pressed, ahead of the click, not on hover", async () => {
  const wrapper = mounted({ pages: undefined, expanded: false });
  const arrow = wrapper.get(".sidebar-space-row .tree-toggle");
  // A pointer that only crosses the arrows would load every pinned tree.
  await arrow.trigger("pointerenter");
  expect(wrapper.emitted("intent")).toBeUndefined();
  await arrow.trigger("pointerdown");
  expect(wrapper.emitted("intent")).toHaveLength(1);
  await wrapper.setProps({ pages: [navod] });
  await arrow.trigger("pointerdown");
  expect(wrapper.emitted("intent")).toHaveLength(1);
  wrapper.unmount();
});

// The owner's report (2.6.1): the card's border, padding and margin appeared at once before the tree unfolded and
// vanished at once after it had folded, so the rows below jumped 16 and 12 px in a single frame.
const provoz = { id: "s", name: "Provoz", icon: "book", color: "blue", role: "Admin" as const, pinPolicy: "None" as const, pinned: true, pageCount: 1, lastChangedAt: null };
const navod = { id: "p", spaceId: "s", parentId: null, title: "Návod", version: 1, isPublished: true, sortOrder: 1, canEdit: true };
interface Started { target: Element; frames: Keyframe[]; options: KeyframeAnimationOptions; animation: { finish: () => void; cancel: ReturnType<typeof vi.fn>; pause: ReturnType<typeof vi.fn>; play: ReturnType<typeof vi.fn>; playState: string } }
/** Every animation the component starts, with what it animates; `progress` is how far each one has got. */
function recordAnimations(progress = 0.5) {
  const started: Started[] = [];
  Element.prototype.animate = function (this: Element, frames: Keyframe[], options: KeyframeAnimationOptions) {
    let end!: () => void;
    const animation = { finished: new Promise<void>((resolve) => { end = resolve; }), finish: () => end(), cancel: vi.fn(), pause: vi.fn(), play: vi.fn(), playState: "running", currentTime: 50, effect: { getComputedTiming: () => ({ endTime: 280, progress }) } };
    started.push({ target: this, frames, options, animation });
    return animation as unknown as Animation;
  } as unknown as Element["animate"];
  return {
    started,
    tree: () => started.filter(({ target }) => target.classList.contains("sidebar-space-tree")),
    room: () => started.filter(({ target, options }) => target.classList.contains("sidebar-space") && !options.pseudoElement),
    frame: () => started.filter(({ options }) => options.pseudoElement === "::before"),
  };
}
// Only the room under the tree comes and goes. The room above the row is the item's all the time, so the row a reader
// presses stays under the pointer: animated with the rest, it sank 6 px while its tree unfolded (2.6.1).
const noRoom = { paddingBottom: "0px", marginBottom: "0px" };
const mounted = (props: Record<string, unknown>) => mount(SidebarSpace, { props: { space: provoz, pages: [navod], ...props } as never, attachTo: document.body, global: { stubs: { transition: false } } });

it("brings the card's room and frame in with the tree, on one clock, instead of all at once before it", async () => {
  const motion = recordAnimations();
  const wrapper = mounted({ expanded: false });
  await wrapper.setProps({ expanded: true });
  expect(motion.tree()).toHaveLength(1);
  expect(motion.tree()[0]!.frames[0]).toMatchObject({ height: "0px", opacity: 0, marginTop: "0px", marginBottom: "0px" });
  expect(motion.room()).toHaveLength(1);
  expect(motion.room()[0]!.frames[0]).toEqual(noRoom);
  expect(motion.frame().map(({ frames }) => frames)).toEqual([[{ opacity: 0 }, { opacity: 1 }]]);
  expect(new Set(motion.started.map(({ options }) => options.duration)).size).toBe(1);
  // Entrances keep no fill: when they end, the item shows its own styles.
  expect(motion.started.every(({ options }) => options.fill === "none")).toBe(true);
  wrapper.unmount();
});

it("takes the card's room and frame away with the tree and leaves no animation on the item", async () => {
  const motion = recordAnimations();
  const wrapper = mounted({ expanded: true });
  expect(motion.started).toHaveLength(0);
  await wrapper.setProps({ expanded: false });
  const tree = motion.tree()[0]!, room = motion.room()[0]!, frame = motion.frame()[0]!;
  expect(tree.frames.at(-1)).toMatchObject({ height: "0px", opacity: 0, marginTop: "0px", marginBottom: "0px" });
  expect(room.frames.at(-1)).toEqual(noRoom);
  expect(frame.frames).toEqual([{ opacity: 1 }, { opacity: 0 }]);
  expect(new Set(motion.started.map(({ options }) => options.duration)).size).toBe(1);
  // The tree stays, out of use, until it has folded; the room and the frame hold their last frame until then.
  expect(wrapper.get(".sidebar-space-tree").attributes()).toMatchObject({ inert: "", "aria-hidden": "true" });
  expect(motion.started.every(({ options }) => options.fill === "forwards")).toBe(true);
  tree.animation.finish();
  await new Promise((resolve) => setTimeout(resolve));
  expect(wrapper.find(".sidebar-space-tree").exists()).toBe(false);
  expect(room.animation.cancel).toHaveBeenCalledTimes(1);
  expect(frame.animation.cancel).toHaveBeenCalledTimes(1);
  wrapper.unmount();
});

it("turns around from where it is when the space is toggled again midway, in both directions", async () => {
  const motion = recordAnimations(0.25);
  const wrapper = mounted({ expanded: true });
  await wrapper.setProps({ expanded: false });
  // A quarter of the fold is done: three quarters of the card are left, and the unfolding starts there.
  await wrapper.setProps({ expanded: true });
  expect(motion.tree()).toHaveLength(2);
  expect(motion.tree()[1]!.frames[0]).toMatchObject({ opacity: 0.75 });
  expect(motion.frame()[1]!.frames).toEqual([{ opacity: 0.75 }, { opacity: 1 }]);
  expect(motion.room()[0]!.animation.cancel).toHaveBeenCalledTimes(1);
  expect(motion.frame()[0]!.animation.cancel).toHaveBeenCalledTimes(1);
  // And a fold that interrupts this unfolding starts from the opacity the tree has reached, not from full, and from
  // its height to the fraction of a pixel: rounded (offsetHeight), the turn began with a step of up to half a pixel.
  (wrapper.get(".sidebar-space-tree").element as HTMLElement).style.opacity = "0.8";
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockReturnValue({ top: 0, height: 127.6 } as DOMRect);
  await wrapper.setProps({ expanded: false });
  expect(motion.tree()[2]!.frames[0]).toMatchObject({ opacity: 0.8, height: "127.6px" });
  // The card: 0.75 and a quarter of the way on to 1.
  expect(motion.frame()[2]!.frames).toEqual([{ opacity: 0.8125 }, { opacity: 0 }]);
  expect(motion.tree()[1]!.animation.cancel).toHaveBeenCalledTimes(1);
  wrapper.unmount();
});

it("moves only the part of a long tree that can be seen when its chevron folds or unfolds it", async () => {
  // A tree of hundreds of rows reaches far below the window: eased over all of it, the visible part filled within
  // a frame or two when unfolding and stayed empty until the last moments when folding.
  const motion = recordAnimations();
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockReturnValue({ top: 100, height: 5000 } as DOMRect);
  vi.spyOn(window, "innerHeight", "get").mockReturnValue(768);
  // The shell answers the chevron at once, as App does.
  const wrapper = mounted({ expanded: false, onToggle: () => { void wrapper.setProps({ expanded: !wrapper.props("expanded") }); } });
  await wrapper.get(".sidebar-space-row .tree-toggle").trigger("click");
  expect(motion.tree()[0]!.frames.at(-1)).toMatchObject({ height: "668px" });
  await wrapper.get(".sidebar-space-row .tree-toggle").trigger("click");
  expect(motion.tree()[1]!.frames[0]).toMatchObject({ height: "668px" });
  for (const { animation } of motion.started) animation.finish();
  await new Promise((resolve) => setTimeout(resolve));
  // Opened from elsewhere (going to one of its pages), it unfolds whole: the list follows it down to that page's row.
  await wrapper.setProps({ expanded: true });
  expect(motion.tree()[2]!.frames.at(-1)).toMatchObject({ height: "5000px" });
  wrapper.unmount();
});

it("unfolds on from the loading line when the pages arrive after the unfolding has ended", async () => {
  const motion = recordAnimations();
  const wrapper = mounted({ pages: undefined, expanded: false });
  await wrapper.setProps({ expanded: true });
  for (const { animation } of motion.started) animation.finish();
  await new Promise((resolve) => setTimeout(resolve));
  const before = motion.started.length;
  // The loading line is 30.6 px tall, the tree that replaces it 600.
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) { return { top: 100, height: this.querySelector(".page-tree") ? 600 : 30.6 } as DOMRect; });
  await wrapper.setProps({ pages: [navod] });
  expect(motion.started).toHaveLength(before + 1);
  expect(motion.tree().at(-1)!.frames).toEqual([{ height: "30.6px", overflow: "clip" }, { height: "600px", overflow: "clip" }]);
  // Pressing the tree ends this too, so a row can be dragged at once.
  const finish = vi.spyOn(motion.tree().at(-1)!.animation, "finish");
  await wrapper.get(".sidebar-space-tree .tree-page").trigger("pointerdown");
  expect(finish).toHaveBeenCalledTimes(1);
  wrapper.unmount();
});

// The space menu is portalled and exists only while open, so its items are found in the document.
const settleMenu = () => new Promise(resolve => setTimeout(resolve, 40));
const menuItem = (selector: string) => document.querySelector<HTMLElement>(`[role=menu] ${selector}`);

// „Řadit abecedně“ of the root and „Přesouvat stránky tažením“ (3.6): switches in the space's menu, for writers; the shell saves them.
it("offers a writer the root's „Řadit abecedně“ and „Přesouvat stránky tažením“ as ticked switches and marks a sorted root", async () => {
  const space = { id: "s", name: "Provoz", icon: "book", color: "blue", role: "Writer" as const, pinPolicy: "None" as const, pinned: true, pageCount: 1, lastChangedAt: null };
  const pages = [{ id: "p", spaceId: "s", parentId: null, title: "Návod", version: 1, isPublished: true, canEdit: true }];
  const writer = mount(SidebarSpace, { props: { space, pages, expanded: true, canWrite: true, rootByTitle: true, drag: false }, attachTo: document.body });
  expect(writer.find("[data-sidebar-space-sorted]").exists()).toBe(true);
  expect(writer.get(".tree-row").attributes("draggable")).toBe("false");
  await writer.get('[aria-label="Další akce prostoru Provoz"]').trigger("click"); await settleMenu();
  expect(menuItem("[data-sidebar-space-sort]")!.getAttribute("role")).toBe("menuitemcheckbox");
  expect(menuItem("[data-sidebar-space-sort]")!.getAttribute("aria-checked")).toBe("true");
  expect(menuItem("[data-sidebar-space-drag]")!.getAttribute("aria-checked")).toBe("false");
  menuItem("[data-sidebar-space-sort]")!.click(); await settleMenu();
  expect(writer.emitted("sortRoot")).toEqual([[false]]);
  await writer.get('[aria-label="Další akce prostoru Provoz"]').trigger("click"); await settleMenu();
  menuItem("[data-sidebar-space-drag]")!.click(); await settleMenu();
  expect(writer.emitted("dragPages")).toEqual([[true]]);
  writer.unmount();
  document.body.replaceChildren();

  // A reader sees the order, not the switches or the mark.
  const reader = mount(SidebarSpace, { props: { space: { ...space, role: "Reader" as const }, pages, expanded: true, rootByTitle: true }, attachTo: document.body });
  expect(reader.find("[data-sidebar-space-sorted]").exists()).toBe(false);
  await reader.get('[aria-label="Další akce prostoru Provoz"]').trigger("click"); await settleMenu();
  expect(menuItem("[data-sidebar-space-sort]")).toBeNull();
  reader.unmount();
  document.body.replaceChildren();
});

it("eases the tree to its new height when „Sbalit vše“ or „Rozbalit vše“ changes many branches at once", async () => {
  // PageTree animates a single toggled branch and changes many at once: the rows below jumped by the whole difference.
  const motion = recordAnimations();
  const child = { ...navod, id: "c", parentId: "p", title: "Podstránka" };
  let height = 300;
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(() => ({ top: 100, height } as DOMRect));
  // The shell answers at once, as App does; the tree is 120 px tall once its branches are folded.
  const wrapper = mounted({ pages: [navod, child], expanded: true, collapsedIds: new Set<string>(), onSetCollapsed: (ids: string[], collapsed: boolean) => { height = collapsed ? 120 : 300; void wrapper.setProps({ collapsedIds: new Set(collapsed ? ids : []) }); } });
  await wrapper.get('[aria-label="Další akce prostoru Provoz"]').trigger("click"); await settleMenu();
  [...document.querySelectorAll<HTMLElement>("[role=menu] [role=menuitem]")].find((item) => item.textContent?.includes("Sbalit vše"))!.click(); await settleMenu();
  expect(wrapper.emitted("setCollapsed")).toEqual([[["p"], true]]);
  expect(motion.tree().at(-1)!.frames).toEqual([{ height: "300px", overflow: "clip" }, { height: "120px", overflow: "clip" }]);
  // A branch folded by its own chevron moves by itself (PageTree); the tree is not eased around it.
  const count = motion.tree().length;
  height = 300;
  await wrapper.setProps({ collapsedIds: new Set<string>() });
  expect(motion.tree()).toHaveLength(count);
  wrapper.unmount();
  document.body.replaceChildren();
});

it("lets a space administrator open the space's settings from its menu, and shows nobody else that item", async () => {
  const space = { id: "s", name: "Provoz", icon: "book", color: "blue", role: "Admin" as const, pinPolicy: "None" as const, pinned: true, pageCount: 0, lastChangedAt: null };
  const manager = mount(SidebarSpace, { props: { space, pages: [], expanded: false, canWrite: true, canManage: true }, attachTo: document.body });
  await manager.get('[aria-label="Další akce prostoru Provoz"]').trigger("click"); await settleMenu();
  menuItem("[data-space-menu=settings]")!.click(); await settleMenu();
  expect(manager.emitted("settings")).toHaveLength(1);
  manager.unmount();
  document.body.replaceChildren();

  // A writer still has a menu, only without the administration entry.
  const writer = mount(SidebarSpace, { props: { space: { ...space, role: "Writer" as const }, pages: [], expanded: false, canWrite: true }, attachTo: document.body });
  await writer.get('[aria-label="Další akce prostoru Provoz"]').trigger("click"); await settleMenu();
  expect(menuItem("[role=menuitem]")).not.toBeNull();
  expect(menuItem("[data-space-menu=settings]")).toBeNull();
  writer.unmount();
  document.body.replaceChildren();
});

// Module Boards: the boards are nodes of the space's own tree (PageTree), not a row of their own; the menu creates one.
it("lists the space's boards in its tree and offers Nová tabule in its menu while the module is on", async () => {
  const space = { id: "s", name: "Provoz", icon: "book", color: "blue", role: "Writer" as const, pinPolicy: "None" as const, pinned: true, pageCount: 1, lastChangedAt: null };
  const pages = [{ id: "p", spaceId: "s", parentId: null, title: "Návod", version: 1, isPublished: true, canEdit: true }, { id: "b", spaceId: "s", parentId: null, title: "Plán", version: 0, isPublished: true, canEdit: true, type: "board" as const }];
  const wrapper = mount(SidebarSpace, { props: { space, pages, expanded: true, canWrite: true, boards: true }, attachTo: document.body });
  expect(wrapper.find("[data-sidebar-space-boards]").exists()).toBe(false);
  expect(wrapper.get('.sidebar-space-tree [data-tree-item="b"]').attributes("href")).toContain("#/boards/b");
  await wrapper.get('[aria-label="Další akce prostoru Provoz"]').trigger("click"); await settleMenu();
  menuItem("[data-space-menu=new-board]")!.click(); await settleMenu();
  expect(wrapper.emitted("createBoard")).toHaveLength(1);
  wrapper.unmount();
  document.body.replaceChildren();
  const off = mount(SidebarSpace, { props: { space, pages, expanded: true, canWrite: true }, attachTo: document.body });
  await off.get('[aria-label="Další akce prostoru Provoz"]').trigger("click"); await settleMenu();
  expect(menuItem("[data-space-menu=new-board]")).toBeNull();
  off.unmount();
  document.body.replaceChildren();
});

// The tree's filter (spec 2026-10-27 §5.2): a letter on a row opens it, the first match is preselected and Enter opens it; with no
// match Enter searches the whole wiki; Esc closes it and the tree is folded as it was.
it("turns a letter typed on a row into a filter that preselects the first match and opens it with Enter", async () => {
  const pages = [navod, { ...navod, id: "q", title: "Provozní řád", parentId: "p" }, { ...navod, id: "r", title: "Rozpočet" }];
  const wrapper = mounted({ pages, expanded: true, collapsedIds: new Set(["p"]) });
  await wrapper.get('[data-tree-item="p"]').trigger("keydown", { key: "ř" });
  await wrapper.vm.$nextTick();
  const field = wrapper.get("[data-sidebar-tree-filter] input");
  await field.setValue("rad");
  // The match inside the folded branch shows with its ancestor, preselected; what does not lead to it folds away.
  expect(wrapper.get('[data-tree-row="q"]').classes()).toContain("tree-filter-selected");
  expect(wrapper.get('[data-tree-row="r"]').element.closest("li")!.classList.contains("tree-filtered-out")).toBe(true);
  await field.trigger("keydown", { key: "Enter" });
  expect(wrapper.emitted("select")).toEqual([[expect.objectContaining({ id: "q" })]]);
  expect(wrapper.find("[data-sidebar-tree-filter]").exists()).toBe(false);

  await wrapper.get('[data-tree-item="r"]').trigger("keydown", { key: "x" });
  await wrapper.get("[data-sidebar-tree-filter] input").setValue("zzz");
  expect(wrapper.find("[data-tree-filter-wiki]").text()).toContain("Hledat „zzz“ v celé wiki");
  await wrapper.get("[data-sidebar-tree-filter] input").trigger("keydown", { key: "Enter" });
  expect(wrapper.emitted("searchWiki")).toEqual([["zzz"]]);
  // The first Esc empties the typed text and keeps the filter; the next one closes it (P3).
  await wrapper.get("[data-sidebar-tree-filter] input").trigger("keydown", { key: "Escape" });
  expect(wrapper.find("[data-sidebar-tree-filter]").exists()).toBe(true);
  expect(wrapper.get<HTMLInputElement>("[data-sidebar-tree-filter] input").element.value).toBe("");
  await wrapper.get("[data-sidebar-tree-filter] input").trigger("keydown", { key: "Escape" });
  expect(wrapper.find("[data-sidebar-tree-filter]").exists()).toBe(false);
  // Nothing the reader folded was unfolded for good.
  expect(wrapper.find('[data-tree-row="q"]').exists()).toBe(false);
  wrapper.unmount();
});

it("scrolls the preselected match into view as the arrows move it, nearest and never smooth", async () => {
  const scrolled: { id: string | undefined; options: unknown }[] = [];
  vi.spyOn(HTMLElement.prototype, "scrollIntoView").mockImplementation(function (this: HTMLElement, options?: boolean | ScrollIntoViewOptions) { scrolled.push({ id: this.dataset.treeRow, options }); });
  const pages = [navod, { ...navod, id: "q", title: "Provozní řád" }, { ...navod, id: "r", title: "Provozní plán" }];
  const wrapper = mounted({ pages, expanded: true });
  await wrapper.get('[data-tree-item="p"]').trigger("keydown", { key: "x" });
  const field = wrapper.get("[data-sidebar-tree-filter] input");
  await field.setValue("provozn");
  await flushPromises();
  expect(scrolled.at(-1)).toMatchObject({ id: "q", options: { block: "nearest" } });
  await field.trigger("keydown", { key: "ArrowDown" });
  await flushPromises();
  expect(scrolled.at(-1)).toMatchObject({ id: "r", options: { block: "nearest" } });
  expect(scrolled.every(({ options }) => (options as ScrollIntoViewOptions).behavior !== "smooth")).toBe(true);
  wrapper.unmount();
});

it("places what is dropped on the space row from outside as a reference node at the root", async () => {
  const wrapper = mounted({ expanded: true, canWrite: true });
  const drop = new Event("drop", { bubbles: true, cancelable: true });
  await wrapper.get(".sidebar-space-row").element.dispatchEvent(drop);
  expect(wrapper.emitted("rootDrop")?.[0]?.[0]).toBe(drop);
  wrapper.unmount();
});
