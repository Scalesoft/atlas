import type { Editor } from "@tiptap/core";
import { createMarkdownStubbedPopover, openMarkdownContextPopover, openMarkdownReaderPopover, type MarkdownPopoverAnchor, type MarkdownStubbedPopover } from "@atlas/markdown/core";
import { h, render, type AppContext, type Component } from "vue";

/** A popover's component loaded apart: its popover opens at once as a stub of its size while the chunk loads (stubbedPopover.ts). */
export type LazyVueComponent = MarkdownStubbedPopover<Component>;
export const lazyVueComponent = (load: () => Promise<{ default: Component }>): LazyVueComponent => createMarkdownStubbedPopover(() => load().then(module => module.default));
const isLazy = (component: Component | LazyVueComponent): component is LazyVueComponent => typeof (component as LazyVueComponent).prefetch === "function";

/**
 * The editor's anchored popover (Esc, focus return, outside click, the editor's theme) with a Vue component inside; the
 * component gets `close`. The node views' panels and menus all open through it, so they behave as the editor's own. A lazy component
 * (lazyVueComponent) opens as a skeleton of `stub` rows until its chunk is there, then as itself without a jump.
 */
export function openVuePopover(options: {
  editor: Editor; anchor: HTMLElement; positionAnchor?: MarkdownPopoverAnchor; title: string; variant?: "panel" | "menu"; readonly?: boolean; signal?: AbortSignal;
  /** Where the keyboard goes back when it closes, if not where it was opened from (a save redrew that). */
  returnFocus?: () => HTMLElement | null | undefined;
  component: Component | LazyVueComponent; props?: Record<string, unknown>;
  /** A lazy component's skeleton: its rows and their height in px, the size of what it draws. */
  stub?: { rows: number; rowHeight?: number };
  /** Called once the popover has closed, however it closed. */
  closed?: () => void;
  /** A class of its own besides atlas-requirement-popover (`is-wide`: a block's detail with its notes). */
  className?: string;
}): () => void {
  const { editor, component, props = {}, closed, className, stub, ...popover } = options;
  const mount = (drawn: Component, container: HTMLElement, close: () => void) => {
    const vnode = h(drawn, { ...props, close });
    const appContext = (editor as Editor & { appContext?: AppContext | null }).appContext;
    if (appContext) vnode.appContext = appContext;
    render(vnode, container);
    // Ctrl+Alt+M inside a chip's field closes it and goes on to the block's comment field (3.5.1 B9); the detail itself is that field.
    if (!/is-(wide|detail)/.test(className ?? "")) container.addEventListener("keydown", event => {
      if (!(event.ctrlKey || event.metaKey) || !event.altKey || event.key.toLowerCase() !== "m") return;
      event.preventDefault(); event.stopPropagation();
      close();
      editor.view.dom.dispatchEvent(new KeyboardEvent("keydown", { key: "m", ctrlKey: event.ctrlKey, metaKey: event.metaKey, altKey: true, bubbles: true, cancelable: true }));
    });
    return () => { render(null, container); };
  };
  const frame = { editor, trackSelection: false, ...popover, className: ["atlas-requirement-popover", className].filter(Boolean).join(" ") };
  if (isLazy(component)) return component.open({ ...frame, ...stub, closed, render: mount });
  return openMarkdownContextPopover({ ...frame, render: (container, close) => { const unmount = mount(component, container, close); return () => { unmount(); closed?.(); }; } });
}

/** A reader's panel (openMarkdownReaderPopover) with a Vue component inside, at a block of a page drawn without an editor (a read page). */
export function openVueReaderPopover(options: { anchor: HTMLElement; root: HTMLElement; title: string; className?: string; component: Component; props?: Record<string, unknown>;
  closed?: () => void }): () => void {
  const { component, props = {}, closed, className, ...popover } = options;
  return openMarkdownReaderPopover({ ...popover, className: ["atlas-requirement-popover", className].filter(Boolean).join(" "), render(container, close) {
    render(h(component, { ...props, close }), container);
    return () => { render(null, container); closed?.(); };
  } });
}
