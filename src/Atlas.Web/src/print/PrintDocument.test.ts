import { afterEach, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import PrintDocument from "./PrintDocument.vue";
import type { PrintSnapshot } from "./main";

/*
 * The PDF prints the page's one editor, read-only (UX review 3.6, F1): the Přehled counts the requirement headings, and each heading
 * keeps its checkbox, state and party. The static renderer printed „0 % · 0 z 0“ and plain headings.
 */
type Json = { type: string; attrs?: Record<string, unknown>; content?: Json[]; text?: string };
const text = (value: string): Json => ({ type: "text", text: value });
const heading = (headingId: string, title: string, attrs: Record<string, unknown>): Json => ({ type: "heading", attrs: { level: 3, headingId, ...attrs }, content: [text(title)] });
const document_ = { format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }, { id: "atlas-requirements", version: 4 }],
  document: { type: "doc", content: [
    { type: "overview", attrs: { id: "5d1b8a3e-2c47-4e9f-8a60-7b3c9d2e1f40", kind: "requirements" } },
    heading("smlouva", "Smlouva podepsána", { state: "done", direction: "inbound", party: "DODAVATEL" }),
    heading("predani", "Předání dokumentace", { state: "waiting_for_input", direction: "outbound", party: "Nemocnice" }),
    { type: "paragraph", content: [text("Kde uloženo: C:\\TMP\\Projekty\\dlouhá\\cesta.pdf")] },
  ] } };
const snapshot: PrintSnapshot = { sourceOrigin: "https://atlas.example", title: "Zakázka", space: "Prostor", version: 3, isPublished: true,
  activeFrom: "2026-11-01T10:00:00Z", exportedAt: "2026-11-04T10:00:00Z", pageId: "0b5f3c1e-0000-4000-8000-0000000000a1",
  contentJson: JSON.stringify(document_), details: ["revision"] };

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

it("prints the requirement overview's numbers and each heading's state and party", async () => {
  Object.defineProperty(document, "fonts", { configurable: true, value: { ready: Promise.resolve() } });
  const ready = vi.fn(), failure = vi.fn();
  wrapper = mount(PrintDocument, { props: { snapshot, onReady: ready, onFailure: failure }, attachTo: document.body });
  await vi.waitFor(() => expect(ready.mock.calls.length + failure.mock.calls.length).toBe(1), { timeout: 10_000 });
  expect(failure).not.toHaveBeenCalled();
  const content = document.querySelector(".print-content")!, squash = (value?: string | null) => (value ?? "").replace(/\s+/g, " ");
  // The overview over the live document, not the old requirement blocks (its percentage counts up in motion, which jsdom does not end).
  // The card leads with the states and then the parties' counts (b12068488).
  const overview = squash(content.querySelector("[data-overview-kind='requirements']")?.textContent);
  expect(overview).toContain("1 z 2 hotovo");
  expect(overview).toContain("1 vstup · 1 předání");
  const rows = [...content.querySelectorAll<HTMLElement>(".ProseMirror > h3[data-state]")];
  expect(rows.map(row => [row.dataset.state, (row.querySelector("input.rq-check") as HTMLInputElement | null)?.checked,
    squash(row.querySelector("[data-rq-chip='state']")?.textContent).trim(), squash(row.querySelector("[data-rq-chip='kind']")?.textContent).trim()]))
    .toEqual([["done", true, "hotovo", "od DODAVATEL"], ["waiting_for_input", false, "čeká na vstup", "pro Nemocnice"]]);
});

it("keeps Atlas links relative without a configured public origin (a recurring export never takes the Host header)", async () => {
  Object.defineProperty(document, "fonts", { configurable: true, value: { ready: Promise.resolve() } });
  const linked = { format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [
    { type: "text", text: "Příručka", marks: [{ type: "link", attrs: { href: "/spaces/x" } }] }] }] } };
  const ready = vi.fn(), failure = vi.fn();
  wrapper = mount(PrintDocument, { props: { snapshot: { ...snapshot, sourceOrigin: "", contentJson: JSON.stringify(linked) }, onReady: ready, onFailure: failure },
    attachTo: document.body });
  await vi.waitFor(() => expect(ready.mock.calls.length + failure.mock.calls.length).toBe(1), { timeout: 10_000 });
  expect(failure).not.toHaveBeenCalled();
  expect(document.querySelector(".print-content a")?.getAttribute("href")).toBe("/spaces/x");
});
