import { afterEach, expect, it, vi } from "vitest";
import { createMarkdownEditorController, type MarkdownEditorController } from "@atlas/markdown/core";
import { createContactsPlugin } from "@atlas/markdown/plugins/contacts";
import type { Editor } from "@tiptap/core";

// `/kontakt jan kovarik`: the name typed after the command is the people picker's first search.
const opened = vi.hoisted(() => vi.fn());
vi.mock("../projector/openVuePopover", () => ({ openVuePopover: (options: unknown) => { opened(options); return () => {}; } }));
const warning = vi.hoisted(() => vi.fn());
vi.mock("../composables/useToast", () => ({ useToast: () => ({ warning }) }));
const { pickContact, withContactPicker } = await import("./contactSlash");
const { linkContact } = await import("./contactLinking");

const controllers: MarkdownEditorController[] = [];
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); opened.mockReset(); });

function mount(text = "") {
  const controller = createMarkdownEditorController({ profile: "rich", plugins: [withContactPicker(createContactsPlugin(), async () => [])],
    source: { kind: "local", value: { format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", ...(text ? { content: [{ type: "text", text }] } : {}) }] } } } });
  controllers.push(controller);
  const host = document.createElement("div"); document.body.append(host);
  controller.attachView(host, { ariaLabel: "Editor" });
  controller.focus();
  return controller;
}

it("gives „Kontakt“ the typed name and the in-text insert none", () => {
  const actions = mount().actions.slash;
  expect(actions.find(action => action.id === "contacts.insert")?.parameter?.label).toBe("Kontakt");
  expect(actions.find(action => action.id === "contact-inline.insert")?.parameter).toBeUndefined();
});

it("opens the picker with the typed name as its search, a card on an empty line and in the text after words", async () => {
  for (const [line, inline] of [["", false], ["Volejte ", true]] as const) {
    const controller = mount(line);
    const parameter = controller.actions.slash.find(action => action.id === "contacts.insert")!.parameter!;
    expect(controller.dispatch(parameter.command("jan kovarik")!)).toBe(true);
    await vi.waitFor(() => expect(opened).toHaveBeenCalledOnce());
    const options = opened.mock.calls[0]![0] as { title: string; props: { typeAhead: { take(): { text: string } } } };
    expect(options.props.typeAhead.take().text).toBe("jan kovarik");
    expect(options.title).toBe(inline ? "Kontakt v textu" : "Kontakt");
    opened.mockReset();
  }
});

// PR-1 (UX cycle 1): a card picked on an empty line was left selected, so the next keys went to its toolbar and the app's letter keys.
it("leaves the caret on a line after a picked card, so typing goes on", async () => {
  const controller = mount();
  controller.dispatch(controller.actions.slash.find(action => action.id === "contacts.insert")!.parameter!.command("jan")!);
  await vi.waitFor(() => expect(opened).toHaveBeenCalledOnce());
  const { editor, props } = opened.mock.calls[0]![0] as { editor: import("@tiptap/core").Editor; props: { pick(contact: { name: string }): void } };
  props.pick({ name: "Honza Kovařík" });
  const { $from } = editor.state.selection;
  expect(editor.state.doc.firstChild?.type.name).toBe("contactCard");
  expect($from.parent.isTextblock && $from.index(0)).toBe(1);
});

// Spec 2026-10-30 §7: the picker links every person to the directory, and a contact the page has already is said, never refused.
it("links the pick through the directory and warns of a contact the page has already", async () => {
  const controller = mount();
  controller.dispatch(controller.actions.slash.find(action => action.id === "contacts.insert")!.parameter!.command("jan")!);
  await vi.waitFor(() => expect(opened).toHaveBeenCalledOnce());
  const { props } = opened.mock.calls[0]![0] as { props: { link: unknown; pick(contact: { name: string; objectId?: string }): void } };
  expect(props.link).toBe(linkContact);
  const id = "0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b";
  props.pick({ name: "Honza Kovařík", objectId: id });
  expect(warning).not.toHaveBeenCalled();
  props.pick({ name: "Honza Kovařík", objectId: id });
  expect(warning).toHaveBeenCalledWith("Kontakt už tu je – vložen podruhé.");
});

// „Udělat kontakt“ over a selected name: the plugin's own `pick` opens the same picker with the name as its search.
it("picks for the plugin with the selected name as the search and answers the person, or null when the picker closes", async () => {
  mount("Jana Nováková");
  const editor = document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")!.editor!;
  const pick = pickContact(async () => []);
  const request = (query: string) => ({ editor, query, inline: true, signal: new AbortController().signal, anchor: () => new DOMRect() });
  const asked = pick(request("Jana Nováková"));
  const options = opened.mock.calls[0]![0] as { closed(): void; props: { typeAhead: { take(): { text: string; enter: boolean } }; link: unknown; pick(contact: { name: string }): void } };
  expect(options.props.typeAhead.take()).toEqual({ text: "Jana Nováková", enter: false });
  expect(options.props.link).toBe(linkContact);
  options.props.pick({ name: "Jana Nováková" });
  options.closed();
  await expect(asked).resolves.toEqual({ name: "Jana Nováková" });
  const none = pick(request("x"));
  (opened.mock.calls[1]![0] as { closed(): void }).closed();
  await expect(none).resolves.toBeNull();
});
