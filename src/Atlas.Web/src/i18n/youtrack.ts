import { translate } from ".";

/** The YouTrack issues overview (youtrack/). The Czech source. */
const cs = {
  label: "Úkoly YouTrack",
  empty: "Zatím tu nejsou žádné úkoly YouTrack. Vložte je přes /YouTrack.",
  copyKeys: "Kopírovat klíče",
  keysCopied: "Klíče jsou zkopírované.",
  keysCopyFailed: "Klíče se nepodařilo zkopírovat.",
  csv: "Stáhnout (.csv)",
  csvFile: "ukoly.csv",
  csvHeader: "Klíč;Název;Stav;Odkaz",
};

export type YoutrackMessages = typeof cs;
export default translate(cs, { en: () => import("./youtrack.en") });
