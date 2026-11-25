/**
 * „Moje barva“ and the colour of a person on every screen: the one palette of the editor (`@atlas/markdown/core/userColor`, a light
 * subpath that does not load the editor, also embedded by the server, which assigns each person present in a live page or board one
 * colour of it: their choice if no one present has it, else the next free one). The server stamps the light variant; a dark surface
 * shows the dark variant (`themedCollaboratorColor`).
 */
import { shallowRef } from "vue";
import { COLLABORATOR_COLORS, collaboratorColor, markdownUserColor, markdownUserColorSlot } from "@atlas/markdown/core/userColor";
import { api } from "../api";
import { appearanceHues } from "../appearance";

/** Every account's chosen colour (lower-case user id → stamped colour); "Automaticky" accounts are absent. Loaded once per sign-in. */
const preferred = shallowRef<Readonly<Record<string, string>>>({});

/** Loads the chosen colours of all accounts (one small request) for the surfaces no presence message reaches. */
export async function loadPreferredColors() {
  try { preferred.value = await api<Record<string, string>>("/collaborator-colors"); } catch { /* the automatic colours stay */ }
}

/** Fired on `window` when the signed-in person saved another „Moje barva“. */
export const preferredColorEvent = "atlas:collaborator-color";

/** The signed-in person saved „Moje barva“ (a palette id or "auto"): their avatars follow at once, and a live editor shows the
 *  colour the server now stamps without waiting for its next heartbeat (collaborationPresence.ts). */
export function preferredColorChanged(userId: string, choice: string) {
  const key = userId.toLowerCase();
  const others = Object.fromEntries(Object.entries(preferred.value).filter(([id]) => id !== key));
  const chosen = COLLABORATOR_COLORS.find((color) => color.id === choice);
  preferred.value = chosen ? { ...others, [key]: chosen.light } : others;
  if (typeof window !== "undefined") window.dispatchEvent(new Event(preferredColorEvent));
}

/** The colour of an account where no presence message comes (history, discussion, the peek): their choice, else "Automaticky". Reactive. */
export const userColor = (userId: string) => preferred.value[userId.toLowerCase()] ?? markdownUserColor(userId);

export interface CollaboratorChoice { id: string; label: string; swatch: { light: string; dark: string } }

/** The 12 colours in assignment order, named as the appearance hues they come from. */
export const collaboratorChoices: readonly CollaboratorChoice[] = COLLABORATOR_COLORS.map((color) => ({
  id: color.id,
  get label() { return appearanceHues.find((hue) => hue.id === color.id)?.label ?? color.id; },
  swatch: { light: color.light, dark: color.dark },
}));

/** What "Automaticky" gives a person alone in a page: the palette id their account hashes to. */
export const automaticCollaboratorColorId = (userId: string) => COLLABORATOR_COLORS[markdownUserColorSlot(userId)]!.id;

/** The stamped colour as the current surface should draw it (the dark variant under `html[data-mode="dark"]`, as the editor's carets). */
export const themedCollaboratorColor = (color: string) => collaboratorColor(color, typeof document !== "undefined" && document.documentElement.dataset.mode === "dark");
