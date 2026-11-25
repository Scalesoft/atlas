import { afterEach, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import type { BoardViewItem } from "../../../boards/boardSession";
import BoardContextToolbar from "./BoardContextToolbar.vue";
import { contactCardShare } from "../../../boards/contactCardShare";
import { liveObjects } from "../../../objects/liveObjects";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

const item = (kind: BoardViewItem["kind"], extra: Partial<BoardViewItem> = {}): BoardViewItem => ({ id: kind, kind, x: 0, y: 0, width: 200, height: 100, z: 1, ...extra });

describe("BoardContextToolbar", () => {
  // A contact card's business card and QR code are the toolbar's actions (no icons on the card, nothing to collide with its grip).
  it("offers a contact card its vCard and QR code: in the card's place when it has room, in a dialog when it is low", async () => {
    liveObjects.seed({ id: "c1", typeId: "atlas.contact", version: 1, title: "Jana", canEdit: true, available: true, data: { name: "Jana Nováková", emails: ["jana@example.test"] } });
    const card = (height: number) => item("object", { id: "card", targetResourceId: "c1", props: { typeId: "atlas.contact" }, height });
    wrapper = mount(BoardContextToolbar, { props: { items: [card(200)], canEdit: false }, attachTo: document.body });
    expect(wrapper.find("[data-board-contact-vcf]").exists()).toBe(true);
    await wrapper.get("[data-board-contact-qr]").trigger("click");
    expect(contactCardShare.isOpen("card")).toBe(true);
    expect(wrapper.get("[data-board-contact-qr]").attributes("aria-pressed")).toBe("true");
    expect(document.body.querySelector("dialog[open]")).toBeNull();
    await wrapper.get("[data-board-contact-qr]").trigger("click");
    expect(contactCardShare.isOpen("card")).toBe(false);
    wrapper.unmount();

    wrapper = mount(BoardContextToolbar, { props: { items: [card(80)], canEdit: false }, attachTo: document.body });
    await wrapper.get("[data-board-contact-qr]").trigger("click");
    expect(contactCardShare.isOpen("card")).toBe(true);
    await vi.waitFor(() => expect(document.body.querySelector("dialog [data-contact-qr-panel] .contact-qr-code svg")).not.toBeNull());
    contactCardShare.set("card", false);
    wrapper.unmount();

    // The keyboard path: the editor's toolbar keys walk the buttons (arrows, Home, End), Enter or Space presses one.
    wrapper = mount(BoardContextToolbar, { props: { items: [card(200)], canEdit: true }, attachTo: document.body });
    const vcf = wrapper.get<HTMLButtonElement>("[data-board-contact-vcf]"), qr = wrapper.get<HTMLButtonElement>("[data-board-contact-qr]");
    vcf.element.focus();
    await vcf.trigger("keydown", { key: "ArrowRight" });
    expect(document.activeElement).toBe(qr.element);
    await qr.trigger("keydown", { key: "ArrowLeft" });
    expect(document.activeElement).toBe(vcf.element);
    expect(qr.element.tagName).toBe("BUTTON");
  });

  // T5: the board's toolbar had worded buttons („Čárkovaně“, „Seskupit“, „Detail“) where a page block's toolbar has icons.
  it("is a row of icon buttons as a page block's toolbar, each named by its label and its current value", () => {
    const kinds: BoardViewItem[][] = [[item("connector")], [item("group")], [item("page", { targetResourceId: "p" })], [item("block")], [item("shape")], [item("note"), item("text", { id: "t" })]];
    for (const items of kinds) {
      wrapper = mount(BoardContextToolbar, { props: { items, canEdit: true }, attachTo: document.body });
      for (const button of wrapper.findAll("button")) {
        expect(button.text(), `${items[0]!.kind}: ${button.html().slice(0, 120)}`).toBe("");
        expect(button.attributes("aria-label"), button.html().slice(0, 120)).toBeTruthy();
        expect(button.attributes("title")).toBeTruthy();
      }
      wrapper.unmount();
    }
    wrapper = mount(BoardContextToolbar, { props: { items: [item("shape", { props: { shape: "ellipse" } })], canEdit: true }, attachTo: document.body });
    expect(wrapper.get("[data-board-shape-menu]").attributes("aria-label")).toBe("Tvar: Elipsa");
    expect(wrapper.get("[data-board-delete]").attributes("aria-label")).toBe("Smazat");
    wrapper.unmount();
    // P12: a card leaves the board, its page stays („Odebrat“); a double-click no longer opens the detail, Ctrl+Enter opens the page.
    wrapper = mount(BoardContextToolbar, { props: { items: [item("page", { targetResourceId: "p" })], canEdit: true }, attachTo: document.body });
    expect(wrapper.get("[data-board-delete]").attributes("title")).toBe("Odebrat z tabule (Delete)");
    expect(wrapper.get("[data-board-detail]").attributes("title")).toBe("Otevřít detail");
    expect(wrapper.get("[data-board-open]").attributes("title")).toBe("Otevřít stránku (Ctrl+Enter)");
  });

  // Owner, 28. 10.: an editor component's colour is its background, picked with the same AppearanceColorPicker as a space's colour.
  it("offers a component the shared colour picker of spaces and a note the board's colours", async () => {
    const chart = item("block", { props: { block: { type: "chart" } }, color: "blue-4" });
    wrapper = mount(BoardContextToolbar, { props: { items: [chart], canEdit: true }, attachTo: document.body });
    expect(wrapper.get("[data-board-color-trigger]").attributes("aria-label")).toBe("Pozadí: Modrá · tmavá");
    await wrapper.get("[data-board-color-trigger]").trigger("click");
    // Bez pozadí, Bílá, Černá lead the ten quick colours; a stored colour outside the ten opens the palette with it checked.
    const quick = [...document.body.querySelectorAll<HTMLElement>("[data-appearance-quick-colors] [role=radio]")];
    expect(quick.map(radio => radio.getAttribute("aria-label")).slice(0, 3)).toEqual(["Bez pozadí", "Bílá", "Černá"]);
    expect(quick).toHaveLength(13);
    expect(document.body.querySelectorAll("[data-appearance-colors] [role=radio]")).toHaveLength(60);
    expect(document.body.querySelector("[data-appearance-colors] [data-value='blue-4']")?.getAttribute("aria-checked")).toBe("true");
    document.body.querySelector<HTMLElement>("[data-appearance-colors] [data-value='slate-5']")!.click();
    // The colours tried in one opening of the palette are one undo step (the canvas coalesces on the run).
    expect(wrapper.emitted("style")).toEqual([[{ color: "slate-5" }, "palette#1"]]);
    document.body.querySelector<HTMLElement>("[data-appearance-quick-colors] [data-value='none']")!.click();
    document.body.querySelector<HTMLElement>("[data-appearance-quick-colors] [data-value='white']")!.click();
    expect(wrapper.emitted("style")!.slice(1)).toEqual([[{ color: null }, "palette#1"], [{ color: "white" }, "palette#1"]]);
    wrapper.unmount();
    document.body.innerHTML = "";

    // Nothing set: the none swatch is checked and the palette stays closed.
    wrapper = mount(BoardContextToolbar, { props: { items: [item("block", { props: { block: { type: "chart" } } })], canEdit: true }, attachTo: document.body });
    expect(wrapper.get("[data-board-color-trigger]").attributes("aria-label")).toBe("Pozadí: Bez pozadí");
    await wrapper.get("[data-board-color-trigger]").trigger("click");
    expect(document.body.querySelector("[data-appearance-quick-colors] [data-value='none']")?.getAttribute("aria-checked")).toBe("true");
    expect(wrapper.get("[data-board-color-trigger]").attributes("aria-label")).toBe("Pozadí: Bez pozadí");
    wrapper.unmount();
    document.body.innerHTML = "";

    wrapper = mount(BoardContextToolbar, { props: { items: [item("note", { color: "yellow" })], canEdit: true }, attachTo: document.body });
    expect(wrapper.get("[data-board-color-trigger]").attributes("aria-label")).toBe("Barva: Žlutá");
    await wrapper.get("[data-board-color-trigger]").trigger("click");
    const radios = [...document.body.querySelectorAll<HTMLElement>(".board-palette-grid [role=radio]")];
    expect(radios).toHaveLength(12);
    // Roving tabindex: only the checked swatch is in the tab order (the none swatch when nothing is checked).
    expect(radios.filter(radio => radio.tabIndex === 0).map(radio => radio.dataset.boardColor)).toEqual(["yellow"]);
  });
});
