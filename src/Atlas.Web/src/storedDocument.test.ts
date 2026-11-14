import { beforeAll, describe, expect, it } from "vitest";
import { createMarkdownEditorController, loadMarkdownDialect } from "@atlas/markdown/core";
import hostSchema from "../../../tests/fixtures/host-document-schema.json";
import fixture from "../../../tests/fixtures/default-attributes.json";
import { atlasContentPlugins } from "./documentWidgets";

/** The editor's value with Atlas's plugins, opened from `document`: what a save sends. */
function saved(document: unknown) {
  const value = { format: "atlas-rich", version: 1, plugins: hostSchema.plugins, document } as never;
  const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value }, plugins: atlasContentPlugins({ kind: "edit" }) });
  try {
    controller.attachView(window.document.createElement("div"), { ariaLabel: "Stored" });
    return controller.getValue().document;
  } finally { controller.destroy(); }
}

beforeAll(() => loadMarkdownDialect());

describe("stored document form (tests/fixtures/default-attributes.json, the server's RichDocument.WithoutDefaultAttributes)", () => {
  it("saves without attributes at their default, from a document with them or without", () => {
    expect(saved(fixture.saved)).toEqual(fixture.stored);
    expect(saved(fixture.stored)).toEqual(fixture.stored);
  });
});
