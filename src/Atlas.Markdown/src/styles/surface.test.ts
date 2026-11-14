import { describe, expect, it } from "vitest";
import css from "./surface.css?raw";

describe("editor surface focus", () => {
  it("frames only the form-field editor; the page editor (paper) gets a keyboard-only bar", () => {
    expect(css).toContain(".md-editor:not(.paper):focus-within:not(.readonly)");
    expect(css).not.toMatch(/\.md-editor\.paper:focus-within/);
    expect(css).toMatch(/\.md-editor\.paper:not\(\.readonly\)\[data-focus-keyboard=\"true\"\] \.paper-inner/);
  });
});
