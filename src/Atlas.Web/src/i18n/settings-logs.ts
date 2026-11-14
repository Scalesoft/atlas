import { plural, translate } from ".";

/** Správa wiki → Systémový log (settings/logs: LogsPage, LogList, LogRow). The Czech source. */
const cs = {
  title: "Systémový log",
  filterLabel: "Filtr logu",
  level: "Úroveň",
  time: "Čas",
  live: "Živě",
  request: (id: string) => `Požadavek ${id}`,
  download: "Stáhnout",
  levels: { all: "Vše", information: "Info a výš", warning: "Varování a výš", error: "Chyby" },
  times: { any: "Kdykoli", m15: "15 minut", h1: "1 hodina", h24: "24 hodin", d7: "7 dní" },
  list: {
    loadFailed: "Logy se nepodařilo načíst",
    loading: "Načítání logů…",
    empty: "Žádné záznamy",
    copied: "Zkopírováno",
    copy: (n: number) => `Kopírovat ${plural(n, { one: "# záznam", few: "# záznamy", other: "# záznamů" })}`,
    fresh: (n: number) => plural(n, { one: "# nový", few: "# nové", other: "# nových" }),
    label: "Záznamy logu, od nejnovějšího",
    loadingOlder: "Načítání…",
    older: "Načíst starší",
  },
  row: {
    openRun: (id: number) => `Otevřít běh ${id}`,
    onlyRequest: (id: string) => `Jen požadavek ${id}`,
  },
};
export type SettingsLogsMessages = typeof cs;

export default translate(cs, { en: () => import("./settings-logs.en") });
