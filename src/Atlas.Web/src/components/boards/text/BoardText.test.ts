import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { Editor } from "@tiptap/core";
import { registerMarkdownOverlayForElement } from "@atlas/markdown/core";
// The chained commands of the editor's starter kit (bold, headings) in the types.
import type {} from "@tiptap/starter-kit";
import BoardMarkdown from "./BoardMarkdown.vue";
import BoardTextEditor from "./BoardTextEditor.vue";
import type { TextBinding } from "./textBinding";
import { drafts } from "../../../lib/unsavedWork";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

describe("BoardMarkdown", () => {
  it("renders the Markdown of a note with the editor's static renderer, and no HTML from the input", () => {
    wrapper = mount(BoardMarkdown, { props: { text: "# Plán\n\n**Tučně** a _kurzíva_\n\n- [ ] úkol\n\n<img src=x onerror=alert(1)>" }, attachTo: document.body });
    expect(wrapper.get("h1").text()).toBe("Plán");
    expect(wrapper.get("strong").text()).toBe("Tučně");
    expect(wrapper.find("em").text()).toBe("kurzíva");
    expect(wrapper.find("ul[data-type=taskList], ul.tt-task-list").exists()).toBe(true);
    expect(wrapper.find("img").exists()).toBe(false);
    expect(wrapper.classes()).toEqual(expect.arrayContaining(["atlas-document-editor", "board-text"]));
  });

  // W2-9: an escaped star is one the person meant (the editor escapes a literal star), never a mark.
  it("keeps intentionally escaped asterisks literal", () => {
    wrapper = mount(BoardMarkdown, { props: { text: "\\*\\*nejde o tučné\\*\\* a \\*poznámka\\* · cena 5 \\* 3" }, attachTo: document.body });
    expect(wrapper.find("strong").exists()).toBe(false);
    expect(wrapper.find("em").exists()).toBe(false);
    expect(wrapper.text()).toContain("**nejde o tučné** a *poznámka* · cena 5 * 3");
  });

  it("reads the Markdown as a page does, the editor's dialect included, so the text looks as it does while edited", () => {
    wrapper = mount(BoardMarkdown, { props: { text: "Je to ==důležité==" }, attachTo: document.body });
    expect(wrapper.get("mark").text()).toBe("důležité");
  });

  it("keeps http(s), mailto and Atlas links and turns every other link into its text", () => {
    wrapper = mount(BoardMarkdown, { props: { text: "[web](https://example.cz) [mail](mailto:a@example.cz) [stránka](#/pages/1) [zlé](javascript:alert(1)) [tel](tel:+420777)" }, attachTo: document.body });
    expect(wrapper.findAll("a").map(link => link.attributes("href"))).toEqual(["https://example.cz", "mailto:a@example.cz", "#/pages/1"]);
    expect(wrapper.text()).toContain("tel");
  });

  it("renders an inline text as one line: a start that would open a block stays text", () => {
    wrapper = mount(BoardMarkdown, { props: { text: "# Fáze **1**\n- druhý řádek", inline: true }, attachTo: document.body });
    expect(wrapper.find("h1").exists()).toBe(false);
    expect(wrapper.find("ul").exists()).toBe(false);
    expect(wrapper.text()).toBe("# Fáze 1 - druhý řádek");
    expect(wrapper.get("strong").text()).toBe("1");
    expect(wrapper.classes()).toContain("is-inline");
  });
});

describe("BoardMarkdown, a plain text without the renderer (BD2-1)", () => {
  // The fast path draws the DOM the static renderer draws for one plain paragraph; a difference would show as the text jumping on editing.
  it.each(["Poznámka", "Vlastník: Provoz", "Verze 1.2 hotova.", "Co dál? (nejspíš ano)", "Text 1-2", "Praha-Západ", "Fáze - hotovo", "A/B test", "Tom & Jerry", "Vstup + výstup", "Dnes — zítra…", "ATL-123 hotovo"])("draws %s as the renderer does", async text => {
    wrapper = mount(BoardMarkdown, { props: { text }, attachTo: document.body });
    const fast = wrapper.get(".ProseMirror").element.innerHTML;
    wrapper.unmount();
    wrapper = mount(BoardMarkdown, { props: { text: text + "​" }, attachTo: document.body });
    await flushPromises();
    expect(wrapper.get(".ProseMirror").element.innerHTML.replace("​", "")).toBe(fast);
  });

  it("keeps the renderer’s frame: classes, role and name", () => {
    wrapper = mount(BoardMarkdown, { props: { text: "Rám", inline: true, ariaLabel: "Název rámu" }, attachTo: document.body });
    expect(wrapper.classes()).toEqual(expect.arrayContaining(["md-editor", "readonly", "md-renderer", "paper", "atlas-document-editor", "board-text", "is-inline"]));
    expect(wrapper.attributes("role")).toBe("document");
    expect(wrapper.attributes("aria-label")).toBe("Název rámu");
  });
});

describe("BoardTextEditor", () => {
  function binding(text: string) {
    const commit = vi.fn();
    return { binding: { read: () => text, commit } satisfies TextBinding, commit };
  }
  async function editing(text: string, props: Record<string, unknown> = {}) {
    const bound = binding(text);
    wrapper = mount(BoardTextEditor, { props: { binding: bound.binding, ...props }, attachTo: document.body });
    await vi.waitFor(() => expect(document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor).toBeDefined());
    await flushPromises();
    const editor = document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")!.editor!;
    return { ...bound, editor };
  }
  // With the key codes of a real key: ProseMirror takes (prevents) every Enter and Esc by its key code.
  const keyCodes: Record<string, number> = { Enter: 13, Escape: 27 };
  const press = (key: string) => document.querySelector(".ProseMirror")!.dispatchEvent(new KeyboardEvent("keydown", { key, keyCode: keyCodes[key], bubbles: true, cancelable: true }));
  /** Text input as the browser reports it to ProseMirror: each piece through the input rules, else inserted as it is. */
  function input(editor: Editor, ...pieces: string[]) {
    const view = editor.view;
    for (const text of pieces) {
      const { from, to } = view.state.selection;
      const insert = () => view.state.tr.insertText(text, from, to);
      if (!view.someProp("handleTextInput", handle => handle(view, from, to, text, insert))) view.dispatch(insert());
    }
  }
  const typed = (editor: Editor, text: string) => input(editor, ...Array.from(text));

  it("types Markdown as a page does: **, _, a list and a heading", async () => {
    const { editor, commit } = await editing("");
    typed(editor, "# Plán na **listopad** a _prosinec_");
    editor.commands.splitBlock();
    typed(editor, "- úkol");
    expect(editor.getJSON().content).toMatchObject([
      { type: "heading", attrs: { level: 1 }, content: [
        { type: "text", text: "Plán na " }, { type: "text", text: "listopad", marks: [{ type: "bold" }] }, { type: "text", text: " a " },
        { type: "text", text: "prosinec", marks: [{ type: "italic" }] },
      ] },
      { type: "bulletList", content: [{ type: "listItem", content: [{ type: "paragraph", content: [{ type: "text", text: "úkol" }] }] }] },
      { type: "paragraph" },
    ]);
    press("Escape");
    expect(commit).toHaveBeenCalledWith("# Plán na **listopad** a *prosinec*\n\n- úkol");
  });

  it("offers a one-line text only the marks it keeps, so the selection bubble stays small over what is around it", async () => {
    await editing("Petr Svoboda", { inline: true });
    expect(wrapper!.findComponent({ name: "MarkdownEditor" }).props("formatting")).toEqual(["bold", "italic", "strike", "code", "link"]);
    wrapper!.unmount(); wrapper = undefined; document.body.replaceChildren();
    await editing("Poznámka");
    expect(wrapper!.findComponent({ name: "MarkdownEditor" }).props("formatting")).toBeUndefined();
  });
  it("starts with the caret at the end of the text, where typing continues it", async () => {
    const { editor, commit } = await editing("Plán");
    typed(editor, " B");
    press("Escape");
    expect(commit).toHaveBeenCalledWith("Plán B");
  });

  it("offers page links on [[ as a page does, from the board's host", async () => {
    const search = vi.fn(async () => [{ id: "p1", title: "Plán", url: "#/pages/p1" }]);
    const { editor } = await editing("", { host: { youTrack: { search: async () => [], resolve: async () => null }, pages: { search } } });
    typed(editor, "[[Pl");
    await vi.waitFor(() => expect(search).toHaveBeenCalledWith("Pl", expect.anything()));
  });

  it("takes several characters at once (IME, autocorrect, dictation) as if typed one by one, without the input rule crash", async () => {
    const { editor } = await editing("");
    expect(() => input(editor, "Plán na **listopad**")).not.toThrow();
    expect(editor.getJSON().content![0]!.content).toEqual([{ type: "text", text: "Plán na " }, { type: "text", text: "listopad", marks: [{ type: "bold" }] }]);
  });

  it("reads the stored Markdown as a page reads it, the editor's dialect included, and commits nothing when nothing changed", async () => {
    const stored = "# Plán\n\n**Tučně**, ==zvýrazněno== a _kurzíva_\n\n- [ ] úkol";
    const { editor, commit } = await editing(stored);
    expect(editor.getHTML()).toContain("<mark");
    expect(editor.getHTML()).toContain("<h1");
    press("Escape");
    expect(commit).not.toHaveBeenCalled();
    expect(wrapper!.emitted("cancel")).toHaveLength(1);
  });

  it("lets an open popup of the editor take Esc first, then Esc commits", async () => {
    const { editor, commit } = await editing("A");
    editor.chain().focus("end").insertContent("B").run();
    await flushPromises();
    const popup = document.body.appendChild(document.createElement("div"));
    const release = registerMarkdownOverlayForElement(document.querySelector<HTMLElement>(".ProseMirror")!, popup);
    press("Escape");
    expect(commit).not.toHaveBeenCalled();
    release();
    press("Escape");
    expect(commit).toHaveBeenCalledWith("AB");
  });

  it("commits a connector's label or a frame's title on Esc too", async () => {
    const { editor, commit } = await editing("Popisek", { inline: true });
    editor.chain().focus("end").insertContent(" 2").run();
    await flushPromises();
    press("Escape");
    expect(commit).toHaveBeenCalledWith("Popisek 2");
  });

  it("edits the text with the page editor, and commits the Markdown on Esc", async () => {
    const { editor, commit } = await editing("Ahoj");
    expect(wrapper!.find(".md-editor.atlas-document-editor.board-text:not(.md-renderer)").exists()).toBe(true);
    expect(document.querySelector(".ProseMirror")!.getAttribute("contenteditable")).toBe("true");
    expect(editor.getText()).toBe("Ahoj");
    editor.chain().selectAll().toggleBold().run();
    await flushPromises();
    press("Escape");
    expect(commit).toHaveBeenCalledWith("**Ahoj**");
    expect(wrapper!.emitted("commit")).toEqual([["**Ahoj**"]]);
  });

  it("cancels when nothing was edited, also when the editor writes the text differently", async () => {
    const { commit } = await editing("*Ahoj*");
    press("Escape");
    expect(commit).not.toHaveBeenCalled();
    expect(wrapper!.emitted("cancel")).toHaveLength(1);
    press("Escape");
    expect(wrapper!.emitted("cancel")).toHaveLength(1);
  });

  it("ends the editing on a click outside, cleaned to what the server takes", async () => {
    const { editor, commit } = await editing("Text");
    editor.chain().focus("end").insertContent("‮konec").run();
    await flushPromises();
    document.body.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
    expect(commit).toHaveBeenCalledWith("Textkonec");
  });

  it("keeps an inline text on one line and ends it with Enter", async () => {
    const { editor, commit } = await editing("Popisek", { inline: true });
    editor.chain().selectAll().setHeading({ level: 2 }).toggleItalic().run();
    await flushPromises();
    press("Enter");
    expect(commit).toHaveBeenCalledWith("*Popisek*");
  });

  // UX3-2: a note's text has its type's limit, not a board item's 4000, and a text over its limit is never cut silently.
  describe("the length limit", () => {
    it("commits a note's text past a board item's 4000 characters whole", async () => {
      const { editor, commit } = await editing("", { max: 100_000 });
      editor.commands.insertContent("x".repeat(4001));
      await flushPromises();
      press("Escape");
      expect(commit).toHaveBeenCalledExactlyOnceWith("x".repeat(4001));
    });

    it("commits a text of exactly its limit, 100 000 characters, without a warning", async () => {
      const { editor, commit } = await editing("", { max: 100_000 });
      editor.commands.insertContent("x".repeat(100_000));
      await flushPromises();
      press("Escape");
      expect(commit.mock.calls[0]![0]).toHaveLength(100_000);
      expect(document.querySelector("[data-board-text-limit]")).toBeNull();
    });

    it("says by how much a text is over its limit and stays open on Esc and a click outside until it is shortened", async () => {
      const { editor, commit } = await editing("", { max: 100_000 });
      editor.commands.insertContent("x".repeat(100_002));
      await flushPromises();
      press("Escape");
      document.body.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
      expect(commit).not.toHaveBeenCalled();
      expect(document.querySelector(".ProseMirror")).not.toBeNull();
      await flushPromises();
      expect(document.querySelector("[data-board-text-limit]")?.textContent?.replace(/\s/g, " ")).toBe("Text je o 2 znaky delší, než se uloží (nejvýše 100 000). Zkraťte ho.");
      expect(document.querySelector("[data-board-text-limit]")?.getAttribute("role")).toBe("alert");
      editor.chain().focus("end").deleteRange({ from: editor.state.doc.content.size - 3, to: editor.state.doc.content.size - 1 }).run();
      await flushPromises();
      press("Escape");
      expect(commit.mock.calls[0]![0]).toHaveLength(100_000);
    });

    it("keeps a board item's own 4000 characters when no limit is given", async () => {
      const { editor, commit } = await editing("");
      editor.commands.insertContent("x".repeat(4001));
      await flushPromises();
      press("Escape");
      expect(commit).not.toHaveBeenCalled();
      await flushPromises();
      expect(document.querySelector("[data-board-text-limit]")?.textContent).toContain("o 1 znak delší");
    });
  });

  // Review 3.5.1 finding 1: an editor taken away over the limit never saves the text cut; the full text waits as a draft.
  describe("taken away over the limit", () => {
    afterEach(() => { drafts.clear(); });
    it.each([["unmounted", () => wrapper!.unmount()], ["pagehide of a binding without save", () => window.dispatchEvent(new PageTransitionEvent("pagehide"))]])(
      "saves nothing cut and keeps the full draft when %s, and shows it again in the next editor", async (_name, leave) => {
        const { editor, commit } = await editing("A", { draftKey: "k" });
        editor.commands.insertContent("x".repeat(4001));
        await flushPromises();
        leave();
        wrapper?.unmount(); wrapper = undefined;
        expect(commit).not.toHaveBeenCalled();
        expect((drafts.get("k") as string).length).toBe(4002);
        const again = await editing("A", { draftKey: "k" });
        await vi.waitFor(() => expect(document.querySelector("[data-board-text-limit]")?.textContent).toContain("o 2 znaky delší"));
        again.editor.commands.setContent("Kratší");
        await flushPromises();
        press("Escape");
        expect(again.commit).toHaveBeenCalledExactlyOnceWith("Kratší");
        expect(drafts.has("k")).toBe(false);
      });
    it("still saves an in-limit text when taken away", async () => {
      const { editor, commit } = await editing("A", { draftKey: "k" });
      editor.commands.insertContent("B");
      await flushPromises();
      wrapper!.unmount(); wrapper = undefined;
      expect(commit).toHaveBeenCalledExactlyOnceWith("AB");
      expect(drafts.has("k")).toBe(false);
    });
  });

  // U3-1: a text being edited reaches the board on a pause and when the page is hidden (reload, closing the tab), not only on Esc.
  describe("saving while editing", () => {
    afterEach(() => { vi.useRealTimers(); });
    async function autosaving(text: string) {
      const save = vi.fn();
      const commit = vi.fn();
      wrapper = mount(BoardTextEditor, { props: { binding: { read: () => text, commit, save } satisfies TextBinding }, attachTo: document.body });
      await vi.waitFor(() => expect(document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor).toBeDefined());
      await flushPromises();
      return { save, commit, editor: document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")!.editor! };
    }

    it("saves the text typed so far after a pause and keeps editing; it holds the text as unsaved until then", async () => {
      const { editor, save, commit } = await autosaving("Plán");
      vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
      typed(editor, " B");
      await flushPromises();
      expect(wrapper!.emitted("unsaved")?.at(-1)).toEqual([true]);
      expect(save).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1000);
      expect(save).toHaveBeenCalledExactlyOnceWith("Plán B");
      expect(wrapper!.emitted("unsaved")?.at(-1)).toEqual([false]);
      expect(document.querySelector(".ProseMirror")).not.toBeNull();
      // Esc after the save: nothing new to send, the editing ends.
      press("Escape");
      expect(commit).not.toHaveBeenCalled();
      expect(save).toHaveBeenCalledOnce();
      expect(wrapper!.emitted("commit")).toEqual([["Plán B"]]);
    });

    it("saves nothing while the text is over its limit, so the board keeps saying it is unsaved", async () => {
      const { editor, save } = await autosaving("");
      vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
      editor.commands.insertContent("x".repeat(4001));
      await flushPromises();
      vi.advanceTimersByTime(1000);
      expect(save).not.toHaveBeenCalled();
      expect(wrapper!.emitted("unsaved")?.at(-1)).toEqual([true]);
    });

    it("saves at once when the page is hidden or left", async () => {
      const { editor, save } = await autosaving("A");
      typed(editor, "B");
      await flushPromises();
      window.dispatchEvent(new PageTransitionEvent("pagehide"));
      expect(save).toHaveBeenCalledExactlyOnceWith("AB");
      typed(editor, "C");
      await flushPromises();
      Object.defineProperty(document, "visibilityState", { configurable: true, get: () => "hidden" });
      try { document.dispatchEvent(new Event("visibilitychange")); } finally { delete (document as { visibilityState?: unknown }).visibilityState; }
      expect(save).toHaveBeenLastCalledWith("ABC");
    });

    // COL2-9: a note was last-writer-wins for its whole text; a colleague's text that arrives meanwhile is merged, never overwritten.
    async function shared(text: string) {
      const stored = { text };
      const save = vi.fn((markdown: string) => { stored.text = markdown; });
      const commit = vi.fn((markdown: string) => { stored.text = markdown; });
      wrapper = mount(BoardTextEditor, { props: { binding: { read: () => stored.text, commit, save } satisfies TextBinding }, attachTo: document.body });
      await vi.waitFor(() => expect(document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor).toBeDefined());
      await flushPromises();
      return { stored, save, commit, editor: document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")!.editor! };
    }

    it("merges a colleague's edit of another place into what it saves, and shows it", async () => {
      const { stored, editor, save, commit } = await shared("Koupit mléko.");
      vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
      typed(editor, " A chléb.");
      stored.text = "Dnes: Koupit mléko.";
      await flushPromises();
      vi.advanceTimersByTime(1000);
      expect(save).toHaveBeenCalledExactlyOnceWith("Dnes: Koupit mléko. A chléb.");
      await flushPromises();
      expect(editor.getText()).toBe("Dnes: Koupit mléko. A chléb.");
      expect(document.querySelector("[data-board-text-conflict]")).toBeNull();
      // Its own save echoing back is no colleague's change.
      typed(editor, "!");
      await flushPromises();
      press("Escape");
      expect(commit).toHaveBeenCalledExactlyOnceWith("Dnes: Koupit mléko. A chléb.!");
    });

    it("keeps both versions of a place both changed, says so, and ends only on the next Esc", async () => {
      const { stored, editor, save, commit } = await shared("Koupit mléko");
      typed(editor, " a sýr");
      await flushPromises();
      stored.text = "Koupit mléko a máslo";
      press("Escape");
      await flushPromises();
      expect(save).toHaveBeenCalledExactlyOnceWith("Koupit mléko a máslo\n\na sýr");
      expect(document.querySelector("[data-board-text-conflict]")?.textContent).toContain("Obě verze");
      expect(wrapper!.emitted("commit")).toBeUndefined();
      expect(document.querySelector(".ProseMirror")).not.toBeNull();
      press("Escape");
      expect(commit).not.toHaveBeenCalled();
      expect(wrapper!.emitted("commit")).toEqual([["Koupit mléko a máslo\n\na sýr"]]);
    });

    it("ends the editing of a binding that does not save on the way, when the page is left", async () => {
      const { editor, commit } = await editing("A");
      typed(editor, "B");
      await flushPromises();
      window.dispatchEvent(new PageTransitionEvent("pagehide"));
      expect(commit).toHaveBeenCalledWith("AB");
    });
  });

  it("hands what was typed to the binding when it goes away mid-edit", async () => {
    const { editor, commit } = await editing("A");
    editor.chain().focus("end").insertContent("B").run();
    await flushPromises();
    wrapper!.unmount();
    wrapper = undefined;
    expect(commit).toHaveBeenCalledWith("AB");
  });
});
