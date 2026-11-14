import { Extension, type Editor } from "@tiptap/core";
import type { Node as PMNode } from "@tiptap/pm/model";
import { Plugin } from "@tiptap/pm/state";
import { shallowRef, toRaw, type ShallowRef } from "vue";
import type { ProjectorCatalog } from "./projectorApi";
import type { ProjectorHost } from "./requirementsPlugin";
import { linkLoader, noLinks, type RequirementLinks } from "./requirementLinks";
import type { PageRequirement } from "./requirementModel";
import { pageRequirements } from "./requirementNode";

/** What one editor knows about its requirements beyond the document; nothing of it is stored. */
export interface RuntimeStorage {
  /** The page's requirements (its requirement headings), as last read from the document. */
  index: ShallowRef<{ readonly items: readonly PageRequirement[] }>;
  /** The document the index was last brought up to date with. */
  indexed: PMNode | null;
  /** Whether the editor is editable; it can change without a transaction (setEditable, an offline pause). */
  editable: ShallowRef<boolean>;
  /** The space's requirement catalog as last fetched (requirementCatalog.ts): for the page's flag then, its size once answered. */
  catalog?: { spaceId: string; flag: boolean | undefined; at: number; value: Promise<ProjectorCatalog>; size?: number };
  /** The page editor's host (requirementsPlugin.ts, set before the editor's first state); none in an editor without the page's plugin. */
  host?: ProjectorHost;
  /** Whether this page is a requirement catalog, for a host that does not pass the page's own flag. */
  catalogPage: ShallowRef<boolean | undefined>;
  /** What the requirements link outside the document, as the server answers (requirementLinks.ts): contacts' names, other pages' requirements. */
  links: ShallowRef<RequirementLinks>;
  /** Its loader, started once the editor is created (the host is set then). */
  linkLoader?: ReturnType<typeof linkLoader>;
}

// toRaw: Vue views get the editor through reactive props, which would unwrap the refs below.
export const storageOf = (editor: Editor) => (toRaw(editor).storage as unknown as Record<string, RuntimeStorage>).requirementRuntime!;

/**
 * Brings the index up to date with `doc`; a document the editor loaded without a transaction is caught by the first to ask. Typing that
 * changes no requirement keeps the same index object, so nothing that watches it redraws.
 */
function syncIndex(storage: RuntimeStorage, doc: PMNode) {
  if (storage.indexed === doc) return;
  const items = pageRequirements(doc, storage.host?.pageId());
  if (JSON.stringify(items) !== JSON.stringify(storage.index.value.items)) storage.index.value = { items };
  storage.indexed = doc;
}

/** The editor's requirement runtime, its index up to date. */
export function runtimeOf(editor: Editor): RuntimeStorage {
  const storage = storageOf(editor);
  syncIndex(storage, toRaw(editor).state.doc);
  return storage;
}

/** The live side of the requirements: the page's index, whether the editor is editable, and what the requirements link outside the page. */
export const RequirementRuntime = Extension.create<Record<string, never>, RuntimeStorage>({
  name: "requirementRuntime",
  addStorage: () => ({ index: shallowRef({ items: [] }), indexed: null, editable: shallowRef(true), catalogPage: shallowRef(undefined), links: shallowRef(noLinks) }),
  // After every extension's onCreate: the host (requirementsPlugin's preload) is set by then.
  onCreate() { queueMicrotask(() => { if (this.editor.isDestroyed) return; syncIndex(this.storage, this.editor.state.doc); this.storage.linkLoader?.start(); }); },
  addProseMirrorPlugins() {
    const storage = this.storage;
    return [new Plugin({
      view: view => {
        storage.editable.value = view.editable;
        const links = storage.linkLoader = linkLoader(storage, () => {});
        return {
          update(view, before) {
            const shown = storage.index.value;
            syncIndex(storage, view.state.doc);
            if (view.state.doc !== before.doc) links.touched();
            if (storage.index.value !== shown) links.sync();
            storage.editable.value = view.editable;
          },
          destroy: () => links.destroy(),
        };
      },
    })];
  },
});
