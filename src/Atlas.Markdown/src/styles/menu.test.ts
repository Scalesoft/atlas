import { describe, expect, it } from "vitest";
import menu from "./menu.css?raw";
import controls from "./controls.css?raw";
import contextToolbar from "./contextToolbar.css?raw";

describe("eager menu look", () => {
  it("copies its declarations from the editor's menu rules", () => {
    const editor = (controls + contextToolbar).replace(/\s+/g, " ");
    const declarations = menu.replace(/\/\*[\s\S]*?\*\//g, "").match(/[a-z-]+:\s*[^;{}]+(?=;)/g) ?? [];
    expect(declarations.length).toBeGreaterThan(15);
    for (const declaration of declarations) expect(editor, declaration).toContain(declaration.replace(/\s+/g, " "));
  });
});
