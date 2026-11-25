/**
 * „Moje barva“ and the colour of a person on every screen: the one palette of the editor (`@atlas/markdown/core`, also embedded by the
 * server, which assigns each person present in a live page or board one colour of it: their choice if no one present has it, else the
 * next free one). The server stamps the light variant; a dark surface shows the dark variant (`themedCollaboratorColor`).
 */
import { COLLABORATOR_COLORS, collaboratorColor, markdownUserColorSlot } from "@atlas/markdown/core";
import { appearanceHues } from "./appearance";

export interface CollaboratorChoice { id: string; label: string; hue: number; swatch: { light: string; dark: string } }

/** The 12 colours in assignment order, named as the appearance hues they come from; `hue` is unused by the picker's own swatch colour. */
export const collaboratorChoices: readonly CollaboratorChoice[] = COLLABORATOR_COLORS.map((color) => ({
  id: color.id,
  get label() { return appearanceHues.find((hue) => hue.id === color.id)?.label ?? color.id; },
  hue: 0,
  swatch: { light: color.light, dark: color.dark },
}));

/** What "Automaticky" gives a person alone in a page: the palette id their account hashes to. */
export const automaticCollaboratorColorId = (userId: string) => COLLABORATOR_COLORS[markdownUserColorSlot(userId)]!.id;

/** The stamped colour as the current surface should draw it (the dark variant under `html[data-mode="dark"]`). */
export const themedCollaboratorColor = (color: string) => collaboratorColor(color, typeof document !== "undefined" && document.documentElement.dataset.mode === "dark");
