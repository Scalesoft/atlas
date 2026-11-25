import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { emptyDocument } from "../document";
import { prefetchPageSource } from "../pageSource";
import type { PageSourceDraftOptions } from "../pageSourceDraft";
import DocumentEditor from "./DocumentEditor.vue";

// The editor's source draft, panel and the unsaved-changes question are replaced by small fakes, so Monaco never loads. The editor is real.
const fake = vi.hoisted(() => ({ options: undefined as unknown, draft: undefined as unknown }));
vi.mock("../pageSource", async original => ({ ...await original<object>(), prefetchPageSource: vi.fn() }));
vi.mock("../pageSourceDraft", async original => {
  const actual = await original<typeof import("../pageSourceDraft")>();
  const { computed: vueComputed, defineComponent: component, h: render, ref: vueRef } = await import("vue");
  return {
    ...actual,
    usePageSourceDraft(options: unknown) {
      const text = vueRef(""), base = vueRef(""), busy = vueRef(false);
      const draft = {
        text, base, busy, dirty: vueComputed(() => text.value !== base.value),
        apply: vi.fn(async () => true), discard: vi.fn(() => { text.value = base.value; }), close: vi.fn(),
      };
      fake.options = options; fake.draft = draft;
      return draft;
    },
    MarkdownSourcePanel: component({ props: ["draft"], setup: () => () => render("div", { class: "fake-source-panel" }, "Zdroj") }),
  };
});
// The shared unsaved-changes question, as a fake: its own behaviour is UnsavedChangesDialog.test.ts.
vi.mock("./UnsavedChangesDialog.vue", async () => {
  const { defineComponent: component, h: render } = await import("vue");
  return { default: component({
    props: ["open", "canSave", "saving", "message", "saveLabel"], emits: ["stay", "discard", "save"],
    setup: (props, { emit }) => () => props.open ? render("div", { class: "fake-confirmation", "data-apply": props.canSave ? props.saveLabel : "" }, [
      render("button", { class: "fake-continue", onClick: () => emit("stay") }, "Zůstat"),
      render("button", { class: "fake-discard", onClick: () => emit("discard") }, "Zahodit"),
      render("button", { class: "fake-apply", onClick: () => emit("save") }, "Použít"),
    ]) : null,
  }) };
});
type FakeDraft = { text: { value: string }; base: { value: string }; busy: { value: boolean }; apply: ReturnType<typeof vi.fn>; discard: ReturnType<typeof vi.fn>; close: ReturnType<typeof vi.fn> };
const draft = () => fake.draft as FakeDraft;
const options = () => fake.options as PageSourceDraftOptions;
type Exposed = { openSource(): boolean; resolveSourceDraft(): Promise<boolean>; discardSourceDraft(): void };

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.mocked(prefetchPageSource).mockClear(); document.body.replaceChildren(); });

async function editor(props: Record<string, unknown> = {}) {
  wrapper = mount(DocumentEditor, {
    props: { value: emptyDocument(), readonly: false, dark: false, accent: "blue", spaceId: "space", pageId: "page", documentTitle: "Runbook",
      canEdit: true, canEditSource: true, saveSource: vi.fn(async () => ({ ok: true as const })), ...props },
    attachTo: document.body,
  });
  await vi.waitFor(() => expect(wrapper!.emitted("ready")).toBeTruthy());
  return wrapper;
}
const exposed = (view: VueWrapper) => view.vm as unknown as Exposed;
const switchButtons = (view: VueWrapper) => view.findAll(".source-switch button");
const sourceShown = (view: VueWrapper) => view.find(".source-pane").exists();

describe("page source tab in the page editor", () => {
  it("opens only for source editors while editing, through the Dokument | Zdroj switch, and warms Monaco on intent", async () => {
    // The switch stands wherever the page can be written (3.6-A: attaching the editor must not push the page down); the tab
    // itself opens only over the attached editor, which the host attaches first.
    for (const props of [{ canEditSource: false }, { canEdit: false }, { readonly: true }]) {
      const denied = await editor(props);
      expect(denied.find(".source-switch").exists()).toBe("readonly" in props || "canEdit" in props);
      // A locked page keeps the switch's place invisibly (J7.1) and takes no input.
      if ("canEdit" in props) expect(denied.find(".source-switch").classes()).toContain("is-locked");
      expect(exposed(denied).openSource()).toBe(false);
      await flushPromises();
      expect(sourceShown(denied)).toBe(false);
      denied.unmount(); wrapper = undefined;
    }

    const view = await editor();
    expect(view.find(".fake-source-panel").exists()).toBe(false);
    // „Zdroj“ asks the host, which flushes and opens.
    const [documentButton, sourceButton] = switchButtons(view);
    expect(documentButton!.attributes("aria-pressed")).toBe("true");
    await sourceButton!.trigger("pointerenter"); await sourceButton!.trigger("focus");
    expect(prefetchPageSource).toHaveBeenCalledTimes(2);
    await sourceButton!.trigger("click");
    expect(view.emitted("requestSource")).toHaveLength(1);
    expect(sourceShown(view)).toBe(false);
    expect(exposed(view).openSource()).toBe(true);
    await flushPromises();
    // The same switch stays in place and marks Zdroj; the document stays mounted but hidden.
    expect(sourceShown(view)).toBe(true);
    expect(switchButtons(view).map(button => button.attributes("aria-pressed"))).toEqual(["false", "true"]);
    expect(view.find(".fake-source-panel").exists()).toBe(true);
    expect((view.get(".document-pane").element as HTMLElement).style.display).toBe("none");
    expect(view.findComponent({ name: "MarkdownEditor" }).exists()).toBe(true);
    expect(options().value()).toEqual(emptyDocument());
    // Zdroj again does not ask the host a second time.
    await switchButtons(view)[1]!.trigger("click");
    expect(view.emitted("requestSource")).toHaveLength(1);
  });

  it("switches back to the document at once when the draft is clean, to the same editor, with the caret in its text", async () => {
    const view = await editor();
    const text = view.get(".ProseMirror").element, controller = (view.getComponent({ name: "MarkdownEditor" }).vm as unknown as { controller: unknown }).controller;
    // The document rises in only when it returns from the source, never when the page opens.
    expect(view.get(".document-pane").classes()).not.toContain("atlas-view-enter");
    exposed(view).openSource(); await flushPromises();
    (switchButtons(view)[1]!.element as HTMLElement).focus();
    await switchButtons(view)[0]!.trigger("click"); await flushPromises();
    expect(sourceShown(view)).toBe(false);
    expect(draft().close).toHaveBeenCalledTimes(1);
    expect((view.get(".document-pane").element as HTMLElement).style.display).toBe("");
    expect(view.get(".document-pane").classes()).toContain("atlas-view-enter");
    expect(switchButtons(view).map(button => button.attributes("aria-pressed"))).toEqual(["true", "false"]);
    // Never remounted: the same element and controller, so the undo history and the selection are kept.
    expect(view.get(".ProseMirror").element).toBe(text);
    expect((view.getComponent({ name: "MarkdownEditor" }).vm as unknown as { controller: unknown }).controller).toBe(controller);
    // The editor takes focus in its next frame.
    await vi.waitFor(() => expect(document.activeElement).toBe(text));
  });

  it("leaves focus where the reader went meanwhile when the source closes", async () => {
    const view = await editor();
    const elsewhere = document.createElement("input"); document.body.append(elsewhere);
    exposed(view).openSource(); await flushPromises();
    elsewhere.focus();
    exposed(view).discardSourceDraft(); await flushPromises();
    expect(sourceShown(view)).toBe(false);
    expect(document.activeElement).toBe(elsewhere);
  });

  it("takes Esc in the source as „Dokument“: at once when clean, through the same question when dirty, never from a dialog or a handled key", async () => {
    const view = await editor();
    const escape = async (target: Element, prevented = false) => {
      const event = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
      if (prevented) event.preventDefault();
      target.dispatchEvent(event); await flushPromises();
      return event;
    };
    // Without the source the key is not taken: the host ends editing with it.
    expect((await escape(switchButtons(view)[0]!.element)).defaultPrevented).toBe(false);

    exposed(view).openSource(); await flushPromises();
    // The source editor used the key itself (its find box, a suggestion), or a dialog of the source has it.
    await escape(view.get(".fake-source-panel").element, true);
    const dialog = document.createElement("dialog"); view.get(".fake-source-panel").element.append(dialog);
    expect((await escape(dialog)).defaultPrevented).toBe(false);
    dialog.remove();
    expect(sourceShown(view)).toBe(true);
    const taken = await escape(view.get(".fake-source-panel").element);
    expect(taken.defaultPrevented).toBe(true);
    expect(sourceShown(view)).toBe(false);

    // Esc in the source editor's text with nothing of its own to dismiss arrives as the panel's `escape`.
    exposed(view).openSource(); await flushPromises();
    view.get(".fake-source-panel").element.dispatchEvent(new Event("escape")); await flushPromises();
    expect(sourceShown(view)).toBe(false);

    exposed(view).openSource(); await flushPromises();
    draft().text.value = "changed"; await flushPromises();
    // On the switch too, e.g. while the source editor is still loading.
    await escape(switchButtons(view)[1]!.element);
    expect(sourceShown(view)).toBe(true);
    expect(view.get(".fake-confirmation").attributes("data-apply")).toBe("Použít a zavřít");
    await view.get(".fake-discard").trigger("click"); await flushPromises();
    expect(sourceShown(view)).toBe(false);
  });

  it("gives the switch one thumb that is placed first and then follows the chosen view, on a switch that never moves", async () => {
    // jsdom has no layout: Dokument sits at 2 px (86 px wide), Zdroj at 90 px (55 px wide).
    const left = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetLeft");
    const width = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetWidth");
    const isSource = (element: HTMLElement) => element.textContent === "Zdroj";
    Object.defineProperty(HTMLElement.prototype, "offsetLeft", { configurable: true, get() { return isSource(this) ? 90 : 2; } });
    Object.defineProperty(HTMLElement.prototype, "offsetWidth", { configurable: true, get() { return this.tagName !== "BUTTON" ? 0 : isSource(this) ? 55 : 86; } });
    const frame = () => new Promise<void>(done => requestAnimationFrame(() => done()));
    try {
      const view = await editor();
      const track = view.get(".source-switch");
      const place = () => [(track.element as HTMLElement).style.getPropertyValue("--thumb-x"), (track.element as HTMLElement).style.getPropertyValue("--thumb-w")];
      await frame(); await frame(); await flushPromises();
      expect(track.attributes("data-segmented")).toBeDefined();
      expect(place()).toEqual(["2px", "86px"]);
      expect(track.attributes("data-thumb")).toBe("ready");
      exposed(view).openSource(); await flushPromises(); await frame();
      // The same element: the switch is not replaced by another one when the source opens.
      expect(view.get(".source-switch").element).toBe(track.element);
      expect(place()).toEqual(["90px", "55px"]);
      await switchButtons(view)[0]!.trigger("click"); await flushPromises(); await frame();
      expect(place()).toEqual(["2px", "86px"]);
    } finally {
      if (left) Object.defineProperty(HTMLElement.prototype, "offsetLeft", left); else delete (HTMLElement.prototype as { offsetLeft?: number }).offsetLeft;
      if (width) Object.defineProperty(HTMLElement.prototype, "offsetWidth", width); else delete (HTMLElement.prototype as { offsetWidth?: number }).offsetWidth;
    }
  });

  it("reports the draft to the host", async () => {
    const view = await editor({ readonly: false });
    exposed(view).openSource(); await flushPromises();
    draft().text.value = "# Upraveno"; await flushPromises();
    expect(view.emitted("sourceDraft")?.at(-1)).toEqual([true]);
    draft().text.value = ""; await flushPromises();
    expect(view.emitted("sourceDraft")?.at(-1)).toEqual([false]);
  });

  it("asks before closing a dirty draft and guards host operations with Discard / Continue", async () => {
    const view = await editor();
    exposed(view).openSource(); await flushPromises();
    draft().text.value = "changed"; await flushPromises();

    await switchButtons(view)[0]!.trigger("click"); await flushPromises();
    expect(view.get(".fake-confirmation").attributes("data-apply")).toBe("Použít a zavřít");
    await view.get(".fake-continue").trigger("click"); await flushPromises();
    expect(view.find(".fake-confirmation").exists()).toBe(false);
    expect(sourceShown(view)).toBe(true);

    const kept = exposed(view).resolveSourceDraft(); await flushPromises();
    expect(view.get(".fake-confirmation").attributes("data-apply")).toBe("");
    await view.get(".fake-continue").trigger("click");
    await expect(kept).resolves.toBe(false);

    const discarded = exposed(view).resolveSourceDraft(); await flushPromises();
    await view.get(".fake-discard").trigger("click");
    await expect(discarded).resolves.toBe(true);
    expect(draft().discard).toHaveBeenCalledTimes(1);
    await expect(exposed(view).resolveSourceDraft()).resolves.toBe(true);

    draft().text.value = "again"; await flushPromises();
    await switchButtons(view)[0]!.trigger("click"); await flushPromises();
    await view.get(".fake-apply").trigger("click"); await flushPromises();
    expect(draft().apply).toHaveBeenCalledTimes(1);
    expect(draft().close).toHaveBeenCalled();
    expect(sourceShown(view)).toBe(false);
  });

  it("saves with the Markdown projection and passes the host's outcome on", async () => {
    const saveSource = vi.fn()
      .mockResolvedValueOnce({ ok: false, reason: "conflict" })
      .mockResolvedValueOnce({ ok: false, reason: "live-edits" })
      .mockResolvedValueOnce({ ok: true });
    const view = await editor({ saveSource });
    exposed(view).openSource(); await flushPromises();
    const next: MarkdownRichValue = { format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: "Opraveno" }] }] } };
    const signal = new AbortController().signal;
    await expect(options().save(next, "## Opraveno", { signal })).resolves.toEqual({ ok: false, reason: "conflict", message: undefined });
    expect(saveSource).toHaveBeenLastCalledWith(next, expect.stringContaining("## Opraveno"), signal);
    await expect(options().save(next, "", { signal })).resolves.toEqual({ ok: false, reason: "live-edits", message: undefined });
    await expect(options().save(next, "", { signal })).resolves.toEqual({ ok: true });
  });

  it("gives the source the document's plugins, so password references read and save as ::secret{…} directives", async () => {
    const saveSource = vi.fn(async () => ({ ok: true as const }));
    const view = await editor({ saveSource });
    exposed(view).openSource(); await flushPromises();
    expect(options().plugins!().map(plugin => plugin.id)).toContain("secrets");
    const secretId = "d847b3af-4a87-4e97-aab8-a74a4cf48c89";
    const next: MarkdownRichValue = {
      format: "atlas-rich", version: 1, plugins: [{ id: "secrets", version: 4 }],
      document: { type: "doc", content: [{ type: "secretReference", attrs: { secretId, showDetails: true } }] },
    };
    await expect(options().save(next, "", { signal: new AbortController().signal })).resolves.toEqual({ ok: true });
    expect(saveSource).toHaveBeenLastCalledWith(next, expect.stringContaining(`::secret{secretId=${secretId} showDetails=true}`), expect.anything());
  });

  it("closes the source view when editing ends or the permission is lost and drops the draft on host request", async () => {
    const view = await editor();
    for (const props of [{ readonly: true }, { canEditSource: false }]) {
      exposed(view).openSource(); await flushPromises();
      draft().close.mockClear();
      await view.setProps(props); await flushPromises();
      expect(options().readonly!()).toBe(true);
      expect(draft().close).toHaveBeenCalled();
      // Editing ended: the switch stays in place for the next intent; without the right it goes.
      expect(view.find(".source-switch").exists()).toBe("readonly" in props);
      await view.setProps({ readonly: false, canEditSource: true }); await flushPromises();
      expect(view.find(".source-switch").exists()).toBe(true);
    }
    exposed(view).openSource(); await flushPromises();
    draft().text.value = "dirty"; await flushPromises();
    exposed(view).discardSourceDraft(); await flushPromises();
    expect(draft().discard).toHaveBeenCalled();
    expect(sourceShown(view)).toBe(false);
  });

  it("binds the Markdown import hooks", async () => {
    const view = await editor({ readonly: false, pages: [{ id: "alpha", title: "Project Alpha" }] });
    const props = view.getComponent({ name: "MarkdownEditor" }).props() as Record<string, unknown>;
    const resolve = props.resolveWikiLink as (request: { target: string }) => { href: string } | null;
    expect(resolve({ target: "Project Alpha" })?.href).toContain("#/pages/alpha");
    const onImport = props.onMarkdownImport as (info: { leadingHeading?: string }) => unknown;
    expect(onImport({ leadingHeading: "Runbook" })).toEqual({ omitLeadingHeading: true });
    await view.setProps({ documentTitle: "" });
    onImport({ leadingHeading: "Nový název" });
    expect(view.emitted("importTitle")?.at(-1)).toEqual(["Nový název"]);
  });
});
