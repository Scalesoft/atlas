// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";
import { createPresenceRoster, presenceActivity, userColor } from "./objectPresence";

const at = (userId: string, extra: Record<string, unknown> = {}) => ({
  objectId: "p1", userId, displayName: userId === "u2" ? "Jana" : "Petr", color: "#2a9d8f", payload: { editing: true }, via: "page:p1", ...extra,
});

afterEach(() => { vi.useRealTimers(); });

describe("object presence", () => {
  it("knows who works at each object until they stop or go quiet", () => {
    vi.useFakeTimers();
    const changed = vi.fn();
    const roster = createPresenceRoster(changed, 25_000);
    roster.receive(at("u2"));
    roster.receive(at("u3", { payload: null, via: null }));
    expect(roster.of("p1").map(person => [person.displayName, person.via])).toEqual([["Jana", "page:p1"], ["Petr", null]]);
    expect(roster.of("p2")).toEqual([]);
    roster.receive(at("u2", { payload: { editing: false } }));
    expect(roster.of("p1").map(person => person.userId)).toEqual(["u3"]);
    vi.advanceTimersByTime(25_000);
    expect(roster.of("p1")).toEqual([]);
    expect(changed).toHaveBeenCalledTimes(4);
  });

  it("keeps a person's place and number while their presence is renewed", () => {
    const roster = createPresenceRoster(() => {});
    roster.receive(at("u2"));
    roster.receive(at("u3"));
    const [jana] = roster.of("p1");
    roster.receive(at("u2"));
    expect(roster.of("p1")[0]).toMatchObject({ userId: "u2", clientId: jana!.clientId });
    roster.dispose();
  });

  it("says through which view a colleague works, or that it is elsewhere", () => {
    expect(presenceActivity("Jana", "stránku Provoz")).toBe("Jana upravuje přes stránku Provoz");
    expect(presenceActivity("Jana", null)).toBe("Jana upravuje jinde");
  });

  it("gives a user the server's colour (ViewSessions), from the first group of the id", () => {
    expect(userColor("0000000a-0000-0000-0000-000000000000")).toBe("#c34e62");
    expect(userColor("00000003-ffff-ffff-ffff-ffffffffffff")).toBe("#9c683e");
    expect(userColor("FFFFFFFF-0000-0000-0000-000000000000")).toBe(userColor("ffffffff-0000-0000-0000-000000000000"));
    // 4294967295 % 10 = 5
    expect(userColor("ffffffff-0000-0000-0000-000000000000")).toBe("#ae5f05");
  });

  it("keeps white name tags readable (AA 4.5:1) on every palette colour", () => {
    const luminance = (hex: string) => {
      const [r, g, b] = [1, 3, 5].map(i => Number.parseInt(hex.slice(i, i + 2), 16) / 255).map(v => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
      return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
    };
    for (let i = 0; i < 10; i++) expect(1.05 / (luminance(userColor(`0000000${i}-0000-0000-0000-000000000000`)) + 0.05)).toBeGreaterThanOrEqual(4.5);
  });
});
