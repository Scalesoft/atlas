import type { Editor } from "@tiptap/core";
import { partySuggestions, type PartySuggest } from "./partySuggestions";
import { storageOf } from "./requirementRuntime";

/**
 * The party field's suggestions in this editor: the parties of the page's blocks, the space's „Na co čekáme“ (asked once per field)
 * and the people the host finds. A source that fails offers nothing; the field stays free text. The host is read per query: the
 * title's „čekáme na“ picker makes its source with the editor, before the host is set (requirementInline.ts). `cached` is the same
 * from what is known already (the page, the space's answer once it came, the host's cached people): the field's first rows (P17).
 */
export function partySource(editor: Editor): PartySuggest {
  const storage = storageOf(editor);
  let space: Promise<string[]> | undefined, spaceNames: string[] = [];
  const page = () => [...new Set(storage.index.value.items.map(item => item.attrs.party.trim()).filter(Boolean))];
  const search = async (text: string, signal: AbortSignal) => {
    const host = storage.host, spaceId = host?.spaceId();
    if (host?.api.waiting && spaceId) space ??= host.api.waiting(spaceId).then(list => (spaceNames = list.parties.map(entry => entry.party)), () => []);
    const people = text.trim() && host?.people ? await host.people(text, signal).catch(() => []) : [];
    return partySuggestions(text, { page: page(), space: (await space) ?? [], people });
  };
  const cached = (text: string) =>
    partySuggestions(text, { page: page(), space: spaceNames, people: text.trim() ? storage.host?.people?.cached?.(text) ?? [] : [] });
  return Object.assign(search, { cached });
}
