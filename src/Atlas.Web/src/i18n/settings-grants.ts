import { translate } from ".";

const cs = {
  list: (items: string[]) => items.join(" a "),
  explanation: {
    label: (subject: string, target: string) => `Proč má ${subject} přístup k ${target}`,
    until: (until: string) => `, do ${until}`,
    automatic: (subject: string, scope: string, level: string, label: string) => `${subject} má na ${scope} automaticky úroveň ${level} jako ${label}.`,
    direct: (subject: string, level: string, scope: string, until: string) => `${subject} má přímé přidělení úroveň ${level} na ${scope}${until}.`,
    viaGroup: (subject: string, label: string, level: string, scope: string, until: string) => `${subject} přes skupinu ${label} má úroveň ${level} na ${scope}${until}.`,
    none: (subject: string, target: string) => `${subject} k cíli ${target} přístup nemá.`,
    summary: (subject: string, level: string, includes: string | undefined, paths: number) =>
      `${subject} má úroveň ${level}${includes ? ` (zahrnuje ${includes})` : ""}${paths > 1 ? `, nejvyšší z ${paths} cest` : ""}.`,
    automaticBadge: "Automaticky",
    untilBadge: (until: string) => `do ${until}`,
  },
  level: {
    spoken: (label: string, includes: string | undefined) => (includes ? `Úroveň ${label}, zahrnuje ${includes}` : `Úroveň ${label}`),
    includes: (label: string, list: string) => `${label} zahrnuje i ${list}.`,
    lowest: (label: string) => `${label} je nejnižší úroveň.`,
  },
  effective: {
    label: "Platí",
    none: "Bez nastavení",
    own: (level: string) => `Vlastní nastavení (${level})`,
    inherited: (level: string) => `Zděděno z úrovně ${level}`,
    ownBadge: "Vlastní",
    inheritedBadge: "Zděděno",
    origin: "Odkud hodnota pochází",
    takes: "přebírá",
    change: "Změnit",
    revert: "Vrátit na zděděné",
    custom: "Nastavit vlastní",
  },
  spaceSwitcher: {
    open: "Otevřeno",
    chip: (name: string) => `Prostor ${name} – přepnout prostor`,
    switch: "Přepnout prostor",
    other: "Nastavení jiného prostoru",
    search: "Hledat prostor",
    help: "Šipkami vyberete, klávesou Enter otevřete.",
  },
};
export type SettingsGrantsMessages = typeof cs;

export default translate(cs, { en: () => import("./settings-grants.en") });
