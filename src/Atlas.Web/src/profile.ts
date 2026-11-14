import type { Appearance } from "./api";
import t from "./i18n/shell";
import type { ProfileTab } from "./shellRoute";

/** The sections of the profile are the tabs of its address; `shellRoute.ts` lists them once. */
export type { ProfileTab };
/** The page a profile section is shown on: Účet and Vzhled are the two halves of Účet a vzhled (board 12). */
export const profilePageOf = (tab: ProfileTab): ProfileTab => (tab === "appearance" ? "account" : tab);
export interface ProfileAccount {
  displayName: string;
  email: string | null;
  version: string;
  source: "builtin" | "external" | "linked";
  canEditDisplayName: boolean;
  canEditEmail: boolean;
  currentSessionAuthenticatedAt: string | null;
  currentSessionAuthenticationMethod: string | null;
  lastSignInAt: string | null;
  lastSignInMethod: string | null;
}

/** Accent colours of the interface, the colour it leans to (`data-accent` on <html>). */
export type ThemeAccentId = Appearance["accent"];
export interface ThemeAccent {
  id: ThemeAccentId;
  /** Czech name of the hue, shown under the swatches. */
  label: string;
  /** OKLCH hue the dark theme tints its surfaces toward (palettes.css `--atlas-hue`). */
  hue: number;
}
/**
 * The one list of accents, in the order of the colour wheel from the default. Every entry has a light and a
 * dark palette (styles/palettes.css) and the server accepts it (Platform/Validation/Rules.cs); the profile and
 * the gallery read this list. Calm tones only: the user rejected saturated colour schemes.
 */
export const themeAccents: readonly ThemeAccent[] = [
  { id: "ocean", get label() { return t.accent.ocean; }, hue: 205 },
  { id: "blue", get label() { return t.accent.blue; }, hue: 255 },
  { id: "indigo", get label() { return t.accent.indigo; }, hue: 278 },
  { id: "violet", get label() { return t.accent.violet; }, hue: 305 },
  { id: "red", get label() { return t.accent.red; }, hue: 8 },
  { id: "sunset", get label() { return t.accent.sunset; }, hue: 42 },
  { id: "amber", get label() { return t.accent.amber; }, hue: 75 },
  { id: "green", get label() { return t.accent.green; }, hue: 158 },
];
/** New accounts start here (server `Appearance.Default`). */
export const defaultThemeAccent: ThemeAccentId = "ocean";
export const themeAccentLabel = (id: string) => themeAccents.find((accent) => accent.id === id)?.label ?? id;

/** Reading measure of documents, kept per account in this browser (App.vue, `atlas:reading:<id>:width`). */
export type ReadingWidth = "comfortable" | "wide";
