/**
 * IPv4/IPv6 address and CIDR parsing for the network rule form: normalisation to the start of
 * the range, address counts in Czech, overlap and "too broad"/public checks, and membership for
 * the connection tester. Pure functions; the server will validate the same rules again.
 */
import t from "../../../i18n/settings-security";

export type IpFamily = 4 | 6;

export interface ParsedRange {
  ok: true;
  family: IpFamily;
  /** Normalised `network/prefix`. */
  cidr: string;
  network: string;
  prefix: number;
  first: bigint;
  last: bigint;
  size: bigint;
  /** True when the host bits were set and the range was moved to its start. */
  adjusted: boolean;
  /** True when the input had no prefix and denotes one address. */
  single: boolean;
}
export interface ParseError {
  ok: false;
  error: string;
}

function parseV4(text: string): bigint | undefined {
  const parts = text.split(".");
  if (parts.length !== 4) return undefined;
  let value = 0n;
  for (const part of parts) {
    if (!/^\d{1,3}$/.test(part)) return undefined;
    const number = Number(part);
    if (number > 255) return undefined;
    value = (value << 8n) | BigInt(number);
  }
  return value;
}

function parseV6(text: string): bigint | undefined {
  if (!/^[0-9a-fA-F:.]+$/.test(text) || !text.includes(":")) return undefined;
  const halves = text.split("::");
  if (halves.length > 2) return undefined;
  const expand = (part: string) => (part ? part.split(":") : []);
  let head = expand(halves[0]!);
  let tail = halves.length === 2 ? expand(halves[1]!) : [];
  // An embedded IPv4 tail (::ffff:10.0.0.1) counts as two groups.
  const lastGroup = (tail.length ? tail : head).at(-1);
  if (lastGroup?.includes(".")) {
    const v4 = parseV4(lastGroup);
    if (v4 === undefined) return undefined;
    const groups = [((v4 >> 16n) & 0xffffn).toString(16), (v4 & 0xffffn).toString(16)];
    if (tail.length) tail = [...tail.slice(0, -1), ...groups];
    else head = [...head.slice(0, -1), ...groups];
  }
  const missing = 8 - head.length - tail.length;
  if (halves.length === 1 ? missing !== 0 : missing < 1) return undefined;
  const groups = [...head, ...Array<string>(halves.length === 2 ? missing : 0).fill("0"), ...tail];
  let value = 0n;
  for (const group of groups) {
    if (!/^[0-9a-fA-F]{1,4}$/.test(group)) return undefined;
    value = (value << 16n) | BigInt(parseInt(group, 16));
  }
  return value;
}

export function formatAddress(value: bigint, family: IpFamily): string {
  if (family === 4) return [24n, 16n, 8n, 0n].map((shift) => String((value >> shift) & 255n)).join(".");
  const groups = Array.from({ length: 8 }, (_, index) => ((value >> BigInt((7 - index) * 16)) & 0xffffn).toString(16));
  // Compress the longest run of zero groups (RFC 5952).
  let bestStart = -1;
  let bestLength = 0;
  for (let start = 0; start < 8; start++) {
    let length = 0;
    while (start + length < 8 && groups[start + length] === "0") length++;
    if (length > bestLength && length > 1) {
      bestStart = start;
      bestLength = length;
    }
  }
  if (bestStart < 0) return groups.join(":");
  const left = groups.slice(0, bestStart).join(":");
  const right = groups.slice(bestStart + bestLength).join(":");
  return `${left}::${right}`;
}

export function parseAddress(text: string): { family: IpFamily; value: bigint } | undefined {
  const trimmed = text.trim();
  const v4 = parseV4(trimmed);
  if (v4 !== undefined) return { family: 4, value: v4 };
  const v6 = parseV6(trimmed);
  if (v6 !== undefined) return { family: 6, value: v6 };
  return undefined;
}

export function parseCidr(input: string): ParsedRange | ParseError {
  const text = input.trim();
  if (!text) return { ok: false, error: t.cidr.empty };
  const [addressText = "", prefixText, extra] = text.split("/");
  if (extra !== undefined) return { ok: false, error: t.cidr.oneSlash };
  const address = parseAddress(addressText);
  if (!address) return { ok: false, error: t.cidr.notAddress };
  const bits = address.family === 4 ? 32 : 128;
  let prefix = bits;
  if (prefixText !== undefined) {
    if (!/^\d{1,3}$/.test(prefixText)) return { ok: false, error: t.cidr.prefix(bits) };
    prefix = Number(prefixText);
    if (prefix > bits) return { ok: false, error: t.cidr.prefix(bits) };
  }
  const hostBits = BigInt(bits - prefix);
  const size = 1n << hostBits;
  const first = (address.value >> hostBits) << hostBits;
  const last = first + size - 1n;
  const network = formatAddress(first, address.family);
  return {
    ok: true,
    family: address.family,
    cidr: `${network}/${prefix}`,
    network,
    prefix,
    first,
    last,
    size,
    adjusted: first !== address.value,
    single: prefix === bits,
  };
}

/** "1 adresa", "4 adresy", "65 536 adres"; IPv6 sizes as powers of two. */
export function formatAddressCount(size: bigint): string {
  if (size > 1_000_000_000_000n) {
    const exponent = size.toString(2).length - 1;
    return t.cidr.manyAddresses(exponent);
  }
  return t.cidr.addresses(Number(size));
}

export function rangesOverlap(a: ParsedRange, b: ParsedRange) {
  return a.family === b.family && a.first <= b.last && b.first <= a.last;
}

export function rangeContains(range: ParsedRange, address: { family: IpFamily; value: bigint }) {
  return range.family === address.family && address.value >= range.first && address.value <= range.last;
}

const privateV4 = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16"];
const privateV6 = ["fc00::/7", "fe80::/10", "::1/128"];

export function isPrivateRange(range: ParsedRange) {
  const list = range.family === 4 ? privateV4 : privateV6;
  return list.some((text) => {
    const known = parseCidr(text) as ParsedRange;
    return range.first >= known.first && range.last <= known.last;
  });
}

export type RangeWarning = { kind: "internet" | "broad" | "public" | "overlap" | "adjusted"; message: string; blocking: boolean };

/** Warnings shown under the field; `blocking` ones stop the flow. */
export function rangeWarnings(range: ParsedRange, existing: { name: string; cidr: string }[]): RangeWarning[] {
  const warnings: RangeWarning[] = [];
  if (range.prefix === 0) warnings.push({ kind: "internet", message: t.cidr.internet, blocking: true });
  else if ((range.family === 4 && range.prefix < 8) || (range.family === 6 && range.prefix < 32))
    warnings.push({ kind: "broad", message: t.cidr.broad, blocking: false });
  if (range.prefix > 0 && !isPrivateRange(range))
    warnings.push({ kind: "public", message: t.cidr.public, blocking: false });
  for (const rule of existing) {
    const other = parseCidr(rule.cidr);
    if (!other.ok || !rangesOverlap(range, other)) continue;
    const same = other.first === range.first && other.last === range.last;
    warnings.push({
      kind: "overlap",
      message: same ? t.cidr.sameRange(rule.name) : t.cidr.overlap(rule.name, other.cidr),
      blocking: same,
    });
  }
  return warnings;
}
