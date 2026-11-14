/**
 * The icon pack: the Lucide icons the editor registry (`@atlas/markdown/icons`) does not draw, generated into
 * assets/icon-pack by tooling/icon-pack. Only `meta.json` (the count) is imported here; the drawings come in 64 parts
 * (iconPackPart.ts) and the catalog is a chunk of its own. A part is requested the first time one of its icons is drawn,
 * all of them when the icon browser opens, and each is shared by every caller, so a view loads only what it shows.
 */
import { computed, ref, shallowRef } from "vue";
import parts from "virtual:icon-pack/parts";
import { iconPackPart } from "./iconPackPart";
import { loadChunk } from "./lazyComponent";
import { readStored, writeStored } from "./lib/browserStorage";
import meta from "./assets/icon-pack/meta.json";

export interface IconPackCategory { id: string; label: string }
export interface IconPackIcon { id: string; label: string; category: string; words: string }
export interface IconPackCatalog { lucide: string; categories: IconPackCategory[]; core: Record<string, string>; icons: IconPackIcon[] }

/** Icons the pack adds to the 87; known before the pack is loaded. */
export const iconPackCount: number = meta.count;
/** name → inner markup of the parts that have arrived; null before the first. */
export const iconPackMarkup = shallowRef<Readonly<Record<string, string>> | null>(null);
/** True after a part failed to load twice; reset by the next success. */
export const iconPackUnavailable = ref(false);
export const iconPackCatalog = shallowRef<IconPackCatalog | null>(null);

const loadedParts = shallowRef<ReadonlySet<number>>(new Set());
const partRequests = new Map<number, Promise<void>>();
let catalogRequest: Promise<IconPackCatalog> | undefined;

/** Whether the part that would hold `name` has arrived (reactive): a name it lacks is no pack icon. */
export const iconPackPartLoaded = (name: string): boolean => loadedParts.value.has(iconPackPart(name));

function loadPart(index: number): Promise<void> {
  let request = partRequests.get(index);
  if (!request) {
    request = loadChunk(parts[index]!).then(
      ({ default: markup }) => {
        iconPackMarkup.value = { ...iconPackMarkup.value, ...markup };
        loadedParts.value = new Set([...loadedParts.value, index]);
        iconPackUnavailable.value = false;
      },
      (error) => {
        partRequests.delete(index);
        iconPackUnavailable.value = true;
        throw error;
      },
    );
    partRequests.set(index, request);
  }
  return request;
}

/**
 * The part of `name`, or every part without one (the icon browser). One shared request per part through loadChunk; resolves at
 * once when loaded; rejects while unavailable, and the next call tries again.
 */
export function loadIconPackMarkup(name?: string): Promise<void> {
  return name === undefined ? Promise.all(parts.map((_, index) => loadPart(index))).then(() => {}) : loadPart(iconPackPart(name));
}

/** A hint before the icon browser opens: plain imports whose failure is ignored (no flag, no state). */
export function prefetchIconPackMarkup() {
  for (const load of parts) load().catch(() => {});
}

export function loadIconPackCatalog(): Promise<IconPackCatalog> {
  catalogRequest ??= loadChunk(() => import("./assets/icon-pack/catalog.json")).then(
    ({ default: catalog }) => {
      iconPackCatalog.value = catalog;
      return catalog;
    },
    (error) => {
      catalogRequest = undefined;
      throw error;
    },
  );
  return catalogRequest;
}

const labels = computed(() => new Map(iconPackCatalog.value?.icons.map((icon) => [icon.id, icon.label] as const)));
/** The Czech label of a pack icon once the catalog is loaded (reactive); undefined otherwise. */
export const iconPackLabel = (id: string): string | undefined => labels.value.get(id);

const recentKey = "atlas:appearance:recent-icons";
const recentMax = 16;

/** The last 16 chosen ids, newest first; storage problems read as empty and ignore writes. */
export function readRecentIcons(): string[] {
  try {
    const stored: unknown = JSON.parse(readStored(recentKey) ?? "[]");
    return Array.isArray(stored) ? stored.filter((id): id is string => typeof id === "string").slice(0, recentMax) : [];
  } catch {
    return [];
  }
}

export function rememberRecentIcon(id: string): string[] {
  const recent = [id, ...readRecentIcons().filter((item) => item !== id)].slice(0, recentMax);
  writeStored(recentKey, JSON.stringify(recent));
  return recent;
}
