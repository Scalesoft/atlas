import type { BoardColor, BoardPaletteColor } from "../../../boards/boardApi";
import t from "../../../i18n/boards";
import { appearanceColorLabel } from "../../../appearance";

/**
 * The board's colours (spec §4 order) on the appearance palette of styles/appearance.css, so they hold up in dark mode:
 * `data-color` gives --appearance-bg/-fg/-ink. White and black have no hue: the board's CSS draws them from the surface and
 * the text colour (`data-board-color`). An editor component's background may be any colour of the palette itself (`blue-4`).
 */
export const boardPalette: Record<BoardPaletteColor, { appearance?: string; label: string }> = {
  yellow: { appearance: "amber-2", get label() { return t.palette.yellow; } },
  orange: { appearance: "orange-2", get label() { return t.palette.orange; } },
  red: { appearance: "red-2", get label() { return t.palette.red; } },
  pink: { appearance: "pink-2", get label() { return t.palette.pink; } },
  purple: { appearance: "violet-2", get label() { return t.palette.purple; } },
  blue: { appearance: "blue-2", get label() { return t.palette.blue; } },
  teal: { appearance: "teal-2", get label() { return t.palette.teal; } },
  green: { appearance: "green-2", get label() { return t.palette.green; } },
  gray: { appearance: "slate-2", get label() { return t.palette.gray; } },
  white: { get label() { return t.palette.white; } },
  black: { get label() { return t.palette.black; } },
};

const ofBoard = (color: BoardColor): { appearance?: string; label: string } | undefined => boardPalette[color as BoardPaletteColor];
/** The appearance key a board colour draws with: its palette's, or the colour itself. */
const appearanceOf = (color: BoardColor) => (ofBoard(color) ? ofBoard(color)!.appearance : color);
export const colorLabel = (color: BoardColor) => ofBoard(color)?.label ?? appearanceColorLabel(color);

/** Attributes that colour an element by a board colour; none for an item without one. */
export const colorAttributes = (color: BoardColor | undefined) => {
  if (!color) return {};
  const appearance = appearanceOf(color);
  return { "data-board-color": color, ...(appearance && { "data-color": appearance }) };
};
