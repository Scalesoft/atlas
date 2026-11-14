import { Editor } from "@tiptap/core";
import StarterKit from "@tiptap/starter-kit";
import { afterEach, describe, expect, it, vi } from "vitest";
import { effectScope, shallowRef } from "vue";
import { activeHeadingIndex, collectHeadings, goToHeading, sectionCount, usePageOutline } from "./pageOutline";

function documentWith(html: string) {
  const root = document.createElement("section");
  root.innerHTML = `<div class="ProseMirror">${html}</div>`;
  return root;
}

describe("page outline", () => {
  it("never takes the style of an inline component for a heading title", () => {
    const root = documentWith(`<h2>Správce <span data-md-module="contact"><style>.md-contact{color:red}</style>Jan</span></h2>`);
    expect(collectHeadings(root).map(heading => heading.title)).toEqual(["Správce Jan"]);
  });
  it("reads the words inside marks and chips in place, as the heading shows them", () => {
    const root = documentWith(`<h2>Plán <strong>na <em>rok</em></strong> <a class="md-chip" contenteditable="false"><span class="ProseMirror-widget">×</span>PX2-1 Přihlášení</a></h2>`);
    expect(collectHeadings(root).map(heading => heading.title)).toEqual(["Plán na rok PX2-1 Přihlášení"]);
  });
  it("keeps the editor's widgets out of a title: a requirement's chips and checkbox, a collaborator's caret with its name", () => {
    const root = documentWith(`<h3 data-heading-id="rq"><input class="rq-check ProseMirror-widget" type="checkbox"><span class="rq-chips ProseMirror-widget">Hotovo Dodavatel</span>Přihlá<span class="collaboration-carets__caret ProseMirror-widget"><div class="collaboration-carets__label">Petra Malá</div></span>šení</h3>`);
    expect(collectHeadings(root).map(heading => heading.title)).toEqual(["Přihlášení"]);
  });
  it("reads h1–h6 of the document relative to its top level and skips embedded components", () => {
    const root = documentWith(`
      <h2 data-heading-id="intro">Úvod</h2><p>Text</p>
      <h3>Instalace   krok za krokem</h3>
      <div data-md-module="toc"><h2>Obsah</h2></div>
      <h5>Podrobnost</h5><h6>Poznámka</h6>
      <h2 data-heading-id="faq"></h2>`);
    expect(collectHeadings(root).map(({ key, level, title, headingId }) => ({ key, level, title, headingId }))).toEqual([
      { key: "intro", level: 0, title: "Úvod", headingId: "intro" },
      { key: "h1", level: 1, title: "Instalace krok za krokem", headingId: undefined },
      { key: "h2", level: 3, title: "Podrobnost", headingId: undefined },
      { key: "h3", level: 4, title: "Poznámka", headingId: undefined },
      { key: "faq", level: 0, title: "Nadpis bez textu", headingId: "faq" },
    ]);
    expect(collectHeadings(null)).toEqual([]);
    expect(collectHeadings(document.createElement("div"))).toEqual([]);
  });

  it("marks the heading the reader is in, and the last one at the end of the page", () => {
    expect(activeHeadingIndex([], 100)).toBe(-1);
    expect(activeHeadingIndex([140, 600, 1200], 100)).toBe(0);
    expect(activeHeadingIndex([40, 600, 1200], 100)).toBe(0);
    expect(activeHeadingIndex([-900, -300, 90], 100)).toBe(2);
    expect(activeHeadingIndex([-900, 300, 700], 100, true)).toBe(2);
  });

  it("counts sections in Czech", () => {
    expect([1, 2, 4, 5, 12].map(sectionCount)).toEqual(["1 sekce", "2 sekce", "4 sekce", "5 sekcí", "12 sekcí"]);
  });

  it("puts the writer's caret into the heading an entry leads to, and only focuses it for a reader", () => {
    Element.prototype.scrollIntoView ??= () => {};
    const editor = new Editor({ element: document.body.appendChild(document.createElement("div")), extensions: [StarterKit], content: "<p>Úvod</p><h2>Druhá</h2><p>Text</p>" });
    goToHeading(collectHeadings(document.body)[0]!);
    expect(editor.state.selection.$head.parent.textContent).toBe("Druhá");
    expect(editor.state.selection.$head.parentOffset).toBe(0);
    editor.commands.setTextSelection(2);
    editor.setEditable(false);
    const heading = collectHeadings(document.body)[0]!;
    goToHeading(heading);
    expect(editor.state.selection.head).toBe(2);
    expect(document.activeElement).toBe(heading.element);
    editor.destroy();
  });
});

let scope: ReturnType<typeof effectScope> | undefined;
afterEach(() => { scope?.stop(); scope = undefined; vi.useRealTimers(); document.body.innerHTML = ""; });

/** The live outline of a document with three headings. `queries` counts the passes that read the headings, `measured` those that measure them. */
function liveOutline() {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "requestAnimationFrame", "cancelAnimationFrame"] });
  const root = documentWith(`<h2>Úvod</h2><p>Dlouhý odstavec</p><h2>Druhá</h2><p>Další odstavec</p><h2>Třetí</h2>`);
  document.body.append(root);
  const queries = vi.spyOn(root, "querySelectorAll");
  const measured = vi.spyOn(root.querySelector("h2")!, "getBoundingClientRect");
  scope = effectScope();
  const outline = scope.run(() => usePageOutline(shallowRef(root)))!;
  // The first pass runs when the outline is created, its measure once that frame has painted.
  vi.advanceTimersByTime(20);
  queries.mockClear();
  measured.mockClear();
  /** Types `keys` characters into the first element matching `selector`, one keystroke per frame (60 Hz). */
  const type = async (selector: string, keys: number) => {
    const text = root.querySelector(selector)!.firstChild as Text;
    for (let key = 0; key < keys; key++) { text.data += "x"; await vi.advanceTimersByTimeAsync(16); }
  };
  return { root, outline, queries, measured, type };
}

describe("usePageOutline while the document changes", () => {
  // Typing outside the headings cannot change the list; it moves the headings below only by changing the document's size (J12.6).
  it("neither reads nor measures the headings while text is typed outside them, also with pauses between keys", async () => {
    const { queries, measured, type } = liveOutline();
    for (let pause = 0; pause < 5; pause++) { await type("p", 4); await vi.advanceTimersByTimeAsync(500); }
    expect(queries).toHaveBeenCalledTimes(0);
    expect(measured).toHaveBeenCalledTimes(0);
  });

  // The observer reports right after layout: measuring there finds it fresh (J12.6), a timer or the next frame found it dirty.
  it("measures the headings only in the size observer's callback when the document changed its size", async () => {
    let resize: () => void = () => {};
    vi.stubGlobal("ResizeObserver", class { constructor(callback: () => void) { resize = callback; } observe() {} disconnect() {} });
    try {
      const { queries, measured, type } = liveOutline();
      await vi.advanceTimersByTimeAsync(500);
      measured.mockClear();
      await type("p", 10);
      expect(measured).toHaveBeenCalledTimes(0);
      resize();
      expect(measured).toHaveBeenCalledTimes(1);
      await vi.advanceTimersByTimeAsync(500);
      expect(queries).toHaveBeenCalledTimes(0);
      expect(measured).toHaveBeenCalledTimes(1);
    } finally { vi.unstubAllGlobals(); }
  });

  it("does not measure the headings when only a heading's text changed", async () => {
    const { measured, type } = liveOutline();
    await type("h2", 10);
    await vi.advanceTimersByTimeAsync(500);
    expect(measured).toHaveBeenCalledTimes(0);
  });

  // A rename shows a moment later, never later than a pause of typing plus the settle delay.
  it("reads a heading that is renamed while typing a few times, and is exact as soon as typing pauses", async () => {
    const { outline, queries, type } = liveOutline();
    await type("h2", 20);
    expect(queries.mock.calls.length).toBeLessThanOrEqual(3);
    await vi.advanceTimersByTimeAsync(100);
    expect(outline.headings.value[0]!.title).toBe(`Úvod${"x".repeat(20)}`);
  });

  // A heading that appears or goes is one discrete change, not part of a burst: it shows once the browser is idle (not in the frame that drew it).
  it("picks up a heading that is added or removed once the browser is idle", async () => {
    const { root, outline } = liveOutline();
    root.querySelector(".ProseMirror")!.insertAdjacentHTML("beforeend", "<h2>Čtvrtá</h2>");
    await vi.advanceTimersByTimeAsync(16);
    expect(outline.headings.value.map((heading) => heading.title)).toEqual(["Úvod", "Druhá", "Třetí", "Čtvrtá"]);
    root.querySelector("h2")!.remove();
    await vi.advanceTimersByTimeAsync(16);
    expect(outline.headings.value.map((heading) => heading.title)).toEqual(["Druhá", "Třetí", "Čtvrtá"]);
  });

  // Upravit builds the live editor hidden under the rendered page (DocumentEditor `editor-hydrating`); following it re-read and measured the
  // headings a frame at a time while it built (perf 3.5). It counts once it shows.
  it("ignores the editor building hidden under the page, and takes its headings once it shows", async () => {
    const { root, outline, queries, measured } = liveOutline();
    const building = document.createElement("div");
    building.className = "editor-hydrating";
    root.append(building);
    await vi.advanceTimersByTimeAsync(500);
    queries.mockClear(); measured.mockClear();
    for (let block = 0; block < 20; block++) { building.insertAdjacentHTML("beforeend", `<div class="ProseMirror"><h2>Živý ${block}</h2><p>x</p></div>`); await vi.advanceTimersByTimeAsync(16); }
    await vi.advanceTimersByTimeAsync(500);
    expect(queries).not.toHaveBeenCalled();
    expect(measured).not.toHaveBeenCalled();
    expect(outline.headings.value.map((heading) => heading.title)).toEqual(["Úvod", "Druhá", "Třetí"]);
    // It shows: the rendered page goes, the editor stays.
    building.classList.remove("editor-hydrating");
    root.querySelector(".ProseMirror")!.remove();
    await vi.advanceTimersByTimeAsync(16);
    expect(outline.headings.value).toHaveLength(20);
  });

  // Scrolling reads the headings to mark the active one, so it must not read a rename that is still waiting.
  it("reads a renamed heading before measuring it for a scroll that comes first", async () => {
    const { root, outline } = liveOutline();
    (root.querySelector("h2")!.firstChild as Text).data = "Nový úvod";
    document.dispatchEvent(new Event("scroll"));
    // The scroll is measured once its frame has painted.
    await vi.advanceTimersByTimeAsync(20);
    expect(outline.headings.value[0]!.title).toBe("Nový úvod");
  });

  // The page closes with a rename still waiting: nothing of a disposed outline may read the document that is gone.
  it("stops reading when its scope is disposed with a change still waiting", async () => {
    const { root, queries, measured } = liveOutline();
    (root.querySelector("h2")!.firstChild as Text).data += "x";
    // The observer has seen the change and armed the delay.
    await vi.advanceTimersByTimeAsync(1);
    scope!.stop();
    await vi.advanceTimersByTimeAsync(1000);
    expect(queries).toHaveBeenCalledTimes(0);
    expect(measured).toHaveBeenCalledTimes(0);
  });

  // A scroll queues a measuring frame; when the outline is read at once before it runs, that frame is dropped instead of
  // measuring a second time, and a disposal that follows cannot miss it.
  it("drops the frame a scroll queued once the outline has been read at once", async () => {
    const { outline, measured } = liveOutline();
    document.dispatchEvent(new Event("scroll"));
    outline.refresh();
    expect(measured).toHaveBeenCalledTimes(1);
    scope!.stop();
    await vi.advanceTimersByTimeAsync(100);
    expect(measured).toHaveBeenCalledTimes(1);
  });
});
