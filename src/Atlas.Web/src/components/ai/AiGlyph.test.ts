import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import { iconMarkup } from "@atlas/markdown/icons";
import AiGlyph from "./AiGlyph.vue";

// The one AI icon (plan §0a.1): the registry's sparkles drawing, so Atlas and the editor show the
// same geometry; its meaning is carried by the text beside it, and the twinkle means "the model is
// working" and nothing else.
describe("AiGlyph", () => {
  it("renders the one sparkles glyph, hidden from assistive technology, and twinkles only while working", async () => {
    const glyph = mount(AiGlyph, { props: { size: 14 } });
    const root = glyph.get("[data-ai-glyph]");
    expect(root.attributes("aria-hidden")).toBe("true");
    const svg = root.get("svg");
    const registry = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    registry.innerHTML = iconMarkup.sparkles;
    expect(svg.element.innerHTML).toBe(registry.innerHTML);
    expect(svg.attributes("width")).toBe("14");
    expect(glyph.find(".atlas-ai-twinkle").exists()).toBe(false);

    await glyph.setProps({ working: true });
    expect(root.find("svg").classes()).toContain("atlas-ai-twinkle");
    expect(root.attributes("data-working")).toBe("true");

    await glyph.setProps({ working: false });
    expect(glyph.find(".atlas-ai-twinkle").exists()).toBe(false);
  });

  it("takes the named sizes of the board and draws the tick only while it says done", async () => {
    const glyph = mount(AiGlyph, { props: { size: "xl", state: "searching" } });
    expect(glyph.get("svg").attributes("width")).toBe("44");
    expect(glyph.get("[data-ai-glyph]").attributes("data-working")).toBe("true");
    expect(glyph.find(".ai-glyph-check").exists()).toBe(false);
    await glyph.setProps({ size: "xs", state: "done" });
    expect(glyph.get("svg").attributes("width")).toBe("12");
    expect(glyph.get("[data-ai-glyph]").attributes("data-working")).toBeUndefined();
    expect(glyph.find(".ai-glyph-check").exists()).toBe(true);
    // The tick is a second drawing; the AI glyph itself stays the registry's geometry.
    expect(glyph.findAll("svg")[0]!.classes()).toContain("ai-glyph-mark");
  });
});
