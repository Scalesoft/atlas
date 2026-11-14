import type { EditorSuggestionsMode } from "../aiPreferences";
import type { User } from "../api";
import type { AtlasAssistFactory } from "./types";

/**
 * Lazy and conditional loading of the editor's assist module (plan §0.2, §12 H.4). The module and the Atlas adapter
 * are separate chunks that load only while the wiki has „Návrhy při psaní“ on (`features.editorAssist`, S7b) and
 * a writable page is open (in idle time, when its live editor warms up or attaches). Switched off, nothing is imported, so
 * the browser never requests either chunk and the editor gets exactly the plugins it has without assistance.
 *
 * The pinned editor (0.9.0) ships the module. A failed chunk resolves `null`; the next editing start tries again. The
 * editor never waits for it: a session that starts before it arrives runs without assistance.
 */
let loading: Promise<AtlasAssistFactory | null> | undefined;

export function editorAssistEnabled(user: Pick<User, "features"> | null | undefined): boolean {
  return user?.features?.editorAssist === true;
}

export function loadEditorAssist(user: (Pick<User, "features"> & Partial<Pick<User, "id">>) | null | undefined, options: { writingAllowed?: boolean } = {}): Promise<AtlasAssistFactory | null> {
  if (!user) { preferenceAbort?.abort(); activeAuthor = undefined; }
  if (!editorAssistEnabled(user) && !options.writingAllowed) return Promise.resolve(null);
  const disabled = [...new Set(user?.features?.disabledSuggestionTypes ?? [])].sort();
  loading ??= load().catch((error: unknown) => {
    loading = undefined;
    console.warn("Návrhy při psaní se nepodařilo načíst.", error);
    return null;
  });
  const mode = user?.id ? preferenceMode(user) : Promise.resolve<EditorSuggestionsMode>("rules");
  return Promise.all([loading, mode]).then(([factory, choice]) => factory && forAuthor(factory, disabled, editorAssistEnabled(user), choice));
}

/**
 * Profil → AI (U1): the suggestion types the author switched off (`features.disabledSuggestionTypes`, e.g. `link-mention`)
 * bound to the adapter's context, unless the host passes its own choice. One factory per set of types, so the same
 * author gets the same factory every time.
 */
const bound = new WeakMap<AtlasAssistFactory, Map<string, AtlasAssistFactory>>();
function forAuthor(factory: AtlasAssistFactory, disabled: readonly string[], automatic: boolean, mode: EditorSuggestionsMode): AtlasAssistFactory {
  const key = `${automatic}:${mode}:` + disabled.join("\n");
  let byTypes = bound.get(factory);
  if (!byTypes) bound.set(factory, byTypes = new Map());
  let author = byTypes.get(key);
  if (!author) {
    const off = new Set(disabled);
    author = context => factory({ ...context, suggestionTypeEnabled: context.suggestionTypeEnabled ?? (type => !off.has(type)),
      automaticSuggestionsEnabled: context.automaticSuggestionsEnabled ?? (() => automatic && mode !== "off"),
      modelSuggestionsEnabled: context.modelSuggestionsEnabled ?? (() => automatic && mode === "on"),
    });
    Object.assign(author, { automaticSuggestionsEnabled: () => automatic && mode !== "off", modelSuggestionsEnabled: () => automatic && mode === "on" });
    byTypes.set(key, author);
  }
  return author;
}

async function load(): Promise<AtlasAssistFactory> {
  const assist = await import("@atlas/markdown/assist");
  const { createAtlasAssist } = await import("./atlasAssist");
  return context => createAtlasAssist(assist, context);
}

// The session's author object is replaced by refreshSession after profile save. A weak cache shares hover/edit loads
// for that snapshot, but never carries the old choice into a new account/session snapshot. Failure is rules only.
let activeAuthor: object | undefined;
let preferenceAbort: AbortController | undefined;
const modes = new WeakMap<object, Promise<EditorSuggestionsMode>>();
function preferenceMode(author: object): Promise<EditorSuggestionsMode> {
  if (activeAuthor !== author) { preferenceAbort?.abort(); activeAuthor = author; }
  let mode = modes.get(author);
  if (!mode) {
    preferenceAbort = new AbortController();
    const signal = preferenceAbort.signal;
    mode = import("../aiPreferences").then(({ aiPreferencesApi }) => aiPreferencesApi.load(signal))
      .then(value => ['on','rules','off'].includes(value.editorSuggestions) ? value.editorSuggestions : 'rules').catch(() => 'rules' as const);
    modes.set(author, mode);
  }
  return mode;
}
