import type { Editor } from "@tiptap/core";
import shellMore from "../i18n/shell-more";
import { onScopeDispose, ref, shallowRef, watch, type Ref } from "vue";
import { scrollBehavior } from "../motion/aiMotion";
import { unfoldTo } from "./headingFold";

/**
 * „Na této stránce“ (refresh 2.5, boards 03 and 13): the headings of the open document for the page dock and the narrow
 * disclosure. It reads the rendered document (reading renderer or live editor) like the editor's own static outline
 * source does, so it needs no editor API and follows edits, remounts and the reading/editing switch by itself.
 */
export interface PageOutlineHeading {
  /** Stable while the document does not change: the persistent heading id, else the position. */
  key: string;
  /** 0 for the top level present in the document. */
  level: number;
  title: string;
  element: HTMLElement;
  headingId?: string;
}

const SKIP = "style, script, .ProseMirror-widget";
/**
 * The words of a heading: an inline component inside it (a contact) carries its own `<style>`, and the editor's widgets (a requirement's
 * chips and checkbox, the fold chevron, a collaborator's caret with its name) are drawn, not written; none is title. Read in place:
 * a copy of every heading without them cost 1.2 s per 30 keys typed into a requirement title at 4× CPU (150 requirements).
 */
function headingText(element: Element): string {
  let text = "";
  for (let child = element.firstChild; child; child = child.nextSibling) {
    if (child.nodeType === Node.TEXT_NODE) text += (child as Text).data;
    else if (child instanceof Element && !child.matches(SKIP)) text += headingText(child);
  }
  return text;
}

/** Headings h1–h6 of the document, without those inside embedded components (a table of contents, a diagram). */
/** The live editor building hidden under the rendered page as editing starts (DocumentEditor): not the page's outline until it shows. */
const BUILDING = ".editor-hydrating";
const building = (node: Node) => !!(node instanceof Element ? node : node.parentElement)?.closest(BUILDING);

export function collectHeadings(root: ParentNode | null | undefined): PageOutlineHeading[] {
  if (!root) return [];
  const elements = [...root.querySelectorAll<HTMLElement>(".ProseMirror :is(h1, h2, h3, h4, h5, h6)")]
    .filter(element => !element.closest("[data-md-module]") && !element.closest(BUILDING));
  if (!elements.length) return [];
  const levels = elements.map(element => Number(element.tagName.slice(1)));
  const top = Math.min(...levels);
  return elements.map((element, index) => {
    const headingId = element.dataset.headingId || undefined;
    return { key: headingId ?? `h${index}`, level: levels[index]! - top, title: headingText(element).replace(/\s+/g, " ").trim() || "Nadpis bez textu", element, ...(headingId ? { headingId } : {}) };
  });
}

interface JsonNode { type?: string; text?: string; attrs?: { level?: unknown }; content?: readonly JsonNode[] }
const jsonText = (node: JsonNode): string => node.text ?? (node.content ?? []).map(jsonText).join("");
/**
 * The headings of a stored document (its JSON), in document order with the levels `collectHeadings` gives: the outline's stand-in
 * rows while the page's own headings are not rendered yet (a page switch), so the dock keeps its height (as the contract's TOC stand-in, one-editor contract §2.4).
 */
export function documentHeadings(document: unknown): { key: string; level: number; title: string }[] {
  const found: { level: number; title: string }[] = [];
  const walk = (nodes: readonly JsonNode[] | undefined) => {
    for (const node of nodes ?? []) {
      // A level left out of the stored form is the default 1.
      if (node.type === "heading") found.push({ level: Number(node.attrs?.level ?? 1), title: jsonText(node).replace(/\s+/g, " ").trim() || "Nadpis bez textu" });
      else walk(node.content);
    }
  };
  walk((document as JsonNode | undefined)?.content);
  const top = Math.min(...found.map(heading => heading.level));
  return found.map((heading, index) => ({ key: `h${index}`, level: heading.level - top, title: heading.title }));
}

/**
 * The heading the reader is in: the last one whose top passed the reading line; above the first heading (the page's
 * introduction) the first one, as the board shows a page opened at its top. At the end of the page the last heading
 * wins even if it never reaches the line. -1 only without headings.
 */
export function activeHeadingIndex(tops: readonly number[], line: number, atEnd = false): number {
  if (!tops.length) return -1;
  if (atEnd) return tops.length - 1;
  let active = 0;
  tops.forEach((top, index) => { if (top <= line) active = index; });
  return active;
}

/** "4 sekce", "1 sekce", "5 sekcí". */
export function sectionCount(count: number) {
  return shellMore.outline.sections(count);
}

/** Below the sticky top bar, where a heading scrolled to with scroll-margin-top lands. */
function readingLine() {
  const bar = document.querySelector(".workspace-bar");
  return (bar ? bar.getBoundingClientRect().bottom : 0) + 32;
}

/** Scrolls to a heading and moves there: the writer's caret into the heading, else focus for keyboard and screen reader users. */
export function goToHeading(heading: PageOutlineHeading) {
  const element = heading.element;
  if (!element.isConnected) return;
  unfoldTo(element);
  element.scrollIntoView?.({ block: "start", behavior: scrollBehavior(element) });
  // A heading inside the live editor is text, not a focus target: the caret goes to its start (tiptap keeps the editor on its root).
  const editor = (element.closest(".ProseMirror") as (Element & { editor?: Editor }) | null)?.editor;
  if (editor?.isEditable && !editor.isDestroyed) {
    editor.commands.setTextSelection(editor.view.posAtDOM(element, 0));
    editor.view.focus();
    return;
  }
  if (element.isContentEditable || element.closest("[contenteditable=true]")) return;
  if (!element.hasAttribute("tabindex")) {
    element.setAttribute("tabindex", "-1");
    element.addEventListener("blur", () => element.removeAttribute("tabindex"), { once: true });
  }
  element.focus({ preventScroll: true });
}

/** Typing changes the document many times a second: what it did to the outline is read this long after the last change... */
const SETTLE_QUIET_MS = 100;
/** ...and at the latest this long after the first change of a burst. */
const SETTLE_MAX_MS = 300;
const HEADING_SELECTOR = "h1, h2, h3, h4, h5, h6";
/**
 * A heading that came or went is read once the browser is idle (at the latest SETTLE_MAX_MS later), after the frame that drew the change:
 * read in that frame, the outline cost 30–40 ms at 4× CPU of a block drop or an Enter on a long page, its layout reads included.
 */
const idle = typeof requestIdleCallback === "function"
  ? { request: (run: () => void) => requestIdleCallback(run, { timeout: SETTLE_MAX_MS }), cancel: (id: number) => cancelIdleCallback(id) }
  : { request: (run: () => void) => setTimeout(run, 1) as unknown as number, cancel: (id: number) => clearTimeout(id) };

/** A heading was added or removed, on its own or inside a node that came or went. */
function changesHeadings(record: MutationRecord) {
  if (record.type !== "childList") return false;
  for (const nodes of [record.addedNodes, record.removedNodes])
    for (const node of nodes) if (node instanceof Element && (node.matches(HEADING_SELECTOR) || node.querySelector(HEADING_SELECTOR))) return true;
  return false;
}
/** The change is inside a heading, so its title may differ. */
function insideHeading(record: MutationRecord) {
  const target = record.target;
  return !!(target instanceof Element ? target : target.parentElement)?.closest(HEADING_SELECTOR);
}

/**
 * Live outline of the document inside `root` (the page's document section, which stays while the editor inside it is
 * replaced). A heading that appears or goes is collected once the browser is idle. Any other change is typing: inside a heading it
 * can rename it, outside them it moves them only when the document changes its size; either is read once the document has
 * been quiet for SETTLE_QUIET_MS (at the latest SETTLE_MAX_MS after it began), and other typing reads nothing. Scrolling,
 * resizing and `refresh` read at once, titles still waiting included; the active heading follows scrolling.
 */
export function usePageOutline(root: Ref<HTMLElement | null | undefined>) {
  const headings = shallowRef<PageOutlineHeading[]>([]);
  const active = ref(-1);
  let observer: MutationObserver | undefined;
  let sizeObserver: ResizeObserver | undefined;
  let collectIdle = 0, scrollFrame = 0, scrollTimer: ReturnType<typeof setTimeout> | undefined;
  let settleQuiet: ReturnType<typeof setTimeout> | undefined, settleMax: ReturnType<typeof setTimeout> | undefined;
  /** A heading's text changed since the last collect. */
  let stale = false;
  /**
   * The heading the reader chose in the outline. It stays the active one while the page travels there and after it:
   * one of the last headings cannot reach the reading line, and the end of the page would name the last heading instead.
   */
  let chosen = -1;
  /** The page has arrived at the chosen heading: the next scroll of the page, whatever causes it, lets the position decide again. */
  let arrived = false;
  let arrival: ReturnType<typeof setTimeout> | undefined;

  function same(a: PageOutlineHeading[], b: PageOutlineHeading[]) {
    return a.length === b.length && a.every((heading, index) => heading.element === b[index]!.element && heading.title === b[index]!.title && heading.level === b[index]!.level);
  }
  /**
   * `measure: false` (a renamed heading): the same headings stay where they were, so nothing is measured. `follow: false` (a new root):
   * measured after the frame that shows them (scheduleTrack).
   */
  function collect(measure = true, follow = true) {
    if (collectIdle) idle.cancel(collectIdle);
    collectIdle = 0;
    clearSettle();
    stale = false;
    const count = headings.value.length;
    const next = collectHeadings(root.value);
    if (!same(next, headings.value)) { headings.value = next; chosen = -1; }
    if (follow && (measure || next.length !== count)) track();
  }
  function scheduleCollect() { if (!collectIdle) collectIdle = idle.request(() => collect()); }
  function clearSettle() {
    clearTimeout(settleQuiet);
    clearTimeout(settleMax);
    settleMax = undefined;
  }
  function settle() {
    clearSettle();
    if (stale) collect(false);
  }
  function scheduleSettle() {
    clearTimeout(settleQuiet);
    settleQuiet = setTimeout(settle, SETTLE_QUIET_MS);
    settleMax ??= setTimeout(settle, SETTLE_MAX_MS);
  }
  function changed(all: MutationRecord[]) {
    // The editor building hidden changes a lot and moves nothing the reader sees: following it cost a layout a frame (perf 3.5).
    const records = all.filter(record => !building(record.target));
    if (!records.length) return;
    if (records.some(changesHeadings)) return scheduleCollect();
    // Text typed outside the headings moves none of them unless the document changes its size (the ResizeObserver): J12.6, no layout read per keystroke.
    if (records.some(insideHeading)) { stale = true; scheduleSettle(); }
  }
  function track() {
    if (scrollFrame) cancelAnimationFrame(scrollFrame);
    clearTimeout(scrollTimer);
    scrollFrame = 0;
    if (stale) return collect();
    const list = headings.value;
    if (!list.length) { active.value = -1; return; }
    if (chosen >= 0 && chosen < list.length) { active.value = chosen; return; }
    const scroller = document.scrollingElement;
    const atEnd = !!scroller && scroller.scrollHeight > scroller.clientHeight + 2 && scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 2;
    active.value = activeHeadingIndex(list.map(heading => heading.element.getBoundingClientRect().top), readingLine(), atEnd);
  }
  // After the frame has painted: its layout is done, so the headings' boxes are read without forcing one (a drop's scroll read them dirty, 3.6 perf).
  function scheduleTrack() { if (!scrollFrame) scrollFrame = requestAnimationFrame(() => { scrollTimer = setTimeout(track); }); }
  function travelling() { arrived = false; clearTimeout(arrival); arrival = setTimeout(() => { arrived = true; }, SCROLL_SETTLED_MS); }
  function choose(index: number) { chosen = index; active.value = index; travelling(); }
  /** The reader scrolls by themselves again (wheel, touch, keys, the scrollbar): the position decides. */
  function release(event: Event) {
    if (chosen < 0 || (event instanceof KeyboardEvent && !SCROLL_KEYS.has(event.key))) return;
    chosen = -1;
    scheduleTrack();
  }
  function scrolled(event: Event) {
    if (chosen >= 0 && (event.target === document || event.target === document.scrollingElement)) {
      if (arrived) chosen = -1; else travelling();
    }
    scheduleTrack();
  }

  watch(root, element => {
    observer?.disconnect();
    sizeObserver?.disconnect();
    if (!element) { headings.value = []; active.value = -1; return; }
    // Measured once the frame that shows them has painted, not here: a layout forced at open cost 769 ms (150 requirements, 4× CPU).
    collect(true, false);
    scheduleTrack();
    observer = new MutationObserver(changed);
    observer.observe(element, { childList: true, subtree: true, characterData: true });
    // Blocks outside the viewport have an estimated height (content-visibility); when they render the page grows
    // without a scroll event, and "the end of the page" is no longer where it was. Typing changes the height too. Measured in the
    // observer's callback, which runs right after layout: the reads find it fresh and force none (a timer or the next frame
    // found it dirty and forced one per settle or key, J12.6).
    if (typeof ResizeObserver !== "undefined") { sizeObserver = new ResizeObserver(() => track()); sizeObserver.observe(element); }
  }, { immediate: true, flush: "post" });
  document.addEventListener("scroll", scrolled, { capture: true, passive: true });
  window.addEventListener("resize", scheduleTrack);
  for (const type of RELEASE_EVENTS) window.addEventListener(type, release, { capture: true, passive: true });
  onScopeDispose(() => {
    observer?.disconnect();
    sizeObserver?.disconnect();
    clearSettle();
    clearTimeout(arrival);
    if (collectIdle) idle.cancel(collectIdle);
    if (scrollFrame) cancelAnimationFrame(scrollFrame);
    clearTimeout(scrollTimer);
    document.removeEventListener("scroll", scrolled, { capture: true });
    window.removeEventListener("resize", scheduleTrack);
    for (const type of RELEASE_EVENTS) window.removeEventListener(type, release, { capture: true });
  });
  return { headings, active, refresh: collect, choose };
}
const RELEASE_EVENTS = ["wheel", "touchmove", "keydown", "mousedown"] as const;
const SCROLL_KEYS = new Set(["ArrowUp", "ArrowDown", "PageUp", "PageDown", "Home", "End", " "]);
/** A smooth scroll reports every frame; this long without a report means the page has stopped. */
const SCROLL_SETTLED_MS = 150;
