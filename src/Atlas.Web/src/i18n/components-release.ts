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
    play: "Přehrát úvod",
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
  /** The live demos in the splash's highlight cards (components/release/demos): sample content only. */
  demos: {
    pointers: { title: "Porada týmu", budget: "Rozpočet: ", approved: "schválen ✓", launch: "Spuštění: ", before: "v březnu", after: "v pátek", next: "Další kroky" },
    agent: { title: "Zápis z porady", asked: "Shrň poradu do jedné věty.", summary: "Rozpočet je schválený, spouštíme v pátek.", name: "Asistent" },
    qr: { mention: "Jana Nováková", role: "Vedoucí provozu", scan: "Naskenujte telefonem" },
    graph: { question: (topic: string) => `Kdo umí ${topic}?`, topics: ["PostgreSQL", "Vue", "Účetnictví"], people: ["Jana", "Anna", "Petr", "Marek"] },
    verify: {
      title: "Postup při výpadku serveru", steward: "Garant", stewardName: "Jana Nováková", until: (date: string) => `platí do ${date}`, dates: ["25. 2. 2027", "25. 5. 2027"],
      verified: "Ověřeno", expiring: "Vyprší za 14 dní", expired: "Ověření vypršelo", reminder: "Vyprší za 14 dní: připomínka garantovi", verify: "Ověřit", today: "dnes ověřila Jana",
    },
    health: {
      tiles: ["Ověřeno", "Brzy vyprší", "Vypršelo", "Bez garanta"], counts: [42, 5, 3, 2], readers: (count: number) => `${count} čtenářů`,
      pages: [{ title: "Dovolená a svátky", state: "verified", readers: 38 }, { title: "Postup při výpadku serveru", state: "expired", readers: 214 }, { title: "Noví kolegové", state: "expiring", readers: 96 }],
      states: { verified: "Ověřeno", expiring: "Brzy vyprší", expired: "Vypršelo" },
    },
    discussion: { title: "Plán spuštění", name: "Jana", text: "Doplnila jsem kontakty na dodavatele.", reply: "Odpovědět", answer: "Díky, mrknu na to.", send: "Odeslat", dock: "Diskuse" },
    color: { title: "Moje barva", auto: "Automaticky", you: "Vy", other: "Petr", line: "Spouštíme v pátek" },
  },
};
export type ReleaseMessages = typeof cs;

export default translate(cs, { en: () => import("./components-release.en") });
