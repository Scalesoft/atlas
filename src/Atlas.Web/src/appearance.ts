/**
 * The colour palette of spaces and page icons: 60 colours — 12 hues × 5 tones in OKLCH
 * (styles/appearance.css), so any icon on any colour reads alike in light and dark mode — and the
 * check of a stored icon name. The icon catalog with its names and search is appearanceIcons.ts,
 * loaded only where icons are listed. The server accepts exactly these values
 * (Platform/Appearance/AppearanceCatalog.cs); older values stay valid as aliases.
 */
import t from "./i18n/shell";

/** A stored icon name: lower-case kebab-case, at most 40 characters (the database keeps this shape, not the list of names). */
export const isIconName = (name: string) => name.length <= 40 && /^[a-z0-9]+(-[a-z0-9]+)*$/.test(name);

export interface AppearanceHue { id: string; label: string }
export interface AppearanceTone { id: 1 | 2 | 3 | 4 | 5; label: string }
export const appearanceHues: readonly AppearanceHue[] = [
  { id: "red", get label() { return t.hue.red; } }, { id: "orange", get label() { return t.hue.orange; } }, { id: "amber", get label() { return t.hue.amber; } },
  { id: "lime", get label() { return t.hue.lime; } }, { id: "green", get label() { return t.hue.green; } }, { id: "teal", get label() { return t.hue.teal; } },
  { id: "cyan", get label() { return t.hue.cyan; } }, { id: "blue", get label() { return t.hue.blue; } }, { id: "indigo", get label() { return t.hue.indigo; } },
  { id: "violet", get label() { return t.hue.violet; } }, { id: "pink", get label() { return t.hue.pink; } }, { id: "slate", get label() { return t.hue.slate; } },
];
export const appearanceTones: readonly AppearanceTone[] = [
  { id: 1, get label() { return t.tone.t1; } }, { id: 2, get label() { return t.tone.t2; } }, { id: 3, get label() { return t.tone.t3; } }, { id: 4, get label() { return t.tone.t4; } }, { id: 5, get label() { return t.tone.t5; } },
];
export interface AppearanceColor { id: string; label: string; hue: string; tone: number }
/** 60 colours, tone by tone: key "<hue>-<tone>". */
export const appearanceColors: readonly AppearanceColor[] = appearanceTones.flatMap((tone) =>
  appearanceHues.map((hue) => ({ id: `${hue.id}-${tone.id}`, get label() { return `${hue.label} · ${tone.label}`; }, hue: hue.id, tone: tone.id })),
);
/** Values stored before the palette existed (spaces: blue…coral; page icons: slate…violet); CSS renders them as tone 2. */
export const legacyAppearanceColors: Readonly<Record<string, string>> = {
  blue: "blue-2", green: "green-2", amber: "amber-2", coral: "red-2",
  slate: "slate-2", teal: "teal-2", orange: "orange-2", rose: "pink-2", violet: "violet-2",
};

export function appearanceColorLabel(id: string) {
  const key = legacyAppearanceColors[id] ?? id;
  return appearanceColors.find((color) => color.id === key)?.label ?? id;
}
/** The palette key a stored value stands for (legacy aliases resolved). */
export const appearanceColorKey = (id: string) => legacyAppearanceColors[id] ?? id;
