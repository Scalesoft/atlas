import { api } from "./api";
import { spaceCoverDrawingUrl } from "./spaceCoverDrawings";

/**
 * The image of a space: on its card in Knihovna, in the head of the space, in its settings and in the new-space
 * form. Its source says where it comes from: Auto is the built-in drawing the server picks from the space's name,
 * description and icon (`catalogKey`, null when nothing fits), Catalog a drawing a person chose, Upload an uploaded
 * image (`version` changes with every upload, so its URL can be cached for good; `tinted` blends it into the space's
 * two colours, drawings always are), None no image by choice.
 */
export type SpaceCoverSource = "Auto" | "Catalog" | "Upload" | "None";
/** Never null on a Space from this server; an older server may leave it out or send null, which reads as "no image". */
export interface SpaceCover { source: SpaceCoverSource; catalogKey: string | null; version: string | null; tinted: boolean }
export interface SpaceCoverChoice { source: "Auto" | "Catalog" | "None"; catalogKey?: string }
export interface SpaceCoverMatchInput { name: string; description: string; icon: string }

/** Raster images only: an SVG could carry script. The server checks the bytes, not just the declared type. */
export const spaceCoverTypes = ["image/png", "image/jpeg", "image/webp"] as const;
export const spaceCoverMaxBytes = 5 * 1024 * 1024;

/**
 * Upload → `/api/spaces/<id>/cover?v=<version>`; a catalog key this build knows (Catalog, or Auto with a pick) → its
 * static URL; otherwise undefined (None, Auto without a pick, an unknown key, no cover, a legacy object without source).
 */
export function spaceCoverSrc(spaceId: string, cover: SpaceCover | null | undefined): string | undefined {
  if (cover?.source === "Upload" && cover.version) return `/api/spaces/${encodeURIComponent(spaceId)}/cover?v=${encodeURIComponent(cover.version)}`;
  return cover?.source === "Catalog" || cover?.source === "Auto" ? spaceCoverDrawingUrl(cover.catalogKey) : undefined;
}

/** Changes are for the space Admin or a wiki admin. */
export const spaceCoverApi = {
  /** Replaces any previous image. */
  upload(spaceId: string, file: File) {
    const body = new FormData();
    body.append("file", file);
    return api<SpaceCover>(`/spaces/${encodeURIComponent(spaceId)}/cover`, { method: "PUT", body });
  },
  setTinted(spaceId: string, tinted: boolean) {
    return api<SpaceCover>(`/spaces/${encodeURIComponent(spaceId)}/cover`, { method: "PATCH", body: JSON.stringify({ tinted }) });
  },
  /** Back to Auto, from any source. */
  remove(spaceId: string) {
    return api<void>(`/spaces/${encodeURIComponent(spaceId)}/cover`, { method: "DELETE" });
  },
  /** A drawing of the catalog, the automatic pick again, or no image; releases an upload. */
  choose(spaceId: string, choice: SpaceCoverChoice) {
    return api<SpaceCover>(`/spaces/${encodeURIComponent(spaceId)}/cover/choice`, { method: "PUT", body: JSON.stringify(choice) });
  },
  /** The drawing the server would pick for these three values, for a preview before they are saved. Any signed-in account. */
  match(input: SpaceCoverMatchInput, signal?: AbortSignal) {
    return api<{ catalogKey: string | null }>("/space-covers/match", { method: "POST", body: JSON.stringify(input), signal });
  },
};
