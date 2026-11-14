import { getExtensionField, type AnyExtension, type Editor } from "@tiptap/core";
import type { Plugin } from "@tiptap/pm/state";
import type { MarkdownPlugin, MarkdownPluginContext, MarkdownPluginInstance } from "@atlas/markdown/core";
import type { AtlasAssistContext, AtlasAssistFactory } from "./types";

/**
 * The plugin list of each live editor instance (plan §12 H.4). `MarkdownEditor` fixes its plugins when it creates its
 * controller and refuses a different list later, so the list is decided once per mounted instance (its `key`) and
 * returned unchanged on every render until that instance unmounts:
 *
 * - with an assist factory at that moment, the base list plus one `atlasAssist` plugin;
 * - without one yet (3.6: the live editor opens with the page, the chunk comes in idle time), the base list plus a
 *   stand-in (lateAssist) that attaches the assist in place once `arrived` hands it the factory: the open never waits,
 *   and nothing is re-keyed, which would end the collaboration session and lose the undo history. With assistance off
 *   (`expected` false: no wiki switch, no AI) there is no stand-in at all.
 *
 * Only the live editors use it; reading, the page source, save projections and the warm-up build their own lists.
 */
export function createLiveEditorPlugins(base: readonly MarkdownPlugin[], factory: () => AtlasAssistFactory | null | undefined, context: AtlasAssistContext, expected: () => boolean = () => true) {
  const lists = new Map<string, readonly MarkdownPlugin[]>();
  const waiting = new Set<(assist: AtlasAssistFactory) => void>();
  return {
    for(key: string): readonly MarkdownPlugin[] {
      let list = lists.get(key);
      if (!list) {
        const assist = factory();
        // With assistance off (no wiki switch, no AI) nothing is added: the editor is exactly what it is without the module (H.4).
        const plugin = assist ? build(assist, context) : expected() ? lateAssist(context, waiting) : undefined;
        list = plugin ? [...base, plugin] : base;
        lists.set(key, list);
      }
      return list;
    },
    /** The instance with this key unmounted; the next one decides again. */
    release(key: string) {
      lists.delete(key);
    },
    /** The assist chunk is here: the editors built before it attach it now. */
    arrived(assist: AtlasAssistFactory) {
      for (const attach of [...waiting]) attach(assist);
    },
  };
}

function build(assist: AtlasAssistFactory, context: AtlasAssistContext): MarkdownPlugin | undefined {
  try { return assist(context); }
  catch (error) { console.warn("Návrhy při psaní se nepodařilo zapnout.", error); return undefined; }
}

type Cleanup = void | (() => void);
const run = (cleanup: Cleanup) => { if (typeof cleanup === "function") cleanup(); };
/** The ProseMirror plugins of a Tiptap extension, as the editor's extension manager would make them. */
const pluginsOf = (extension: AnyExtension, editor: Editor): Plugin[] =>
  (getExtensionField(extension, "addProseMirrorPlugins", { name: extension.name, options: extension.options, storage: extension.storage, editor, type: null } as never) as (() => Plugin[]) | undefined)?.() ?? [];

/**
 * The assist of an editor built before its chunk arrived. Once it does, the real plugin's ProseMirror plugins join the editor's
 * state and its hooks run with the contexts the editor gave the stand-in; its rows join the actions menu (Alt+Enter, Ctrl+Space).
 * What a plugin can only declare at creation (the selection bubble's AI action, the status panel) stays out of that session: the
 * actions menu offers the same, and the status shows in the assist's own layer.
 */
function lateAssist(context: AtlasAssistContext, waiting: Set<(assist: AtlasAssistFactory) => void>): MarkdownPlugin {
  return {
    id: "assist",
    apiVersion: 1,
    create(created) {
      let inner: MarkdownPluginInstance | undefined, engine: MarkdownPluginContext | undefined, view: MarkdownPluginContext | undefined;
      let joined = false, leaveEngine: Cleanup, leaveView: Cleanup;
      const attach = () => {
        if (!inner || !view || created.signal.aborted) return;
        if (!joined) {
          joined = true;
          for (const extension of inner.extensions ?? []) for (const plugin of pluginsOf(extension, view.editor)) view.editor.registerPlugin(plugin);
        }
        leaveView = inner.onAttach?.(view);
      };
      const arrive = (assist: AtlasAssistFactory) => {
        waiting.delete(arrive);
        if (created.signal.aborted) return;
        try { inner = build(assist, context)?.create(created); }
        catch (error) { console.warn("Návrhy při psaní se nepodařilo zapnout.", error); return; }
        if (inner && engine) leaveEngine = inner.onCreate?.(engine);
        attach();
      };
      waiting.add(arrive);
      created.signal.addEventListener("abort", () => waiting.delete(arrive), { once: true });
      return {
        intentions: request => inner?.intentions?.(request) ?? [],
        onCreate(context) {
          engine = context;
          return () => { run(leaveEngine); leaveEngine = undefined; engine = undefined; };
        },
        onAttach(context) {
          view = context;
          attach();
          return () => { run(leaveView); leaveView = undefined; view = undefined; };
        },
      };
    },
  };
}
