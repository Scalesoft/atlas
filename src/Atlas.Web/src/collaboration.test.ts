import { describe, it, expect, vi } from "vitest";
import type { MarkdownEditorState } from "@atlas/markdown/core";
import { collaborationCanSave, collaborationCheckpointReady, collaborationSessionCheck } from "./collaboration";
import {
  captureCollaborationBaseline,
  hasHydrationChanges,
  hydrationChanges,
} from "./collaboration";
import { createMarkdownEditorController } from "@atlas/markdown/core";
import { emptyDocument } from "./document";
import { createAttachmentsPlugin, insertAttachment } from "@atlas/markdown/plugins/attachments";
import { createEditorToolsPlugin } from "@atlas/markdown/plugins/editor-tools";
import { createHeadingAnchorsPlugin } from "@atlas/markdown/plugins/heading-anchors";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { createDocumentWidgets } from "./documentWidgets";
/** The page's content plugins (charts included), fresh per call like DocumentEditor's. */
const pagePlugins = () => [createEditorToolsPlugin(), createAttachmentsPlugin(), ...createDocumentWidgets({ kind: "history" }),
  createHeadingAnchorsPlugin({ getHeadingUrl: id => `#${id}` })];
const text = (value: string) => [{ type: "text", text: value }];
const pluginMetadata = () => [{ id: "charts", version: 1 }, { id: "heading-anchors", version: 1 }];
/** A stored page as the host sends it: minimal attributes, host key order, a table as the last block. */
function storedTablePage(): MarkdownRichValue {
  const cell = (type: string, value: string) => ({ content: [{ type: "paragraph", content: text(value) }], type });
  return { format: "atlas-rich", version: 1, plugins: pluginMetadata(), document: { type: "doc", content: [
    { type: "heading", attrs: { level: 2 }, content: text("Ceník") },
    { type: "paragraph", content: text("Aktuální ceny.") },
    { type: "table", content: [
      { type: "tableRow", content: [cell("tableHeader", "Položka"), cell("tableHeader", "Cena")] },
      { type: "tableRow", content: [cell("tableCell", "Licence"), cell("tableCell", "100")] },
    ] },
  ] } };
}
/**
 * The same page as the seeded shared document hydrates it: schema defaults in schema key order, a heading id,
 * random chart references on the table, its rows and cells, and the empty paragraph after the final table.
 */
function hydratedTablePage(cellText = "100"): MarkdownRichValue {
  let next = 0;
  const ref = () => `ref-${++next}-${Math.random().toString(36).slice(2)}`;
  const cell = (type: string, value: string) => ({ type, attrs: { textAlign: null, backgroundColor: null, chartRef: ref(),
    colspan: 1, rowspan: 1, colwidth: null, align: null }, content: [{ type: "paragraph", attrs: { textAlign: null }, content: text(value) }] });
  return { format: "atlas-rich", version: 1, plugins: pluginMetadata().reverse(), document: { type: "doc", content: [
    { type: "heading", attrs: { headingId: "cenik", textAlign: null, level: 2 }, content: text("Ceník") },
    { type: "paragraph", attrs: { textAlign: null }, content: text("Aktuální ceny.") },
    { type: "table", attrs: { chartRef: ref() }, content: [
      { type: "tableRow", attrs: { height: null, chartRef: ref() }, content: [cell("tableHeader", "Položka"), cell("tableHeader", "Cena")] },
      { type: "tableRow", attrs: { height: null, chartRef: ref() }, content: [cell("tableCell", "Licence"), cell("tableCell", cellText)] },
    ] },
    { type: "paragraph", attrs: { textAlign: null } },
  ] } };
}
describe("hydration changes", () => {
  it("ignores the charts plugin's references, a heading id and the trailing paragraph against the stored fallback", () => {
    const baseline = captureCollaborationBaseline(undefined, storedTablePage());
    expect(hasHydrationChanges(baseline, hydratedTablePage(), pagePlugins)).toBe(false);
  });
  it("ignores them against a ready local readonly editor's baseline as well", () => {
    const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value: storedTablePage() },
      plugins: pagePlugins(), editable: false });
    const detach = controller.attachView(document.createElement("div"), { ariaLabel: "Baseline" });
    try {
      const baseline = captureCollaborationBaseline(controller, emptyDocument());
      expect(baseline.document).not.toEqual(emptyDocument().document);
      expect(hasHydrationChanges(baseline, hydratedTablePage(), pagePlugins)).toBe(false);
    } finally { detach(); controller.destroy(); }
  });
  it("still reports another writer's unsaved content in the hydrated document", () => {
    const baseline = storedTablePage();
    expect(hasHydrationChanges(baseline, hydratedTablePage("250"), pagePlugins)).toBe(true);
    const appended = hydratedTablePage();
    (appended.document.content as unknown[]).push({ type: "paragraph", content: text("Neuložená změna kolegy.") });
    expect(hasHydrationChanges(baseline, appended, pagePlugins)).toBe(true);
  });
  // P2-3: a page the server wrote (API, MCP, import, a deployment copy) differs from its hydration only in key order and null
  // defaults; projecting both sides of a 500-block page took ~1.4 s right after the editor became usable.
  it("decides without projecting when only key order, null defaults and the trailing paragraph differ", () => {
    const stored: MarkdownRichValue = { format: "atlas-rich", version: 1, document: { type: "doc", content: [
      { type: "heading", attrs: { level: 2 }, content: text("Postup") },
      ...Array.from({ length: 300 }, (_, index) => ({ content: text(`Krok ${index}`), type: "paragraph" })),
    ] } };
    const hydrated: MarkdownRichValue = { format: "atlas-rich", version: 1, document: { type: "doc", content: [
      { type: "heading", attrs: { textAlign: null, level: 2 }, content: text("Postup") },
      ...Array.from({ length: 300 }, (_, index) => ({ type: "paragraph", attrs: { textAlign: null }, content: text(`Krok ${index}`) })),
      { type: "paragraph", attrs: { textAlign: null } },
    ] } };
    const plugins = vi.fn(pagePlugins);
    expect(hasHydrationChanges(stored, hydrated, plugins)).toBe(false);
    expect(plugins).not.toHaveBeenCalled();
    (hydrated.document.content as unknown[]).splice(5, 0, { type: "paragraph", content: text("Neuložená změna kolegy.") });
    expect(hasHydrationChanges(stored, hydrated, plugins)).toBe(true);
  });
  it("projects one side per step, so each projection can take its own idle period", () => {
    const plugins = vi.fn(pagePlugins);
    const steps = hydrationChanges(storedTablePage(), hydratedTablePage("250"), plugins);
    expect(steps.next()).toEqual({ done: false, value: undefined });
    expect(plugins).toHaveBeenCalledOnce();
    expect(steps.next()).toEqual({ done: true, value: true });
    expect(plugins).toHaveBeenCalledTimes(2);
    expect(hydrationChanges(storedTablePage(), storedTablePage(), plugins).next()).toEqual({ done: true, value: false });
  });
  it("offers the hydrated document when it cannot be projected", () => {
    expect(hasHydrationChanges(storedTablePage(), hydratedTablePage(), () => { throw new Error("broken plugin"); })).toBe(true);
  });
});
it("ignores schema default attributes but detects real hydrated changes against the normalized local baseline", () => {
  const original = emptyDocument();
  const controller = createMarkdownEditorController({
    profile: "rich",
    source: { kind: "local", value: original },
    plugins: [createAttachmentsPlugin()],
  });
  const host = document.createElement("div");
  document.body.append(host);
  const detach = controller.attachView(host, { ariaLabel: "Baseline" });
  try {
    const baseline = captureCollaborationBaseline(controller, original);
    expect(baseline).toEqual(controller.getValue());
    // The editor's value leaves attributes at their default out, as the original has them.
    expect(baseline.document).toEqual(original.document);
    const plugins = () => [createAttachmentsPlugin()];
    expect(hasHydrationChanges(baseline, controller.getValue(), plugins)).toBe(false);
    expect(hasHydrationChanges(original, controller.getValue(), plugins)).toBe(false);
    controller.dispatch(insertAttachment({ version: 1, name: "Hydrated.pdf", url: "/files/hydrated.pdf", size: 42, mimeType: "application/pdf" }));
    expect(hasHydrationChanges(baseline, controller.getValue(), plugins)).toBe(true);
  } finally {
    detach();
    controller.destroy();
    host.remove();
  }
});
it("uses the stored value while no ready local controller exists", () => {
  const stored = emptyDocument();
  expect(captureCollaborationBaseline(undefined, stored)).toBe(stored);
});
const online = {
  lifecycle: "ready",
  binding: "collaborative",
  editable: true,
  focused: false,
  connection: "online",
  link: null,
  documentSave: "saved",
  projectionSave: "pending",
  pendingChanges: 0,
  error: null,
  peers: [],
  contentRevision: 2,
  changeOrigin: "local",
} satisfies MarkdownEditorState;
describe("collaborative save boundary", () => {
  it("allows a nonsnapshotter with all updates acknowledged while the server fences projection freshness", () => {
    expect(collaborationCanSave(online)).toBe(true);
  });

  it("can checkpoint a document locked by a source draft, but still requires acknowledged online changes", () => {
    const locked = { ...online, editable: false };
    expect(collaborationCanSave(locked)).toBe(false);
    expect(collaborationCheckpointReady(locked)).toBe(true);
    expect(collaborationCheckpointReady({ ...locked, pendingChanges: 1 })).toBe(false);
    expect(collaborationCheckpointReady({ ...locked, connection: "offline" })).toBe(false);
  });
  it.each([
    { connection: "offline" },
    { pendingChanges: 1 },
    { editable: false },
    { documentSave: "pending" },
    { projectionSave: "degraded" },
    { lifecycle: "starting" },
    {
      error: {
        kind: "collaboration",
        message: "Access revoked",
        recoverable: false,
      },
    },
  ] as Partial<MarkdownEditorState>[])("blocks unsafe state %j", (patch) => {
    expect(collaborationCanSave({ ...online, ...patch })).toBe(false);
  });
});

describe("collaborationSessionCheck (ARCH3-7)", () => {
  it("checks the session each time the page editor's connection is lost or fails, once per change", () => {
    const check = vi.fn(async () => {});
    const seen = collaborationSessionCheck(check);
    for (const connection of ["connecting", "online", "online", "reconnecting", "reconnecting", "online", "failed", "failed"] as const) seen(connection);
    expect(check).toHaveBeenCalledTimes(2);
    seen("offline");
    expect(check).toHaveBeenCalledTimes(3);
  });

  it("checks nothing for a page edited without collaboration", () => {
    const check = vi.fn(async () => {});
    collaborationSessionCheck(check)("local");
    expect(check).not.toHaveBeenCalled();
  });
});
