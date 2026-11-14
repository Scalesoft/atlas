import { afterEach, describe, expect, it, vi } from "vitest";
import { readStored, writeStored } from "./browserStorage";

afterEach(() => { vi.unstubAllGlobals(); localStorage.clear(); });

describe("browser storage", () => {
  it("reads back what it wrote, and null for a key that was never set", () => {
    writeStored("atlas:test", "wide");
    expect(readStored("atlas:test")).toBe("wide");
    expect(readStored("atlas:missing")).toBeNull();
  });

  it("treats unavailable storage as empty and ignores writes to it", () => {
    const blocked = { getItem: () => { throw new DOMException("denied", "SecurityError"); }, setItem: () => { throw new DOMException("full", "QuotaExceededError"); } };
    vi.stubGlobal("localStorage", blocked);
    expect(readStored("atlas:test")).toBeNull();
    expect(() => writeStored("atlas:test", "wide")).not.toThrow();
  });
});
