import { expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import { nextTick } from "vue";
import type { Editor } from "@tiptap/core";
import type { Transaction } from "@tiptap/pm/state";
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from "@atlas/markdown/core";
import { MarkdownRenderer } from "@atlas/markdown/vue";
import { atlasContentPlugins, type DocumentReadServices, type DocumentWriteCapabilities } from "../documentWidgets";

/*
 * The editability contract of a document component (plan 3.6-0, rules 3–7), shared by every component's suite: the component in
 * Atlas's real content plugins (atlasContentPlugins), written or read through the page's one `canWrite()` like DocumentEditor does.
 * A component gives one case; every check becomes a test. A deviation listed for a check turns the expectation around: the check must
 * still fail, and once it passes the test asks for the deviation to be deleted. jsdom has no layout, so `render` compares what decides
 * it here (elements, classes, text); the geometry itself is the gallery's „Upravovat“ switch in the browser.
 */

export type EditabilityCheck = "render" | "switch" | "transition" | "remote" | "undo";

/** The component in a page editor: the controller, its editor, the page's host element and the component's element. */
export interface EditabilityPage {
  readonly controller: MarkdownEditorController<"rich">;
  readonly editor: Editor;
  readonly host: HTMLElement;
  readonly root: () => Element | null;
}

export interface EditabilityCase {
  readonly name: string;
  /** A page holding the component once. */
  readonly document: MarkdownRichValue;
  /** The component's element, in the static render and in the editor alike. */
  readonly selector: string;
  /** Affordances only editing adds that take no space (an overlay, a toolbar), left out of `render`. */
  readonly affordances?: readonly string[];
  /** Classes only the written component's controls carry; such a control is a button there and a plain element read: the same box, so the button and these classes are not compared. */
  readonly controls?: readonly string[];
  /** The component's confirmed action as a person does it (a command, a click). */
  readonly act: (page: EditabilityPage) => unknown;
  /** Opens its interaction (popover, menu, panel) in the writable page; resolves with the element that must go when the right goes. */
  readonly open?: (page: EditabilityPage) => Promise<Element | null>;
  /** A colleague's change of the component's node, as their transaction. */
  readonly remote: (editor: Editor) => Transaction;
  /** What the component waits for besides microtasks (a lazy panel, a lazy renderer). */
  readonly settle?: () => Promise<unknown>;
  /** Host services the component needs to show itself (the page's read services, e.g. secrets); the static render then is the live page read without the right to write, not the history view. */
  readonly services?: DocumentReadServices & DocumentWriteCapabilities;
  /** Checks it does not pass yet, each with the reason; delete the row once it passes. */
  readonly deviations?: Partial<Record<EditabilityCheck, string>>;
}

// ProseMirror's own helpers inside a node view, not the component's.
// `md-module-editable` is the module host's hover cursor (styles/modules.css, only under contenteditable=true): an affordance, no geometry.
const editorParts = ".ProseMirror-separator, .ProseMirror-trailingBreak, .ProseMirror-widget";

async function flush(settle?: () => Promise<unknown>) {
  for (let round = 0; round < 3; round++) {
    await settle?.();
    for (let i = 0; i < 8; i++) await Promise.resolve();
    await nextTick();
    await new Promise(resolve => setTimeout(resolve, 0));
  }
}

/** Elements, classes and text of the component, without editing helpers and the case's affordances. */
export function renderShape(element: Element, affordances: readonly string[] = [], controls: readonly string[] = []): unknown {
  const skipped = [editorParts, ...affordances].join(", ");
  const walk = (node: Node): unknown => {
    if (node.nodeType === Node.TEXT_NODE) return node.textContent!.replace(/\s+/g, " ").trim() || null;
    if (!(node instanceof Element) || node.matches(skipped)) return null;
    const control = node.tagName === "BUTTON" && controls.some(name => node.classList.contains(name));
    const classes = [...node.classList].filter(name => !name.startsWith("ProseMirror") && name !== "md-module-editable" && !(control && controls.includes(name))).sort().join(".");
    return [`${control ? "span" : node.tagName.toLowerCase()}${classes && `.${classes}`}`, ...[...node.childNodes].map(walk).filter(part => part !== null)];
  };
  return walk(element);
}

/**
 * The listeners `run` adds that are still registered after it on window, document or an element on the page (a detached element takes
 * its own along), as „type on target“. jsdom's selector engine registers its own on window as it likes; those are not the page's.
 */
async function listenersLeftBy(run: () => Promise<void>) {
  const add = EventTarget.prototype.addEventListener, remove = EventTarget.prototype.removeEventListener;
  const live = new Map<string, { target: EventTarget; listener: unknown; capture: boolean; type: string }>();
  let next = 0;
  const capture = (options?: boolean | AddEventListenerOptions | EventListenerOptions) => (typeof options === "boolean" ? options : !!options?.capture);
  const find = (target: EventTarget, type: string, listener: unknown, captured: boolean) =>
    [...live].find(([, entry]) => entry.target === target && entry.type === type && entry.listener === listener && entry.capture === captured)?.[0];
  EventTarget.prototype.addEventListener = function (type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | AddEventListenerOptions) {
    const captured = capture(options);
    if (listener && !find(this, type, listener, captured) && !new Error().stack?.includes("dom-selector")) {
      const key = String(next++);
      live.set(key, { target: this, listener, capture: captured, type });
      if (typeof options === "object") options.signal?.addEventListener("abort", () => live.delete(key), { once: true });
    }
    return add.call(this, type, listener, options);
  };
  EventTarget.prototype.removeEventListener = function (type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | EventListenerOptions) {
    const key = listener && find(this, type, listener, capture(options));
    if (key) live.delete(key);
    return remove.call(this, type, listener, options);
  };
  try { await run(); } finally { EventTarget.prototype.addEventListener = add; EventTarget.prototype.removeEventListener = remove; }
  return [...live.values()].filter(entry => !(entry.target instanceof Node) || entry.target instanceof Document || entry.target.isConnected)
    .map(entry => `${entry.type} on ${entry.target instanceof Element ? entry.target.tagName.toLowerCase() : entry.target.constructor.name}`).sort();
}

/** `left` without one of each of `own`: what the interaction left beyond the readonly state's own UI. */
function beyond(left: string[], own: string[]) {
  const rest = [...left];
  for (const entry of own) { const index = rest.indexOf(entry); if (index >= 0) rest.splice(index, 1); }
  return rest;
}

/** The page editor as DocumentEditor builds it: the content plugins once, writability through `canWrite()` and `setEditable`. */
function pageOf(value: MarkdownRichValue, selector: string, writable: boolean, services?: EditabilityCase["services"]) {
  let editor!: Editor;
  let created = 0;
  const capture: MarkdownPlugin = { id: "editability-capture", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };
  const plugins = [...atlasContentPlugins({ kind: "edit", ...services, canWrite: () => writable }), capture]
    .map(plugin => ({ ...plugin, create: (context: Parameters<MarkdownPlugin["create"]>[0]) => { created++; return plugin.create(context); } }));
  const controller = createMarkdownEditorController({ profile: "rich", plugins, source: { kind: "local", value }, editable: writable });
  const host = document.body.appendChild(document.createElement("div"));
  controller.attachView(host, { ariaLabel: "Kontrakt editovatelnosti" });
  // The editor's own normalization (the trailing paragraph) lands with the first transaction; let it land before anything is compared.
  editor.view.dispatch(editor.state.tr.setMeta("addToHistory", false));
  const page: EditabilityPage = { controller, editor, host, root: () => host.querySelector(selector) };
  return {
    page,
    created: () => created,
    value: () => JSON.stringify(controller.getValue()),
    /** The right to write goes (a lock, a revoked right, offline) or comes back, as DocumentEditor's `readonly` does it. */
    write(allowed: boolean) { writable = allowed; controller.setEditable(allowed); },
    destroy() { controller.destroy(); host.remove(); },
  };
}

async function staticRender(value: MarkdownRichValue, selector: string, settle?: () => Promise<unknown>, services?: EditabilityCase["services"]) {
  const wrapper = mount(MarkdownRenderer, { props: { value, profile: "rich", ariaLabel: "Statický render", plugins: atlasContentPlugins(services ? { kind: "edit", ...services, canWrite: () => false } : { kind: "history" }) }, attachTo: document.body });
  await flush(settle);
  return { root: document.body.querySelector(selector), unmount: () => wrapper.unmount() };
}

/** Every check of the contract for one component, as tests of the calling suite. */
export function runEditabilityContract(component: EditabilityCase) {
  const pages: { destroy(): void }[] = [];
  const open = (writable: boolean) => { const made = pageOf(component.document, component.selector, writable, component.services); pages.push(made); return made; };
  const cleanup = () => pages.splice(0).forEach(made => made.destroy());

  const checks: Record<EditabilityCheck, [title: string, probe: () => Promise<void>]> = {
    render: ["renders the same statically, read and written (rule 3)", async () => {
      const shown = await staticRender(component.document, component.selector, component.settle, component.services);
      try {
        const read = open(false), written = open(true);
        await flush(component.settle);
        expect(shown.root, "static render").not.toBeNull();
        const shapes = [shown.root!, read.page.root()!, written.page.root()!].map(element => renderShape(element, component.affordances, component.controls));
        expect(shapes[1], "readonly editor vs static render").toEqual(shapes[0]);
        expect(shapes[2], "editable editor vs static render").toEqual(shapes[0]);
      } finally { shown.unmount(); }
    }],
    switch: ["switches writability in place: no new plugins, the same element (rule 5)", async () => {
      const made = open(true);
      await flush(component.settle);
      const element = made.page.root(), created = made.created();
      made.write(false); await flush(component.settle);
      made.write(true); await flush(component.settle);
      expect(made.created(), "plugins created again").toBe(created);
      expect(made.page.root(), "the component's element was replaced").toBe(element);
    }],
    transition: ["closes its interaction, leaves no listeners and writes nothing once the right goes (rule 4)", async () => {
      const made = open(true);
      await flush(component.settle);
      // A first round lets what registers once per document (a shared menu, a lazy panel) settle; the second counts. What the
      // readonly state registers itself (a reader's control) is measured without the interaction and is not a leftover.
      await component.open?.(made.page);
      made.write(false); await flush(component.settle);
      made.write(true); await flush(component.settle);
      const own = await listenersLeftBy(async () => { made.write(false); await flush(component.settle); });
      made.write(true); await flush(component.settle);
      let opened: Element | null = null;
      const left = await listenersLeftBy(async () => {
        opened = (await component.open?.(made.page)) ?? null;
        made.write(false);
        await flush(component.settle);
      });
      // Gone or hidden: a component that keeps its element across the flip (contract J1.2) closes the interaction by hiding it.
      if (opened) expect((opened as Element).isConnected && !(opened as Element).closest("[hidden]"), "the open interaction stayed").toBe(false);
      expect(beyond(left, own), "listeners left by the interaction").toEqual([]);
      const before = made.value();
      await component.act(made.page);
      await flush(component.settle);
      expect(made.value(), "written after the right went").toBe(before);
    }],
    remote: ["keeps a colleague's change of its node, also with the interaction open (rule 6)", async () => {
      const made = open(true);
      await flush(component.settle);
      await component.open?.(made.page);
      made.page.editor.view.dispatch(component.remote(made.page.editor).setMeta("addToHistory", false));
      const remote = made.value();
      await flush(component.settle);
      expect(made.value(), "the component wrote over the remote change").toBe(remote);
    }],
    undo: ["undoes one confirmed action in one step (rule 7)", async () => {
      const made = open(true);
      await flush(component.settle);
      const before = made.value();
      await component.act(made.page);
      await flush(component.settle);
      expect(made.value(), "the action wrote nothing").not.toBe(before);
      made.page.controller.dispatch({ type: "undo" });
      await flush(component.settle);
      expect(made.value()).toBe(before);
    }],
  };

  for (const [check, [title, probe]] of Object.entries(checks) as [EditabilityCheck, (typeof checks)[EditabilityCheck]][]) {
    const deviation = component.deviations?.[check];
    it(`${component.name}: ${title}${deviation ? " — odchylka" : ""}`, async () => {
      try {
        const failure = await probe().then(() => undefined, (error: unknown) => error);
        if (!deviation) { if (failure) throw failure; return; }
        expect(failure, `${component.name} passes „${check}“ now: delete its deviation (${deviation})`).toBeDefined();
      } finally { cleanup(); document.body.replaceChildren(); }
    });
  }
}
