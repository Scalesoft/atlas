/**
 * „Moje barva“ and the colour of a person on every screen: the one palette of the editor (`@atlas/markdown/core`, also embedded by the
 * server, which assigns each person present in a live page or board one colour of it: their choice if no one present has it, else the
 * next free one). The server stamps the light variant; a dark surface shows the dark variant (`themedCollaboratorColor`).
 */
import palette from "../../../Atlas.Markdown/src/core/collaboratorColors.json";
import { appearanceHues } from "../appearance";

// The palette file is imported, not `@atlas/markdown/core`: the first screen shows colours and must not load the editor (bundle budget).
// The two functions below are the editor's (core/userColor.ts) and the server's (CollaboratorPalette.Auto); the tests pin the same values.
const COLLABORATOR_COLORS: readonly { id: string; light: string; dark: string }[] = palette;
const colorSlot = (userId: string) => {
  let hash = 0;
  for (let i = 0; i < userId.length; i++) hash = (Math.imul(hash, 31) + userId.charCodeAt(i)) | 0;
  return Math.abs(hash) % COLLABORATOR_COLORS.length;
};
const collaboratorColor = (color: string, dark: boolean) => {
  const entry = COLLABORATOR_COLORS.find((item) => item.light === color || item.dark === color);
  return entry ? (dark ? entry.dark : entry.light) : color;
};

/** The automatic colour of an account (light variant): the server's starting point (CollaboratorPalette.Auto) before it moves a colour aside. */
export const userColor = (userId: string) => COLLABORATOR_COLORS[colorSlot(userId)]!.light;

export interface CollaboratorChoice { id: string; label: string; hue: number; swatch: { light: string; dark: string } }

/** The 12 colours in assignment order, named as the appearance hues they come from; `hue` is unused by the picker's own swatch colour. */
export const collaboratorChoices: readonly CollaboratorChoice[] = COLLABORATOR_COLORS.map((color) => ({
  id: color.id,
  get label() { return appearanceHues.find((hue) => hue.id === color.id)?.label ?? color.id; },
  hue: 0,
  swatch: { light: color.light, dark: color.dark },
}));

/** What "Automaticky" gives a person alone in a page: the palette id their account hashes to. */
export const automaticCollaboratorColorId = (userId: string) => COLLABORATOR_COLORS[colorSlot(userId)]!.id;

/** The stamped colour as the current surface should draw it (the dark variant under `html[data-mode="dark"]`). */
export const themedCollaboratorColor = (color: string) => collaboratorColor(color, typeof document !== "undefined" && document.documentElement.dataset.mode === "dark");
