import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { toast } from "vue-sonner";
import { ApiError } from "../../api";
import type { UnlinkedApi, UnlinkedMention } from "../../linking/unlinkedApi";
import UnlinkedMentions from "./UnlinkedMentions.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.restoreAllMocks(); });

const mention = (pageId: string, ordinal: number, text: string, extra: Partial<UnlinkedMention> = {}): UnlinkedMention => ({
  pageId, pageTitle: pageId === "p1" ? "Projekt Alfa" : "Porady", version: 3, ordinal, text, anchor: null, context: `Za klienta mluví ${text} a čekáme na schválení.`,
  kind: "person", state: "suggested", candidates: [{ id: `c-${text}`, label: text, hint: "Modelsoft" }, { id: `d-${text}`, label: `${text} ml.`, hint: null }], ...extra,
});

function show(pages: { items: UnlinkedMention[]; nextCursor: string | null }[], resolve: UnlinkedApi["resolve"] = vi.fn(async () => ({ version: 4 }))) {
  const api = { list: vi.fn(async (_space: string, query?: { cursor?: string | null; pageId?: string }) => pages[query?.pageId ? pages.length - 1 : query?.cursor ? 1 : 0]!), resolve };
  wrapper = mount(UnlinkedMentions, { attachTo: document.body, props: { spaceId: "s", api } });
  return api;
}
const rows = () => wrapper!.findAll("[data-unlinked-row]");

it("groups the mentions by page, each in its sentence with the best candidate first", async () => {
  show([{ items: [mention("p1", 0, "Jana Nováková"), mention("p1", 1, "Petr Malý"), mention("p2", 0, "PX2-17", { kind: "issue", candidates: [{ id: "PX2-17", label: "PX2-17 Čtečka" }] })], nextCursor: null }]);
  await flushPromises();
  expect(wrapper!.findAll("[data-unlinked-page]").map(page => page.text())).toEqual(["Projekt Alfa", "Porady"]);
  expect(rows()[0]!.get("mark").text()).toBe("Jana Nováková");
  expect(rows()[0]!.get(".unlinked-candidates").text()).toBe("nejspíš Jana Nováková (Modelsoft) · nebo Jana Nováková ml.");
  expect(rows()[2]!.find("[data-unlinked-dismiss]").exists()).toBe(false);
  expect(rows()[2]!.find("[data-unlinked-other]").exists()).toBe(false);
  expect(wrapper!.emitted("counted")?.at(-1)).toEqual([3, false]);
});

it("Propojit links the first candidate, the row goes at once and the keyboard is on the next row", async () => {
  const success = vi.spyOn(toast, "success");
  const api = show([{ items: [mention("p1", 0, "Jana Nováková"), mention("p1", 1, "Petr Malý")], nextCursor: null }]);
  await flushPromises();
  await rows()[0]!.get("[data-unlinked-link]").trigger("click");
  expect(rows()).toHaveLength(1);
  expect(document.activeElement).toBe(rows()[0]!.get("[data-unlinked-link]").element);
  await flushPromises();
  expect(api.resolve).toHaveBeenCalledWith(expect.objectContaining({ text: "Jana Nováková", version: 3 }), { targetId: "c-Jana Nováková" });
  expect(success).toHaveBeenCalledWith("Propojeno: Jana Nováková → Jana Nováková.", expect.objectContaining({ action: undefined }));
  // The page's next row posts the version the link made.
  await rows()[0]!.get("[data-unlinked-link]").trigger("click");
  await flushPromises();
  expect(api.resolve).toHaveBeenLastCalledWith(expect.objectContaining({ text: "Petr Malý", version: 4 }), { targetId: "c-Petr Malý" });
  expect(wrapper!.text()).toContain("Všechno je propojené");
});

// Owner decision: only the page says what is linked. Plain text kept as text changes nothing, so nothing is sent or stored;
// an unresolved chip kept as text becomes its text in the page.
it("Nechat textem hides a plain-text mention in this view only and turns an unresolved chip into text on the server", async () => {
  const success = vi.spyOn(toast, "success");
  const api = show([{ items: [mention("p1", 0, "Jana Nováková"), mention("p1", 1, "Petr Malý", { state: "unresolved" })], nextCursor: null }]);
  await flushPromises();
  await rows()[0]!.get("[data-unlinked-text]").trigger("click");
  await flushPromises();
  expect(api.resolve).not.toHaveBeenCalled();
  expect(success).toHaveBeenLastCalledWith("Jana Nováková zůstane textem.", expect.anything());
  await rows()[0]!.get("[data-unlinked-text]").trigger("click");
  await flushPromises();
  expect(api.resolve).toHaveBeenCalledWith(expect.objectContaining({ text: "Petr Malý", state: "unresolved" }), "text");
  expect(wrapper!.text()).toContain("Všechno je propojené");
});

// In a browser the decided row stays in the DOM while it fades out: the keyboard went to its button and then to <body>.
it("the keyboard skips the row that is fading out", async () => {
  const api = { list: vi.fn(async () => ({ items: [mention("p1", 0, "Jana Nováková"), mention("p1", 1, "Petr Malý")], nextCursor: null })), resolve: vi.fn(async () => ({ version: 4 })) };
  wrapper = mount(UnlinkedMentions, { attachTo: document.body, props: { spaceId: "s", api }, global: { stubs: { "transition-group": false } } });
  await flushPromises();
  await rows()[0]!.get("[data-unlinked-link]").trigger("click");
  expect(document.activeElement?.closest("[data-unlinked-row]")?.getAttribute("data-unlinked-row")).toBe("p1:suggested:1:Petr Malý");
});

it("arrows move between rows; a row's „Co s tím jde dělat“ offers every candidate and Nechat textem", async () => {
  show([{ items: [mention("p1", 0, "Jana Nováková"), mention("p2", 0, "Petr Malý")], nextCursor: null }]);
  await flushPromises();
  const first = rows()[0]!.get<HTMLButtonElement>("[data-unlinked-link]").element;
  first.focus();
  first.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }));
  expect(document.activeElement).toBe(rows()[1]!.get("[data-unlinked-link]").element);
  rows()[1]!.element.dispatchEvent(new KeyboardEvent("keydown", { key: ".", ctrlKey: true, bubbles: true }));
  await flushPromises();
  expect([...document.querySelectorAll("[data-row-action]")].map(item => item.textContent?.trim()))
    .toEqual(["Propojit: Petr Malý · Modelsoft", "Propojit: Petr Malý ml.", "Nechat textem"]);
});

it("a 409 brings the page's rows back as the server has them now and says so", async () => {
  const warning = vi.spyOn(toast, "warning");
  const api = show([{ items: [mention("p1", 0, "Jana Nováková"), mention("p2", 0, "Petr Malý")], nextCursor: null },
    // A server that ignores the page filter answers other pages too: only p1's rows are taken.
    { items: [mention("p1", 0, "Jana Nováková", { version: 5 }), mention("p2", 0, "Petr Malý")], nextCursor: null }], vi.fn(async () => { throw new ApiError(409, "Konflikt"); }));
  await flushPromises();
  await rows()[0]!.get("[data-unlinked-link]").trigger("click");
  expect(rows()).toHaveLength(1);
  await flushPromises();
  expect(api.list).toHaveBeenLastCalledWith("s", { pageId: "p1" });
  expect(rows().map(row => row.get("mark").text())).toEqual(["Jana Nováková", "Petr Malý"]);
  expect(warning).toHaveBeenCalledWith(expect.stringContaining("Projekt Alfa se mezitím změnila"), expect.anything());
});

it("another failure puts the row back where it was", async () => {
  const error = vi.spyOn(toast, "error");
  show([{ items: [mention("p1", 0, "Jana Nováková"), mention("p1", 1, "Petr Malý")], nextCursor: null }], vi.fn(async () => { throw new ApiError(500, "Chyba serveru"); }));
  await flushPromises();
  await rows()[1]!.get("[data-unlinked-link]").trigger("click");
  await flushPromises();
  expect(rows().map(row => row.get("mark").text())).toEqual(["Jana Nováková", "Petr Malý"]);
  expect(error).toHaveBeenCalledWith("Chyba serveru", expect.anything());
});

it("Načíst další asks for the next cursor and adds its rows", async () => {
  const api = show([{ items: [mention("p1", 0, "Jana Nováková")], nextCursor: "c2" }, { items: [mention("p2", 0, "Petr Malý")], nextCursor: null }]);
  await flushPromises();
  expect(wrapper!.emitted("counted")?.at(-1)).toEqual([1, true]);
  await wrapper!.get("[data-unlinked-more]").trigger("click");
  await flushPromises();
  expect(api.list).toHaveBeenLastCalledWith("s", { cursor: "c2" });
  expect(rows()).toHaveLength(2);
  expect(wrapper!.find("[data-unlinked-more]").exists()).toBe(false);
});

it("shows a sized skeleton while the first answer is on its way, and the empty state with nothing to link", async () => {
  let answer!: (page: { items: UnlinkedMention[]; nextCursor: null }) => void;
  wrapper = mount(UnlinkedMentions, { attachTo: document.body, props: { spaceId: "s", api: { list: () => new Promise(done => { answer = done; }), resolve: vi.fn() } } });
  expect(wrapper.find(".loading-skeleton").exists()).toBe(true);
  answer({ items: [], nextCursor: null });
  await flushPromises();
  expect(wrapper.text()).toContain("Všechno je propojené");
  expect(wrapper.emitted("counted")?.at(-1)).toEqual([0, false]);
});
