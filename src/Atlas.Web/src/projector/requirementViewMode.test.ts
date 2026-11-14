import { afterEach, describe, expect, it, vi } from "vitest";
import { requirementDefaults, type RequirementAttributes } from "./requirementTypes";
import { Schema, type Node as PMNode } from "@tiptap/pm/model";
import { EditorState, TextSelection } from "@tiptap/pm/state";
import { EditorView, type DecorationSet } from "@tiptap/pm/view";
import { isFolded, resetFoldStateForTests } from "../editor/foldState";
import { compareRequirements, defaultView, keepRowInPlace, playRequirementLevel, requirementIds, requirementLevels, requirementViewPlugin, setViewPrefs, viewPrefs } from "./requirementViewMode";
import { overviewLevel, setOverviewLevel } from "../overview/overviewKinds";
import "./requirementsPlugin";

const attrs = (change: Partial<RequirementAttributes>): RequirementAttributes => ({ ...requirementDefaults, ...change });

afterEach(() => localStorage.clear());

// The page's shape: a requirement is a heading with a state, its section the blocks after it.
const schema = new Schema({ nodes: {
  doc: { content: "block+" }, text: { group: "inline" },
  paragraph: { group: "block", content: "inline*", attrs: { dependencies: { default: false } }, toDOM: () => ["p", 0] },
  heading: { group: "block", content: "inline*", attrs: { level: { default: 1 }, headingId: { default: null }, state: { default: null }, party: { default: "" } }, toDOM: () => ["h2", 0] },
} });
const text = (value: string) => [schema.text(value)];
const para = (value: string) => schema.node("paragraph", null, text(value));
const head = (level: number, id: string, state: string | null = null, party = "") => schema.node("heading", { level, headingId: id, state, party }, text(id.toUpperCase()));

describe("the view over requirement headings, in the document", () => {
  // Úvod · MPI(H2): A blocked, B done · Server(H2): C done · Odkazy(H2) text only
  const page = () => schema.node("doc", null, [para("Úvod"), head(2, "mpi"), para("O MPI"), head(3, "a", "blocked"), para("pozn. A"), head(3, "b", "done"), para("pozn. B"),
    head(2, "server"), head(3, "c", "done"), para("pozn. C"), head(2, "odkazy"), para("Odkaz")]);
  const open = (doc: PMNode, page = "page-v") => EditorState.create({ doc, plugins: [requirementViewPlugin(() => page)] });
  const layout = (state: EditorState) => (state.plugins[0]!.getState(state) as { layout: DecorationSet }).layout;
  /** The look of each top-level block: "" shown, "out" hidden, "pin" pinned outside the filter. */
  const looks = (state: EditorState) => {
    const set = layout(state), found: string[] = [];
    state.doc.forEach((_node, pos) => {
      const look = set.find(pos + 1, pos + 1).map(decoration => (decoration as unknown as { type: { attrs: { class?: string } } }).type.attrs.class)[0];
      found.push(look === "is-filtered-out" ? "out" : look === "is-pinned" ? "pin" : "");
    });
    return found.join(",");
  };
  const posOf = (state: EditorState, index: number) => { let pos = 0; for (let i = 0; i < index; i++) pos += state.doc.child(i).nodeSize; return pos; };
  const filter = (state: EditorState, value: string, parties: string[] = []) => state.apply(state.tr.setMeta(state.plugins[0]!, { sort: "", state: value, parties }));

  afterEach(() => setOverviewLevel("requirements", "page-v", "m"));

  it("hides every block of a requirement the filter leaves out, and a plain heading whose requirements are all hidden, with its text", () => {
    const state = filter(open(page()), "blocked");
    // Úvod, MPI, O MPI, A, pozn. A, B, pozn. B, Server, C, pozn. C, Odkazy, Odkaz
    expect(looks(state)).toBe(",,,,,out,out,out,out,out,,");
    expect(looks(filter(state, ""))).toBe(",,,,,,,,,,,");
    expect(requirementIds(state.doc)).toEqual(["a", "b", "c"]);
  });

  it("keeps only the requirements of the parties chosen („Kdo“, none named as \"\"), and with a state only those of both", () => {
    const parties = () => schema.node("doc", null, [head(2, "mpi"), head(3, "a", "blocked", "Modelsoft"), para("pozn. A"), head(3, "b", "done", " Marek Musil "),
      head(3, "c", "blocked"), head(3, "d", "blocked", "IT KBN")]);
    // MPI, A, pozn. A, B, C, D
    expect(looks(filter(open(parties()), "", ["Modelsoft", "Marek Musil"]))).toBe(",,,,out,out");
    expect(looks(filter(open(parties()), "", [""]))).toBe(",out,out,out,,out");
    expect(looks(filter(open(parties()), "blocked", ["Modelsoft", "Marek Musil"]))).toBe(",,,out,out,out");
    expect(looks(filter(open(parties()), "", ["Nikdo"]))).toBe("out,out,out,out,out,out");
  });

  it("hides every requirement on NONE and keeps every plain heading with its text (model §2)", () => {
    setOverviewLevel("requirements", "page-v", "none");
    expect(looks(open(page()))).toBe(",,,out,out,out,out,,out,out,,");
  });

  it("lets a pinned section go once the caret leaves it, also without any change (the pin kept it shown)", () => {
    let state = open(page());
    state = state.apply(state.tr.setSelection(TextSelection.create(state.doc, posOf(state, 6) + 2)));
    state = filter(state, "blocked");
    expect(looks(state)).toBe(",,,,,pin,pin,out,out,out,,");
    state = state.apply(state.tr.setSelection(TextSelection.create(state.doc, 1)));
    expect((state.plugins[0]!.getState(state) as { stale: string }).stale).toBe("local");
    state = state.apply(state.tr.setMeta("requirementRelayout", true));
    expect(looks(state)).toBe(",,,,,out,out,out,out,out,,");
  });

  it("keeps the section with the caret where it is while it is worked in (pinned), and lays out once the caret has left", () => {
    let state = filter(open(page()), "blocked");
    state = state.apply(state.tr.setSelection(TextSelection.create(state.doc, posOf(state, 4) + 2)));
    // A done in its own section: it stays, marked as outside the filter; typing there only maps the layout.
    state = state.apply(state.tr.setNodeAttribute(posOf(state, 3), "state", "done"));
    state = state.apply(state.tr.setMeta("requirementRelayout", true));
    expect(looks(state)).toBe(",,,pin,pin,out,out,out,out,out,,");
    const typed = state.apply(state.tr.insertText("x", posOf(state, 4) + 2));
    expect(looks(typed)).toBe(looks(state));
    // The caret goes to Úvod: the section is stale, and its relayout hides it with its now empty group.
    state = typed.apply(typed.tr.setSelection(TextSelection.create(typed.doc, 1)));
    expect((state.plugins[0]!.getState(state) as { stale: string }).stale).toBe("local");
    state = state.apply(state.tr.setMeta("requirementRelayout", true));
    expect(looks(state)).toBe(",out,out,out,out,out,out,out,out,out,,");
  });

  it("is a view, not a fold: a filtered-out requirement leaves a matching one under it shown", () => {
    // Pole(H2, done): Q(H3, blocked) · Ven(H2, blocked)
    const nested = schema.node("doc", null, [head(2, "pole", "done"), para("pozn."), head(3, "q", "blocked"), para("pozn. Q"), head(2, "ven", "blocked"), para("pozn. V")]);
    expect(looks(filter(open(nested), "blocked"))).toBe("out,out,,,,");
  });

  it("keeps a plain heading and its text under a requirement on NONE (model §2)", () => {
    setOverviewLevel("requirements", "page-v", "none");
    const nested = schema.node("doc", null, [head(2, "r", "in_progress"), para("pozn."), head(3, "t"), para("Text pod nadpisem.")]);
    expect(looks(open(nested))).toBe("out,out,,");
  });

  it("shows a hidden section at once when the caret lands in it (a jump from the Přehled, Find), pinned", () => {
    let state = filter(open(page()), "blocked");
    state = state.apply(state.tr.setSelection(TextSelection.create(state.doc, posOf(state, 5) + 1)));
    expect(looks(state)).toBe(",,,,,pin,pin,out,out,out,,");
  });
});

describe("the reader's view of the requirements", () => {
  it("puts what is empty last and reads Czech names and numbers as people do", () => {
    const sorted = (sort: Parameters<typeof compareRequirements>[0], list: Partial<RequirementAttributes>[]) =>
      list.map(attrs).sort((a, b) => compareRequirements(sort, a, b)).map(item => item[sort as "date"] || "–");
    expect(sorted("date", [{ date: "" }, { date: "2026-11-02" }, { date: "2026-10-01" }])).toEqual(["2026-10-01", "2026-11-02", "–"]);
    expect(sorted("party", [{ party: "" }, { party: "Šimon" }, { party: "Adam" }, { party: "Čeněk" }])).toEqual(["Adam", "Čeněk", "Šimon", "–"]);
    expect(sorted("key", [{ key: "app.10" }, { key: "app.9" }])).toEqual(["app.9", "app.10"]);
  });

  it("keeps each page's view in this browser and forgets the default", () => {
    setViewPrefs("page-a", { state: "blocked" });
    expect(viewPrefs("page-a").value).toEqual({ sort: "", state: "blocked", parties: [] });
    expect(JSON.parse(localStorage.getItem("atlas.requirementView.page-a")!)).toMatchObject({ state: "blocked" });
    expect(viewPrefs("page-b").value).toEqual(defaultView);
    setViewPrefs("page-a", { state: "" });
    expect(localStorage.getItem("atlas.requirementView.page-a")).toBeNull();
    // An older or tampered entry: only the parties that are text are read.
    localStorage.setItem("atlas.requirementView.page-c", JSON.stringify({ state: "nope", parties: ["Modelsoft", 7, ""] }));
    expect(viewPrefs("page-c").value).toEqual({ sort: "", state: "", parties: ["Modelsoft", ""] });
  });

  it("keeps the detail level NONE – S – M as the requirements overview's level, an older choice read on as its 3.6 level (overviewKinds.ts)", () => {
    expect(requirementLevels.map(level => level.id)).toEqual(["none", "s", "m"]);
    expect(overviewLevel("requirements", "page-new").value).toBe("m");
    localStorage.setItem("atlas.requirementView.page-c", JSON.stringify({ density: "compact", sort: "", state: "" }));
    expect(overviewLevel("requirements", "page-c").value).toBe("m");
    expect(JSON.parse(localStorage.getItem("atlas.requirementView.page-c")!)).toEqual({ sort: "", state: "" });
    // The 3.4 entry: its S was the table alone, its XL the whole block; it moves to the 3.5 entry.
    localStorage.setItem("atlas.overview.requirements.page-d", "s");
    expect(overviewLevel("requirements", "page-d").value).toBe("none");
    expect(localStorage.getItem("atlas.overview.requirements.page-d")).toBeNull();
    expect(localStorage.getItem("atlas.overview.requirements.v2.page-d")).toBe("none");
    localStorage.setItem("atlas.overview.requirements.page-e", "xl");
    expect(overviewLevel("requirements", "page-e").value).toBe("m");
    localStorage.setItem("atlas.overview.requirements.v2.page-x", "xl");
    expect(overviewLevel("requirements", "page-x").value).toBe("m");
    localStorage.setItem("atlas.overview.requirements.page-f", "full");
    expect(overviewLevel("requirements", "page-f").value).toBe("m");
    localStorage.setItem("atlas.overview.requirements.page-g", "row");
    expect(overviewLevel("requirements", "page-g").value).toBe("m");
    setOverviewLevel("requirements", "page-c", "s");
    expect(localStorage.getItem("atlas.overview.requirements.v2.page-c")).toBe("s");
    setOverviewLevel("requirements", "page-c", "m");
    expect(localStorage.getItem("atlas.overview.requirements.v2.page-c")).toBeNull();
  });

  it("folds every requirement at S, also the one with the caret (no deferred fold; the heading fold's caret rules take it from there)", () => {
    const doc = schema.node("doc", null, [para("Úvod"), head(2, "mpi"), head(3, "a", "blocked"), para("pozn. A"), head(3, "b", "done"), para("pozn. B")]);
    const host = document.body.appendChild(document.createElement("div"));
    const view = new EditorView(host, { state: EditorState.create({ doc, plugins: [requirementViewPlugin(() => "page-s")] }) });
    view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, 13)));
    resetFoldStateForTests();
    setOverviewLevel("requirements", "page-s", "s");
    playRequirementLevel(view.dom, () => undefined);
    expect(["a", "b"].map(id => isFolded(`page-s#${id}`))).toEqual([true, true]);
    setOverviewLevel("requirements", "page-s", "m");
    playRequirementLevel(view.dom, () => undefined);
    expect(["a", "b"].map(id => isFolded(`page-s#${id}`))).toEqual([false, false]);
    view.destroy(); host.remove(); setOverviewLevel("requirements", "page-s", "m"); resetFoldStateForTests();
  });

  it("keeps the first row in view where it was across a switch of level, scrolling by what it moved (3.5.1 §1.4)", () => {
    const root = Object.assign(document.createElement("div"), { className: "ProseMirror" }), rows = [0, 1, 2].map(index => Object.assign(document.createElement("div"), { id: `r${index}` }));
    rows.forEach(row => root.append(row));
    document.body.append(root);
    // Above the view, the top row at 40 px; the switch grows the rows above it by 60 px.
    let grown = 0;
    const tops = [-30, 40, 120];
    rows.forEach((row, index) => vi.spyOn(row, "getBoundingClientRect").mockImplementation(() => new DOMRect(0, tops[index]! + (index ? grown : 0), 100, 28)));
    const scrollBy = vi.fn(), scroller = document.scrollingElement ?? document.documentElement, own = scroller.scrollBy;
    scroller.scrollBy = scrollBy;
    keepRowInPlace(root, () => { grown = 60; });
    expect(scrollBy).toHaveBeenCalledWith(0, 60);
    // A row under the page's sticky bar is not the one seen: the first row seen is held, pushed by the growth above it (RC: S→M +22 px).
    scrollBy.mockClear();
    grown = 0;
    const own2 = document.elementFromPoint;
    document.elementFromPoint = (_x: number, y: number) => (y < 64 ? document.body : rows[2]!);
    keepRowInPlace(root, () => { grown = 22; });
    expect(scrollBy).toHaveBeenCalledWith(0, 22);
    document.elementFromPoint = own2;
    scroller.scrollBy = own;
    root.remove();
  });
});
