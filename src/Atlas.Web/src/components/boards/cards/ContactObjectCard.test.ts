import { afterEach, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { inertBlockHost } from "../../../boards/editorBlocks";
import { contactCardShare } from "../../../boards/contactCardShare";
import type { LiveObject } from "../../../objects/liveObjects";
import ContactObjectCard from "./ContactObjectCard.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; contactCardShare.set("item", false); });
const object = (canEdit = false): LiveObject => ({ id: "c1", typeId: "atlas.contact", version: 1, title: "Jana", canEdit, available: true,
  data: { name: "Jana Nováková", title: "Vedoucí provozu", emails: ["jana@example.test"], phones: ["+420 602 000 222"] } });
const mountCard = (props: Record<string, unknown> = {}) => mount(ContactObjectCard, { attachTo: document.body,
  props: { object: object(), editing: false, compact: false, host: inertBlockHost, itemId: "item", height: 200, ...props } });

describe("ContactObjectCard", () => {
  it("draws every field and lets the card's room decide which are shown, with no share icons of its own", () => {
    wrapper = mountCard();
    expect(wrapper.find(".contact-card").classes()).toContain("is-fit");
    expect(wrapper.find(".contact-share").exists()).toBe(false);
    for (const field of ["title", "emails", "phones"]) expect(wrapper.find(`[data-contact-field="${field}"]`).exists(), field).toBe(true);
  });

  it("shows its QR code in its own place while the toolbar's action is on, and Esc then „Zpět“ take it away", async () => {
    wrapper = mountCard();
    const view = wrapper.get("[data-contact-qrview]");
    expect(view.attributes("inert")).toBeDefined();
    contactCardShare.set("item", true);
    await vi.waitFor(() => expect(wrapper!.find("[data-contact-qrview] .contact-qr-svg svg").exists()).toBe(true));
    expect(view.attributes("inert")).toBeUndefined();
    expect(wrapper.get(".contact-card").classes()).toContain("is-qr");
    await wrapper.get(".bcard-contact").trigger("keydown", { key: "Escape" });
    expect(contactCardShare.isOpen("item")).toBe(false);
    expect(wrapper.emitted("done")).toBeUndefined();
    contactCardShare.set("item", true);
    await wrapper.vm.$nextTick();
    await wrapper.get("[data-contact-qr-back]").trigger("click");
    expect(contactCardShare.isOpen("item")).toBe(false);
  });

  it("hands the focus from „Zpět“ to the card's node, not to the body, as the code turns inert", async () => {
    const node = Object.assign(document.createElement("div"), { className: "vue-flow__node", tabIndex: 0 });
    document.body.append(node);
    wrapper = mount(ContactObjectCard, { attachTo: node, props: { object: object(), editing: false, compact: false, host: inertBlockHost, itemId: "item", height: 200 } });
    contactCardShare.set("item", true);
    await wrapper.vm.$nextTick();
    const back = wrapper.get<HTMLElement>("[data-contact-qr-back]");
    back.element.focus();
    await back.trigger("click");
    expect(document.activeElement).toBe(node);
  });

  it("leaves a card too low to scan its code to the toolbar's dialog", async () => {
    wrapper = mountCard({ height: 90 });
    contactCardShare.set("item", true);
    await wrapper.vm.$nextTick();
    expect(wrapper.get(".contact-card").classes()).not.toContain("is-qr");
    expect(wrapper.get("[data-contact-qrview]").attributes("inert")).toBeDefined();
  });

  it("brings every field back while it is edited, and ends a code that was on", async () => {
    wrapper = mountCard({ object: object(true) });
    contactCardShare.set("item", true);
    await wrapper.setProps({ editing: true });
    expect(contactCardShare.isOpen("item")).toBe(false);
    expect(wrapper.get(".contact-card").classes()).toContain("is-editable");
    expect(wrapper.get(".bcard-contact").classes()).toContain("nodrag");
  });
});
