// @vitest-environment node
import { describe, expect, it } from "vitest";
import { formatAddressCount, parseCidr, rangeWarnings } from "./cidr";

describe("CIDR parsing for trusted networks", () => {
  it("normalises to the start of the range and counts addresses in Czech", () => {
    const range = parseCidr("10.4.1.7/22");
    expect(range.ok && range.cidr).toBe("10.4.0.0/22");
    expect(range.ok && range.adjusted).toBe(true);
    expect(range.ok && formatAddressCount(range.size)).toBe(`1${" "}024 adres`);
    const office = parseCidr("10.5.0.0/16");
    expect(office.ok && formatAddressCount(office.size).replace(/\s/g, " ")).toBe("65 536 adres");
    const single = parseCidr(" 198.51.100.7 ");
    expect(single.ok && single.single && formatAddressCount(single.size)).toBe("1 adresa");
    const v6 = parseCidr("2001:db8::1/64");
    expect(v6.ok && v6.cidr).toBe("2001:db8::/64");
    expect(v6.ok && formatAddressCount(v6.size)).toBe("2^64 adres");
  });

  it("rejects invalid input with a readable reason", () => {
    expect(parseCidr("10.0.0.256/8")).toMatchObject({ ok: false });
    expect(parseCidr("10.0.0.0/33")).toMatchObject({ ok: false, error: expect.stringContaining("0–32") });
    expect(parseCidr("")).toMatchObject({ ok: false });
  });

  it("warns about overlaps, public and too broad ranges and blocks the whole internet", () => {
    const existing = [{ name: "Kancelář Praha", cidr: "10.5.0.0/16" }];
    const overlap = rangeWarnings(parseCidr("10.5.8.0/24") as never, existing);
    expect(overlap).toEqual([expect.objectContaining({ kind: "overlap", blocking: false })]);
    const same = rangeWarnings(parseCidr("10.5.0.0/16") as never, existing);
    expect(same[0]).toMatchObject({ kind: "overlap", blocking: true });
    expect(rangeWarnings(parseCidr("0.0.0.0/0") as never, [])[0]).toMatchObject({ kind: "internet", blocking: true });
    expect(rangeWarnings(parseCidr("198.51.100.7") as never, []).map((warning) => warning.kind)).toEqual(["public"]);
    expect(rangeWarnings(parseCidr("10.0.0.0/7") as never, []).map((warning) => warning.kind)).toContain("broad");
  });
});
