import { describe, expect, it } from "vitest";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { sliceSection } from "./boardSections";

const h = (level: number, headingId: string, text = headingId) => ({ type: "heading", attrs: { level, headingId }, content: [{ type: "text", text }] });
const p = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const page = (...content: object[]): MarkdownRichValue =>
  ({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }], document: { type: "doc", content } }) as unknown as MarkdownRichValue;
const contentOf = (value: MarkdownRichValue | null) => (value?.document as { content?: unknown[] } | undefined)?.content;

describe("sliceSection", () => {
  const doc = page(p("úvod"), h(1, "a", "A"), p("a1"), h(2, "b", "B"), p("b1"), h(3, "b-1"), p("b2"), h(2, "c", "C"), p("c1"), h(1, "d", "D"), p("d1"));

  it("returns the heading and the content up to the next heading of the same or a higher level, deeper headings included", () => {
    expect(contentOf(sliceSection(doc, "b"))).toEqual([h(2, "b", "B"), p("b1"), h(3, "b-1"), p("b2")]);
    expect(contentOf(sliceSection(doc, "a"))).toEqual([h(1, "a", "A"), p("a1"), h(2, "b", "B"), p("b1"), h(3, "b-1"), p("b2"), h(2, "c", "C"), p("c1")]);
  });

  it("ends the first section at its sibling and the last one at the end of the page", () => {
    expect(contentOf(sliceSection(doc, "b-1"))).toEqual([h(3, "b-1"), p("b2")]);
    expect(contentOf(sliceSection(doc, "d"))).toEqual([h(1, "d", "D"), p("d1")]);
  });

  it("reads a heading stored without its default level as level 1", () => {
    const h1 = (headingId: string) => ({ type: "heading", attrs: { headingId }, content: [{ type: "text", text: headingId }] });
    expect(contentOf(sliceSection(page(h1("s"), p("s1"), h(2, "t"), h1("u"), p("u1")), "s"))).toEqual([h1("s"), p("s1"), h(2, "t")]);
  });

  it("finds a section by its heading id, not by its title", () => {
    const twice = page(h(2, "x", "Postup"), p("první"), h(2, "y", "Postup"), p("druhý"));
    expect(contentOf(sliceSection(twice, "y"))).toEqual([h(2, "y", "Postup"), p("druhý")]);
  });

  it("does not end a section at a heading inside another block, and slices a heading inside one within its block", () => {
    const callout = { type: "callout", content: [h(1, "in", "Uvnitř"), p("i1"), h(2, "in-2"), p("i2"), h(1, "in-3"), p("i3")] };
    const nested = page(h(2, "s"), p("s1"), callout, p("s2"), h(2, "t"));
    expect(contentOf(sliceSection(nested, "s"))).toEqual([h(2, "s"), p("s1"), callout, p("s2")]);
    expect(contentOf(sliceSection(nested, "in"))).toEqual([h(1, "in", "Uvnitř"), p("i1"), h(2, "in-2"), p("i2")]);
  });

  it("is null for an anchor the page no longer has, and for none", () => {
    expect(sliceSection(doc, "gone")).toBeNull();
    expect(sliceSection(doc, "")).toBeNull();
    expect(sliceSection(page(), "a")).toBeNull();
  });

  it("keeps the envelope and leaves the page as it was", () => {
    const before = JSON.stringify(doc);
    const section = sliceSection(doc, "c")!;
    expect(section).toMatchObject({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }], document: { type: "doc" } });
    expect(JSON.stringify(doc)).toBe(before);
  });
});
