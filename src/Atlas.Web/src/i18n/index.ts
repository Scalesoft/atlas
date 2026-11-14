/**
 * The language of the tool (Atlas 4.1): menus, dialogs, settings, messages. Never of page content or user data.
 *
 * Decided once per page load, before anything renders, and fixed until the next load: no reactivity, a change of language
 * saves and reloads (owner 2026-11-07). The account's choice in the profile („Automaticky / Čeština / English“) is mirrored
 * in the cookie `atlas-language`, so the login page, public pages and the server (Platform/Localization/RequestLanguage.cs)
 * read the same choice; without it the browser decides: Czech for a Czech browser, English otherwise.
 *
 * Messages live per area in `src/i18n/<area>.ts` (the Czech source, bundled, and the fallback) with `<area>.en.ts` beside it,
 * loaded only for English together with the area.
 */
export type Language = "cs" | "en";
/** What the profile stores: `auto` leaves the language to the browser. */
export type LanguageChoice = "auto" | Language;

const cookie = "atlas-language";
// A worker (history/diffWorker.ts) imports this through historyDiff.ts: it has no document, so it reads the language from navigator only.
const inPage = typeof document !== "undefined";
const remembered = (): Language | undefined => (inPage ? /(?:^|;\s*)atlas-language=(cs|en)(?:;|$)/.exec(document.cookie)?.[1] as Language | undefined : undefined);
const fromBrowser = (): Language => (/^cs\b/i.test(navigator.language ?? "") ? "cs" : "en");

/** The language of this page load. */
export const language: Language = remembered() ?? fromBrowser();
/** The locale of dates, numbers and display sorting (`Intl.*`). Sorting stored for everyone (TitleOrder, search) stays Czech. */
export const locale = language === "cs" ? "cs-CZ" : "en-US";
if (inPage) document.documentElement.lang = language;

/**
 * Remembers the account's choice in this browser (and for the server). True when this page shows another language than the
 * choice now asks for, so the caller reloads; false too when the browser keeps no cookie, so a refused cookie never loops.
 */
export function rememberLanguage(choice: LanguageChoice | undefined): boolean {
  document.cookie = choice === "cs" || choice === "en"
    ? `${cookie}=${choice};path=/;max-age=31536000;samesite=lax${location.protocol === "https:" ? ";secure" : ""}`
    : `${cookie}=;path=/;max-age=0`;
  return (remembered() ?? fromBrowser()) !== language;
}

let loading: Promise<unknown> | undefined;

/** Texts on their way (another language's file): the app mounts, and a lazy component renders, only once they are in. A file
 * that fails is no reason to stay blank: the Czech stays. */
export function waitForTexts(arrival: Promise<unknown>) {
  const settled = arrival.catch(() => {});
  const all = (loading = loading ? Promise.all([loading, settled]) : settled);
  void all.then(() => { if (loading === all) loading = undefined; });
}

/**
 * Resolves when the texts of everything loaded so far are in; undefined while nothing loads (Czech never waits). main.ts waits
 * before it mounts, lazyComponent before it renders; another `import()` whose texts show at once: `.then(withTexts)`.
 */
export const textsLoaded = (): Promise<unknown> | undefined => loading;
export const withTexts = <T>(value: T): T | Promise<T> => (loading ? loading.then(() => value) : value);

/** Copies a translation into the Czech messages in place, so a section object held earlier shows it too. */
function fill(target: Record<string, unknown>, source: Record<string, unknown>) {
  for (const [key, value] of Object.entries(source)) {
    const current = target[key];
    if (value && typeof value === "object" && current && typeof current === "object") fill(current as Record<string, unknown>, value as Record<string, unknown>);
    else target[key] = value;
  }
}

/**
 * The messages of an area in the language of this page: `export default translate(cs, { en: () => import("./x.en") })`. Czech
 * is returned as it is; another language is filled into it when its file arrives, before anything that waits for texts renders.
 * Read a text where it is shown (setup, render, a function), never into a module-level constant: that one would keep the
 * Czech. A file that fails to load leaves the Czech source.
 */
export function translate<T extends object>(cs: T, others: { [L in Exclude<Language, "cs">]?: () => Promise<{ default: T }> }): T {
  const load = language === "cs" ? undefined : others[language];
  // shortcut: the file is asked for when its area loads, one round trip after the area's chunk on a cold cache (top-level
  // await would chain it, but any TLA makes Rolldown split the eager set: +51 KiB gzip); preload it beside the chunk if it shows.
  if (load) waitForTexts(load().then((module) => fill(cs as Record<string, unknown>, module.default as Record<string, unknown>)));
  return cs;
}

const numbers = new Intl.NumberFormat(locale);
const rules = new Intl.PluralRules(locale);

/** A number as this language writes it („1 000“, „1,000“). */
export const formatNumber = (value: number) => numbers.format(value);

const sizes = new Intl.NumberFormat(locale, { maximumFractionDigits: 1 });
/** A file size: „812 B“, „1,5 kB“, „3,2 MB“. */
export function formatBytes(value: number): string {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${sizes.format(value / 1024)} kB`;
  return `${sizes.format(value / 1024 / 1024)} MB`;
}

/**
 * The form of a count this language asks for, with `#` as the number: `plural(n, { one: "# trezor", few: "# trezory", other:
 * "# trezorů" })` in Czech, `{ one: "# vault", other: "# vaults" }` in English. A form the language does not use falls back to
 * `other` (Czech `many` is for fractions).
 */
export function plural(count: number, forms: Partial<Record<Intl.LDMLPluralRule, string>> & { other: string }): string {
  return (forms[rules.select(count)] ?? forms.other).replaceAll("#", formatNumber(count));
}
