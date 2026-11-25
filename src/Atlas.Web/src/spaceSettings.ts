import type { SpaceCover } from "./spaceCover";
import { api, post, put, type SpacePinPolicy } from './api';
import type { PublicationMode } from './publishing/pagePublication';
export interface SpaceSettings { id: string; name: string; icon: string; color: string; description: string; pinPolicy: SpacePinPolicy; version: string; /** „Komentáře“: whether readers may post comments; an older server leaves it out (on). */ commentsEnabled?: boolean; /** „Publikace nových stránek“: the default of the pages of the space; an older server leaves it out (Manual). */ publicationMode?: PublicationMode; /** The image of the space, saved on its own (spaceCover.ts), not with the form; an older server may leave it out. */ cover?: SpaceCover | null; /** „Ověřování obsahu“ (5.2): the default interval of its pages in months (3, 6, 12); null or absent = off. */ verificationIntervalMonths?: number | null }
export type SaveSpaceSettings = Pick<SpaceSettings, 'name' | 'icon' | 'color' | 'description' | 'pinPolicy' | 'commentsEnabled' | 'publicationMode' | 'verificationIntervalMonths'> & { expectedVersion: string };
export const spaceSettingsApi = {
  get: (id: string, signal?: AbortSignal) => api<SpaceSettings>(`/spaces/${encodeURIComponent(id)}/settings`, { signal }),
  save: (id: string, value: SaveSpaceSettings) => put<SpaceSettings>(`/spaces/${encodeURIComponent(id)}/settings`, value),
};
/** The space trash, the wiki admin's. */
export interface TrashedSpace { id: string; name: string; icon: string; color: string; trashedAt: string; trashedById: string; trashedByName: string; pageCount: number;
  /** The vaults its permanent delete destroys, named in the confirmation. */
  vaultNames: string[] }
export const spaceTrashApi = {
  /** `name` is the name typed as the confirmation. */
  trash: (id: string, name: string) => post<void>(`/spaces/${encodeURIComponent(id)}/trash`, { name }),
  restore: (id: string) => post<void>(`/spaces/${encodeURIComponent(id)}/restore`, {}),
  /** Permanently, wiki admin only; `name` typed as the confirmation (400 space_name_mismatch). */
  purge: (id: string, name: string) => post<void>(`/spaces/${encodeURIComponent(id)}/purge`, { name }),
  list: (signal?: AbortSignal) => api<TrashedSpace[]>("/space-trash", { signal }),
};
