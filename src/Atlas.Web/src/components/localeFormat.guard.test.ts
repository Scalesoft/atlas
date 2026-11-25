import { globSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

// One mechanism per concept: dates, times and numbers are formatted by formatDate.ts / formatNumber in the language of the tool,
// never by an own Intl or toLocale* call, and never with a hard-coded Czech locale.
const banned = /toLocale(Date|Time)?String\(|new Intl\.DateTimeFormat|"cs-CZ"|toLocaleUpperCase\("cs"\)/;
// Files that may still format by themselves. Shrink this list, never grow it.
const allowed = new Set([
  "formatDate.ts", // the one module
  "i18n/index.ts", // defines the locale
  "test-setup.ts",
  "components/settings/logs/logFormat.ts", // millisecond timestamps of the log
  "components/ActivityHeatmap.vue", // month names of the heatmap axis
  "App.vue", // baseline: initials in Czech, content count in cs-CZ (own package)
  "components/dev/AppearancePickerExample.vue", // gallery example
  "components/library/SpaceCoverField.vue", // baseline: file size
  "components/settings/vaults/VaultWizard.vue", // baseline: KiB size and Czech capital
]);

describe("locale formatting", () => {
  it("goes through formatDate.ts and formatNumber", () => {
    const root = resolve(__dirname, "..");
    const offenders = globSync("**/*.{ts,vue}", { cwd: root })
      .map((file) => file.replaceAll("\\", "/"))
      .filter((file) => !file.endsWith(".test.ts") && !allowed.has(file))
      .filter((file) => banned.test(readFileSync(resolve(root, file), "utf8")));
    expect(offenders).toEqual([]);
  });
});
