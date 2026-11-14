import { afterEach, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import type { Editor } from "@tiptap/core";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { emptyDocument } from "../document";
import DocumentEditor from "./DocumentEditor.vue";

// 3.5.2 had „Kde se to ještě zmiňuje“ and „Kopírovat odkaz na sekci“ on a selection in reading; 3.6 edits always, so the live
// editor's „Co s tím jde dělat“ (the menu key, Shift+F10, right button) and the palette (selectionActions) offer them too.
vi.mock("../pageSource", async original => ({ ...await original<object>(), prefetchPageSource: vi.fn() }));

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); vi.restoreAllMocks(); });

const value = (): MarkdownRichValue => ({ ...emptyDocument(), plugins: [{ id: "heading-anchors", version: 1 }], document: { type: "doc", content: [
  { type: "heading", attrs: { level: 2, headingId: "provoz" }, content: [{ type: "text", text: "Provoz" }] },
  { type: "paragraph", content: [{ type: "text", text: "Záloha běží v noci." }] },
] } });
const menuRows = () => [...document.querySelectorAll("[data-testid=intentions] [role=option]")].map(row => row.querySelector(".t")?.textContent ?? "");

it("offers both selection actions in the live editor's menu by keyboard and in the palette, and runs them", async () => {
  const writeText = vi.fn(async () => {});
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
  wrapper = mount(DocumentEditor, { attachTo: document.body, props: {
    value: value(), readonly: false, dark: false, accent: "blue" as const, spaceId: "space", pageId: "page", documentTitle: "Runbook", canEdit: true,
  } });
  await vi.waitFor(() => expect(wrapper!.emitted("ready")).toBeTruthy(), { timeout: 30_000 });
  const element = await vi.waitFor(() => { const found = document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror"); expect(found?.editor).toBeTruthy(); return found!; }, { timeout: 30_000 });
  const editor = element.editor!;
  let from = 0;
  editor.state.doc.descendants((node, pos) => { if (node.isText && node.text?.startsWith("Záloha")) from = pos; });
  editor.commands.focus();
  editor.commands.setTextSelection({ from, to: from + "Záloha".length });

  editor.view.dom.dispatchEvent(new KeyboardEvent("keydown", { key: "ContextMenu", bubbles: true, cancelable: true }));
  await vi.waitFor(() => expect(menuRows()).toEqual(expect.arrayContaining(["Kde se to ještě zmiňuje", "Kopírovat odkaz na sekci"])));
  // The keyboard stays in the text: arrows to the row (the first is preselected), Enter runs it.
  const key = (name: string) => editor.view.dom.dispatchEvent(new KeyboardEvent("keydown", { key: name, bubbles: true, cancelable: true }));
  for (let index = menuRows().indexOf("Kde se to ještě zmiňuje"); index > 0; index--) key("ArrowDown");
  key("Enter");
  await vi.waitFor(() => expect(wrapper!.emitted("findSelection")).toEqual([["Záloha"]]));

  // The palette asks the same actions for the selection it opened over.
  const actions = (wrapper.vm as unknown as { selectionActions(): { label: string; run(): unknown }[] }).selectionActions();
  expect(actions.map(action => action.label)).toEqual(["Kde se to ještě zmiňuje", "Kopírovat odkaz na sekci"]);
  await actions[1]!.run();
  expect(writeText).toHaveBeenCalledWith(expect.stringContaining("#/pages/page?heading=provoz"));
});

it("gives the palette the same actions over the DOM selection of a read-only editor", async () => {
  wrapper = mount(DocumentEditor, { attachTo: document.body, props: {
    value: value(), readonly: true, dark: false, accent: "blue" as const, spaceId: "space", pageId: "page", documentTitle: "Runbook", canEdit: true,
  } });
  const paragraph = await vi.waitFor(() => { const found = document.querySelector<HTMLElement>(".ProseMirror p"); expect(found).toBeTruthy(); return found!; }, { timeout: 30_000 });
  document.getSelection()!.selectAllChildren(paragraph);
  const actions = (wrapper.vm as unknown as { selectionActions(): { label: string; run(): unknown }[] }).selectionActions();
  expect(actions.map(action => action.label)).toEqual(["Kde se to ještě zmiňuje", "Kopírovat odkaz na sekci"]);
  actions[0]!.run();
  expect(wrapper.emitted("findSelection")).toEqual([["Záloha běží v noci."]]);
});
