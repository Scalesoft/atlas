import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import ContactCard from "./ContactCard.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });
const jana = { name: "**Jana** Nováková", title: "Ředitelka", emails: ["jana@example.com"], phones: ["+420 777 123 456"] };

it("shows the identifiers as links and nothing to edit when it is not editable", () => {
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: jana } });
  expect(wrapper.get('a[href="mailto:jana@example.com"]').text()).toBe("jana@example.com");
  expect(wrapper.get('a[href="tel:+420777123456"]').text()).toBe("+420 777 123 456");
  expect(wrapper.find(".contact-edit-link").exists()).toBe(false);
  expect(wrapper.find('[data-contact-field="address"]').exists()).toBe(false);
});

it("unfolds the card's QR code under it: the plain-text card drawn black on white, no link to the wiki", async () => {
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: jana } });
  const toggle = wrapper.get("[data-contact-qr]");
  await toggle.trigger("click");
  await vi.waitFor(() => expect(wrapper!.find("[data-contact-qr-panel] svg").exists()).toBe(true));
  expect(toggle.attributes("aria-expanded")).toBe("true");
  expect(wrapper.get(`#${toggle.attributes("aria-controls")} [role=img]`).attributes("aria-label")).toBeTruthy();
  expect(wrapper.get("[data-contact-qr-panel] svg rect").attributes("fill")).toBe("white");
  expect(wrapper.findAll("[data-contact-qr-panel] svg path")).toHaveLength(9);
});

it("edits an identifier in place and emits the change of just that field, validated by the editor's rules", async () => {
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: jana, editable: true } });
  await wrapper.get('[data-contact-field="emails"] .contact-edit-link').trigger("click");
  const input = wrapper.get("[data-contact-input]");
  await input.setValue("jana@example.com\nj.n@firma.example.test");
  await input.trigger("keydown", { key: "Enter", ctrlKey: true });
  await vi.waitFor(() => expect(wrapper!.emitted("change")).toBeDefined());
  expect(wrapper.emitted("change")).toEqual([[{ emails: ["jana@example.com", "j.n@firma.example.test"] }]]);
  expect(wrapper.find("[data-contact-input]").exists()).toBe(false);

  await wrapper.get('[data-contact-field="phones"] .contact-edit-link').trigger("click");
  await wrapper.get("[data-contact-input]").setValue("call me");
  await wrapper.get("[data-contact-input]").trigger("blur");
  await vi.waitFor(() => expect(wrapper!.find("[data-contact-error]").exists()).toBe(true));
  expect(wrapper.emitted("change")).toHaveLength(1);
});

it("Esc leaves a field without a change", async () => {
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: jana, editable: true } });
  await wrapper.get("[data-contact-reveal]").trigger("click");
  await wrapper.get('[data-contact-field="website"] .contact-edit-link').trigger("click");
  await wrapper.get("[data-contact-input]").setValue("example.test");
  (wrapper.get("[data-contact-input]").element as HTMLElement).focus();
  await wrapper.get("[data-contact-input]").trigger("keydown", { key: "Escape" });
  expect(wrapper.find("[data-contact-input]").exists()).toBe(false);
  expect(wrapper.emitted("change")).toBeUndefined();
  // The keyboard stays on the field it left, not on the page (keyboard pass).
  await wrapper.vm.$nextTick();
  expect(wrapper.get('[data-contact-field="website"]').element.contains(document.activeElement)).toBe(true);
});

it("leaving a field for another control keeps the focus there", async () => {
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: jana, editable: true } });
  const other = document.body.appendChild(document.createElement("input"));
  await wrapper.get('[data-contact-field="emails"] .contact-edit-link').trigger("click");
  await wrapper.vm.$nextTick();
  const input = wrapper.get("[data-contact-input]").element as HTMLElement;
  expect(document.activeElement).toBe(input);
  // As a click on the panel's role: at the blur the focus is on <body>, the control it goes to is the blur's relatedTarget.
  input.dispatchEvent(new FocusEvent("blur", { relatedTarget: other }));
  other.focus();
  await vi.waitFor(() => expect(wrapper!.find("[data-contact-input]").exists()).toBe(false));
  await wrapper.vm.$nextTick();
  expect(document.activeElement).toBe(other);
});

it("Esc leaves a Markdown field unchanged, as it leaves an identifier", async () => {
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: jana, editable: true } });
  await wrapper.get('[data-contact-field="title"]').trigger("click");
  type Editable = HTMLElement & { editor?: import("@tiptap/core").Editor };
  await vi.waitFor(() => expect(document.querySelector<Editable>('[data-contact-field="title"] .ProseMirror')?.editor).toBeDefined());
  await flushPromises();
  const prose = document.querySelector<Editable>('[data-contact-field="title"] .ProseMirror')!;
  prose.editor!.chain().focus("end").insertContent(" a jednatelka").run();
  await flushPromises();
  prose.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", keyCode: 27, bubbles: true, cancelable: true }));
  await flushPromises();
  expect(wrapper.find('[data-contact-field="title"] [data-board-text-editor]').exists()).toBe(false);
  expect(wrapper.emitted("change")).toBeUndefined();
  // Enter still saves.
  await wrapper.get('[data-contact-field="title"]').trigger("click");
  await vi.waitFor(() => expect(document.querySelector<Editable>('[data-contact-field="title"] .ProseMirror')?.editor).toBeDefined());
  await flushPromises();
  const again = document.querySelector<Editable>('[data-contact-field="title"] .ProseMirror')!;
  again.editor!.chain().focus("end").insertContent(" a jednatelka").run();
  await flushPromises();
  again.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", keyCode: 13, bubbles: true, cancelable: true }));
  await vi.waitFor(() => expect(wrapper!.emitted("change")).toEqual([[{ title: "Ředitelka a jednatelka" }]]));
});

it("in the editor's panel the first Esc leaves just the field and the next one closes the panel", async () => {
  const [{ Editor }, { default: StarterKit }, { openMarkdownContextPopover }] = await Promise.all([import("@tiptap/core"), import("@tiptap/starter-kit"), import("@atlas/markdown/core")]);
  const editor = new Editor({ element: document.body.appendChild(document.createElement("div")), extensions: [StarterKit] });
  const anchor = document.body.appendChild(document.createElement("button"));
  openMarkdownContextPopover({ editor, anchor, title: "Kontakt", trackSelection: false, render: panel => { wrapper = mount(ContactCard, { attachTo: panel.appendChild(document.createElement("div")), props: { contact: jana, editable: true } }); } });
  const panel = () => document.querySelector(".md-context-popover");
  await wrapper!.get('[data-contact-field="phones"] .contact-edit-link').trigger("click");
  const input = wrapper!.get("[data-contact-input]").element as HTMLElement;
  input.focus();
  input.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
  await wrapper!.vm.$nextTick();
  expect(wrapper!.find("[data-contact-input]").exists()).toBe(false);
  expect(panel()).not.toBeNull();
  await wrapper!.vm.$nextTick();
  document.activeElement!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
  expect(panel()).toBeNull();
  expect(wrapper!.emitted("change")).toBeUndefined();
  editor.destroy();
});

// UX1-5 (F3): six empty „+ Funkce …“ on every row of the directory; they wait until the card is being edited.
it("keeps the empty fields out of sight until the keyboard comes into the card, and hides them again when it leaves", async () => {
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: jana, editable: true } });
  const visibleEmpty = () => wrapper!.findAll(".contact-field.is-empty").filter(field => field.attributes("hidden") === undefined).map(field => field.attributes("data-contact-field"));
  expect(visibleEmpty()).toEqual([]);
  expect(wrapper.find(".contact-placeholder").exists()).toBe(false);
  expect(wrapper.find('[data-contact-field="address"]').exists()).toBe(false);
  expect(wrapper.find("[data-contact-reveal]").exists()).toBe(true);

  // Tab onto the name: the card opens and Tab goes on into the empty fields.
  (wrapper.get('[data-contact-field="name"]').element as HTMLElement).focus();
  await wrapper.vm.$nextTick();
  expect(visibleEmpty()).toEqual(["organization", "department", "website", "aliases", "address"]);
  expect(wrapper.find("[data-contact-reveal]").exists()).toBe(false);
  expect(wrapper.get('[data-contact-field="organization"]').attributes("tabindex")).toBe("0");

  (document.activeElement as HTMLElement).blur();
  await vi.waitFor(() => expect(visibleEmpty()).toEqual([]));
});

it("Doplnit opens the card at its first empty field, and a read-only or compact card never offers it", async () => {
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: jana, editable: true } });
  await wrapper.get("[data-contact-reveal]").trigger("click");
  await vi.waitFor(() => expect(document.activeElement).toBe(wrapper!.get('[data-contact-field="organization"]').element));
  await new Promise(resolve => setTimeout(resolve));
  expect(wrapper.find('[data-contact-field="address"]').exists()).toBe(true);
  wrapper.unmount();

  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: jana, editable: true, compact: true } });
  expect(wrapper.find("[data-contact-reveal]").exists()).toBe(false);
  wrapper.unmount();
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: { ...jana, organization: "A", department: "B", website: "https://a.cz", address: "C", aliases: ["JN"] }, editable: true } });
  expect(wrapper.find("[data-contact-reveal]").exists()).toBe(false);
});

it("shows the aliases people search by and edits them in place, one per line, each once; none left clears them", async () => {
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: { ...jana, aliases: ["JN", "Janička"] }, editable: true } });
  expect([...wrapper.findAll(".contact-alias")].map(alias => alias.text())).toEqual(["JN", "Janička"]);
  await wrapper.get('[data-contact-field="aliases"] .contact-edit-link').trigger("click");
  const input = wrapper.get("[data-contact-input]");
  await input.setValue("JN\n  Janicka \njn\nJNo");
  await input.trigger("keydown", { key: "Enter", ctrlKey: true });
  await vi.waitFor(() => expect(wrapper!.emitted("change")).toEqual([[{ aliases: ["JN", "Janicka", "JNo"] }]]));
  await wrapper.get('[data-contact-field="aliases"] .contact-edit-link').trigger("click");
  await wrapper.get("[data-contact-input]").setValue("");
  await wrapper.get("[data-contact-input]").trigger("blur");
  await vi.waitFor(() => expect(wrapper!.emitted("change")).toHaveLength(2));
  expect(wrapper.emitted("change")![1]).toEqual([{ aliases: null }]);
  await wrapper.get('[data-contact-field="aliases"] .contact-edit-link').trigger("click");
  await wrapper.get("[data-contact-input]").setValue("x".repeat(61));
  await wrapper.get("[data-contact-input]").trigger("blur");
  await vi.waitFor(() => expect(wrapper!.get("[data-contact-error]").text()).toContain("60 znaků"));
});

// Owner guard: a contact's own field is that contact's text; typing a name there never searches people nor makes a mention.
it("types a known name into its own name field as plain text, without any people search", async () => {
  const search = vi.fn(async () => [{ name: "Jana Nováková", objectId: "0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b" }]);
  const { inertBlockHost } = await import("../../boards/editorBlocks");
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: { name: "Nový" }, editable: true, host: { ...inertBlockHost, contacts: { search, get: async () => null } } } });
  await wrapper.get('[data-contact-field="name"]').trigger("click");
  type Editable = HTMLElement & { editor?: import("@tiptap/core").Editor };
  const prose = await vi.waitFor(() => { const element = document.querySelector<Editable>('[data-contact-field="name"] .ProseMirror'); expect(element?.editor).toBeDefined(); return element! });
  for (const char of " @Jana Nováková ") prose.editor!.view.dispatch(prose.editor!.state.tr.insertText(char));
  await new Promise(resolve => setTimeout(resolve, 300));
  expect(search).not.toHaveBeenCalled();
  expect(prose.querySelector(".md-contact-inline")).toBeNull();
  expect(document.querySelector("[data-testid=contact-suggestions]")).toBeNull();
  expect(prose.editor!.getText()).toContain("@Jana Nováková");
});

it("shows the logo or photo over the initials and uploads or removes it where the holder can upload (package G)", async () => {
  const photo = "/api/contacts/00000000-0000-4000-8000-000000000001/photo?v=0123456789abcdef";
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: { ...jana, photo } } });
  const image = wrapper.get("[data-contact-photo]");
  expect([image.attributes("src"), image.attributes("loading"), wrapper.get(".contact-avatar").text()]).toEqual([photo, "lazy", "JN"]);
  await image.trigger("error");
  expect(wrapper.find("[data-contact-photo]").exists()).toBe(false);
  wrapper.unmount();

  const upload = vi.fn(async () => photo);
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: jana, editable: true, upload } });
  await wrapper.get("[data-contact-reveal]").trigger("click");
  const chooser = vi.spyOn(HTMLInputElement.prototype, "click").mockImplementation(function (this: HTMLInputElement) {
    Object.defineProperty(this, "files", { value: [new File(["png"], "logo.png", { type: "image/png" })] });
    this.onchange?.(new Event("change"));
  });
  await wrapper.get("[data-contact-photo-upload]").trigger("click");
  await vi.waitFor(() => expect(wrapper!.emitted("change")).toEqual([[{ photo }]]));
  chooser.mockRestore();
  await wrapper.setProps({ contact: { ...jana, photo } });
  await wrapper.get("[data-contact-photo-remove]").trigger("click");
  expect(wrapper.emitted("change")![1]).toEqual([{ photo: null }]);

  // A holder that cannot upload (the overview, a read-only detail) offers nothing.
  wrapper.unmount();
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: jana, editable: true } });
  await wrapper.get("[data-contact-reveal]").trigger("click");
  expect(wrapper.find("[data-contact-photo-upload]").exists()).toBe(false);
});

it("opens a field that says it is a button with Space, without scrolling the page (review 3.5.1, finding 13)", async () => {
  wrapper = mount(ContactCard, { attachTo: document.body, props: { contact: jana, editable: true } });
  const name = wrapper.get('[data-contact-field="name"]');
  expect(name.attributes("role")).toBe("button");
  const event = new KeyboardEvent("keydown", { key: " ", bubbles: true, cancelable: true });
  name.element.dispatchEvent(event);
  await wrapper.vm.$nextTick();
  expect(event.defaultPrevented).toBe(true);
  expect(name.classes()).toContain("is-editing");
});
