import { plural, translate } from ".";

/** components/release: the What's new page (ReleaseChangelog, ReleaseEntry, ReleaseSplash, ReleaseCover). The Czech source. */
const cs = {
  changelog: {
    title: "Co je nového",
    description: "Zásadní změny Atlasu od verze 3.0, nejnovější nahoře.",
    back: "Zpět do Atlasu",
  },
  cover: {
    aria: (label: string) => `${label}: setřete krytí tahem, nebo stiskněte Enter či mezerník`,
  },
  entry: {
    replay: (codename: string) => `Přehrát úvod verze ${codename}`,
    current: "Aktuální verze",
    whatsNewIn: (label: string, codename?: string) => `Co je nového v ${label}${codename ? ` ${codename}` : ""}`,
    patchesOf: (label: string) => `Opravné verze ${label}`,
  },
  splash: {
    markLabel: (label: string, codename: string) => `Značka verze ${label} ${codename}`.trim(),
    patchVersion: (version: string) => `Opravná verze ${version}`,
    newVersion: "Nová verze",
    version: "Verze",
    newMajorVersion: "Nová velká verze",
    majorVersion: "Velká verze",
    more: (count: number) => `A k tomu ${plural(count, { one: "# další novinka", few: "# další novinky", other: "# dalších novinek" })}`,
    patchLead: "Drobné opravy verze, kterou už znáte.",
    sincePatches: (since: string, count: number) => `Od vaší poslední verze ${since}: ${plural(count, { one: "# opravná verze", few: "# opravné verze", other: "# opravných verzí" })}.`,
    sinceReleases: (since: string, count: number) => `Novinky od vaší poslední verze ${since}: ${count} vydání.`,
    fixesIn: (version: string) => `Opravy v Atlas ${version}`,
    mainlyIn: (label: string) => `Hlavně v ${label}`,
    alsoIn: (range: string) => `Také v ${range}:`,
    continue: "Pokračovat do Atlasu",
    changelog: "Celý changelog",
    hint: "Enter pokračuje · Esc zavře · klik intro přeskočí",
  },
};
export type ReleaseMessages = typeof cs;

export default translate(cs, { en: () => import("./components-release.en") });
