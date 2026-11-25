import type { Component, ComputedRef, Ref } from "vue";
import type { MarkdownPlugin, MarkdownRichValue } from "@atlas/markdown/core";
import { createMarkdownEditorController } from "@atlas/markdown/core";
import {
  MarkdownSourcePanel as EditorSourcePanel,
  useMarkdownSourceDraft,
} from "@atlas/markdown/vue";
import type { PageSourceSaveResult } from "./pageSource";

/** The part of the editor's `MarkdownSourceDraft` Atlas drives. */
export interface PageSourceDraft {
  readonly dirty: ComputedRef<boolean>;
  readonly busy: Readonly<Ref<boolean>>;
  apply(): Promise<boolean>;
  discard(): void;
  close(): void;
}

export interface PageSourceDraftOptions {
  value: () => MarkdownRichValue;
  plugins?: () => readonly MarkdownPlugin[];
  readonly?: () => boolean;
  save(value: MarkdownRichValue, text: string, context: { readonly signal: AbortSignal }): Promise<PageSourceSaveResult>;
}

// Through `unknown`: in source mode the editor's own declarations carry a second copy of Vue's ref and component
// types, which Atlas's copy does not accept. The runtime objects are the same.
/** The editor's source panel; it is async, so its chunk (and through it Monaco) loads only when the „Zdroj“ tab renders. */
export const MarkdownSourcePanel = EditorSourcePanel as unknown as Component;

/** The draft for the „Zdroj“ tab. Call during setup; the draft logic loads on the first `open()`. */
export function usePageSourceDraft(options: PageSourceDraftOptions): PageSourceDraft {
  return (useMarkdownSourceDraft as unknown as (options: PageSourceDraftOptions) => PageSourceDraft)(options);
}

/**
 * The Markdown projection stored with the revision, computed the same way as a normal save: a detached,
 * non-editable controller over the applied value. Its plugins must be fresh instances (plugins bind to
 * the editor that creates them), never the live document's.
 */
export function markdownProjection(value: MarkdownRichValue, plugins: readonly MarkdownPlugin[]): string {
  const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value }, plugins, editable: false });
  try {
    const detach = controller.attachView(document.createElement("div"), { ariaLabel: "Projekce zdroje stránky" });
    try { return controller.getProjection().markdown; } finally { detach(); }
  } finally { controller.destroy(); }
}
