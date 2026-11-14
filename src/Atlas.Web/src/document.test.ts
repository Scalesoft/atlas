import { describe, it, expect } from "vitest";
import { createMarkdownEditorController } from "@atlas/markdown/core";
import {
  createYouTrackPlugin,
  insertYouTrackIssue,
} from "@atlas/markdown/plugins/youtrack";
import { insertOpenApi } from "@atlas/markdown/plugins/openapi";
import { captureDocument, emptyDocument, readDocument } from "./document";
import { createDocumentWidgets } from "./documentWidgets";

// Comments, blank lines, trailing spaces, CRLF and quoting are part of the stored source.
const openApiYaml = [
  "# NIS API – interní kontrakt", "", "openapi: 3.1.0   ", "info:", "  title: NIS API   # zobrazovaný název", "  version: '2.3'",
  "", "", "paths:", "  /patients:", "    get:", "      responses:", "        '200':", '          description: "OK"', "# konec", "",
].join("\n");
const openApiJson = ['{', '    "openapi": "3.0.3",', '    "info": { "title": "NIS JSON", "version": "1.0" },', '    "paths": {}', '}'].join("\r\n");
function findSource(node: unknown): string | undefined {
  if (!node || typeof node !== "object") return undefined;
  const record = node as Record<string, unknown>;
  if (typeof record.source === "string" && typeof record.filename === "string") return record.source;
  for (const child of Object.values(record)) { const found = findSource(child); if (found !== undefined) return found; }
  return undefined;
}

describe("authoritative rich document persistence", () => {
  it("round-trips a YouTrack identity that its Markdown projection cannot preserve", () => {
    const plugins = [
      createYouTrackPlugin({ client: { search: async () => [] } }),
    ];
    const controller = createMarkdownEditorController({
      profile: "rich",
      source: { kind: "local", value: emptyDocument() },
      plugins,
    });
    controller.attachView(document.createElement("div"), {
      ariaLabel: "Test document",
    });
    const issue = {
      id: "4-512",
      key: "ATLAS-512",
      title: "Integrace wiki",
      url: "https://youtrack.example.test/issue/ATLAS-512",
    };
    expect(controller.dispatch(insertYouTrackIssue(issue))).toBe(true);
    const saved = captureDocument(controller);
    expect(controller.getProjection().markdownLossy).toBe(true);
    expect(saved.contentJson).toContain("4-512");
    expect(saved.markdown).not.toContain("4-512");
    const reopened = createMarkdownEditorController({
      profile: "rich",
      source: { kind: "local", value: readDocument(saved.contentJson) },
      plugins,
    });
    reopened.attachView(document.createElement("div"), {
      ariaLabel: "Reopened document",
    });
    expect(captureDocument(reopened).contentJson).toEqual(saved.contentJson);
    reopened.destroy();
    controller.destroy();
  });
  it.each([["nis.yaml", openApiYaml], ["nis.json", openApiJson]])("round-trips an OpenAPI source (%s) byte for byte", (filename, source) => {
    const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value: emptyDocument() },
      plugins: createDocumentWidgets({ kind: "edit", uploaderName: "Jana Nováková" }) });
    controller.attachView(document.createElement("div"), { ariaLabel: "Test document" });
    expect(controller.dispatch(insertOpenApi({ version: 1, filename, source, uploadedBy: "Jana Nováková" }))).toBe(true);
    const saved = captureDocument(controller);
    expect(saved.markdown).not.toContain("# konec");
    const value = readDocument(saved.contentJson);
    expect(findSource(value.document)).toBe(source);
    expect(value.plugins).toContainEqual({ id: "openapi", version: 1 });
    // History and public readers load the same saved JSON; reopening it changes nothing.
    const reopened = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value }, plugins: createDocumentWidgets({ kind: "history" }) });
    reopened.attachView(document.createElement("div"), { ariaLabel: "Reopened document" });
    expect(captureDocument(reopened).contentJson).toEqual(saved.contentJson);
    expect(findSource(readDocument(captureDocument(reopened).contentJson).document)).toBe(source);
    reopened.destroy();
    controller.destroy();
  });
  it("fails closed for malformed and future documents rather than replacing them with empty text", () => {
    expect(() => readDocument("not-json")).toThrow();
    expect(() =>
      readDocument(JSON.stringify({ ...emptyDocument(), version: 2 })),
    ).toThrow();
    expect(() =>
      readDocument(JSON.stringify({ document: { type: "doc" } })),
    ).toThrow();
  });
  it("reads a document stored before 5.0 under the legacy format id", () => {
    expect(readDocument(JSON.stringify({ ...emptyDocument(), format: "scalesoft-rich" })).document.type).toBe("doc");
  });
});
