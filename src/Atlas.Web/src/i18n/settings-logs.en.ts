import type { SettingsLogsMessages } from "./settings-logs";
import { plural } from ".";

const en: SettingsLogsMessages = {
  title: "System log",
  filterLabel: "Log filter",
  level: "Level",
  time: "Time",
  live: "Live",
  request: (id) => `Request ${id}`,
  download: "Download",
  levels: { all: "All", information: "Info and above", warning: "Warnings and above", error: "Errors" },
  times: { any: "Any time", m15: "15 minutes", h1: "1 hour", h24: "24 hours", d7: "7 days" },
  list: {
    loadFailed: "Couldn't load the logs",
    loading: "Loading logs…",
    empty: "No entries",
    copied: "Copied",
    copy: (n) => `Copy ${plural(n, { one: "# entry", other: "# entries" })}`,
    fresh: (n) => plural(n, { one: "# new", other: "# new" }),
    label: "Log entries, newest first",
    loadingOlder: "Loading…",
    older: "Load older",
  },
  row: {
    openRun: (id) => `Open run ${id}`,
    onlyRequest: (id) => `Only request ${id}`,
  },
};

export default en;
