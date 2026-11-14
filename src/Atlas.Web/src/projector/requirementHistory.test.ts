// @vitest-environment node
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it, vi } from "vitest";
import { entryText, historyEntries, historyStamp, historyText, latestEntry, pragueNow } from "./requirementHistory";

afterEach(() => { vi.useRealTimers(); });

describe("a requirement's history in its notes", () => {
  it("reads the list lines that start with a day and maybe a time; anything else is plain text", () => {
    const notes = [
      "Formuláře MPI a PX2, vyplněné.",
      "- 1. 11. 2026 23:40 — Urgováno: IT KBN",
      "- 15. 10. 2026 — telefonát s IT: server bude do pátku",
      "  * 3.2.2026 9:05 – vnořený záznam",
      "- doplnit adresu serveru",
      "- 31. 2. 2026 — den, který není",
      "- 1. 11. 2026 24:00 — čas, který není",
      "1. 11. 2026 — bez odrážky",
      "- 1. 11. 2026 —",
    ].join("\n");
    expect(historyEntries(notes)).toEqual([
      { date: "2026-11-01", time: "23:40", text: "Urgováno: IT KBN" },
      { date: "2026-10-15", text: "telefonát s IT: server bude do pátku" },
      { date: "2026-02-03", time: "09:05", text: "vnořený záznam" },
    ]);
  });

  it("says where the stamp's day and time end, for the editor's decoration", () => {
    expect(historyStamp("1. 11. 2026 23:40 — text")).toMatchObject({ dateEnd: 11, timeStart: 12, timeEnd: 17, textStart: 20 });
    expect(historyStamp("15. 10. 2026 — text")).toMatchObject({ dateEnd: 12, timeStart: undefined, timeEnd: undefined });
    expect(historyStamp("Stav: hotovo")).toBeNull();
  });

  // The same table RequirementTreeTests reads: a copy strips on the server exactly the lines the editor takes for history.
  const shared = JSON.parse(readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), "../../../../tests/fixtures/content/history-stamps.json"), "utf8")) as { name: string; text: string; history: boolean }[];
  it.each(shared)("tells a history line as the server does: $name", ({ text, history }) => {
    expect(historyStamp(text) !== null).toBe(history);
  });

  it("writes a line as the editor inserts it, now in Europe/Prague", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-11-01T22:40:00Z"));
    expect(pragueNow()).toEqual({ date: "2026-11-01", time: "23:40" });
    expect(historyText("Dodáno")).toBe("1. 11. 2026 23:40 — Dodáno");
    expect(historyText("bez času", { date: "2026-10-05" })).toBe("5. 10. 2026 — bez času");
    expect(historyEntries(`- ${historyText("Dodáno")}`)).toEqual([{ date: "2026-11-01", time: "23:40", text: "Dodáno" }]);
  });
});


describe("a requirement's history in its note (plan 3.4 §3)", () => {
  const notes = [
    "Stav: čekáme na IT.",
    "- 15. 10. 2026 14:05 — telefonát s IT: server bude do pátku",
    "- 1. 9. 2026 – vyzvali jsme",
    "- 2. 11. 2026 9:30 - urgováno",
    "- bez data",
    "- 32. 1. 2026 — nemožné datum",
    "1. 10. 2026 — obyčejný odstavec, ne řádek seznamu",
  ].join("\n");

  it("reads the dated list lines, with or without the time, and leaves every other line as text", () => {
    expect(historyEntries(notes)).toEqual([
      { date: "2026-10-15", time: "14:05", text: "telefonát s IT: server bude do pátku" },
      { date: "2026-09-01", text: "vyzvali jsme" },
      { date: "2026-11-02", time: "09:30", text: "urgováno" },
    ]);
    expect(historyEntries("")).toEqual([]);
  });

  it("finds the newest record by its date, not by where it is written", () => {
    expect(latestEntry(historyEntries(notes))).toEqual({ date: "2026-11-02", time: "09:30", text: "urgováno" });
    expect(latestEntry([])).toBeUndefined();
    // Equal stamps: the first line wins, as the newest record is written on top.
    expect(latestEntry(historyEntries("- 2. 11. 2026 — nahoře\n- 2. 11. 2026 — dole"))?.text).toBe("nahoře");
    expect(entryText({ date: "2026-10-15", time: "14:05", text: "telefonát" })).toBe("15. 10. 2026 · 14:05 — telefonát");
    expect(entryText({ date: "2026-09-01", text: "vyzvali jsme" })).toBe("1. 9. 2026 — vyzvali jsme");
  });
});
