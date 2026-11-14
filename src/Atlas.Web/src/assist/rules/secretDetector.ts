import { spanOf, spansOverlap, type TextSpan, wordStart } from "./span";

/**
 * Plaintext secrets in a text block (E7). Runs only in the browser: the result carries ranges, never the value, and
 * nothing here stores, logs or sends the text. A block with a hit must never reach a model (design spec §7.4.3).
 */
export type SecretKind = "prefixed" | "key-format" | "private-key" | "high-entropy";

export interface SecretHit {
  readonly kind: SecretKind;
  readonly span: TextSpan;
}

const keyword = "hesl[oa]|password|passwd|pwd|token|secret|api[ _-]?key|api klíč|access[_-]?key|private[_-]?key|client[_-]?secret";
/**
 * `heslo: x`, `DB_PASSWORD=x`, `api klíč: x`, `AWS_SECRET_ACCESS_KEY = x`: a whole name, then a keyword among its
 * segments. A name (letters with diacritics, digits) starts only where no `letter + separator` precedes it, so a run like `token_token_…` is scanned
 * once, not once per start and split (that froze the tab for seconds).
 */
const assignment = /(?<![\p{L}\p{N}]|[\p{L}\p{N}][_.-])((?:[\p{L}\p{N}]+[_.-])*api (?:key|klíč)(?:[_.-][\p{L}\p{N}]+)*|[\p{L}\p{N}]+(?:[_.-][\p{L}\p{N}]+)*)\s*[:=]\s*/giu;
const assignedKeyword = new RegExp(`(?:^|[_.-])(?:${keyword})(?:[_.-]|$)`, "iu");
/** `heslo Srv#2026!rack`, `heslo je …`: without a separator only a value shaped like a password counts. */
const bare = new RegExp(`${wordStart}(?:heslo|password|pwd|token|api klíč)(?:\\s+(?:je|is))?[ \\t\\u00A0]+`, "giu");
const keyFormat = new RegExp(
  "(?<![\\p{L}\\p{N}_-])(?:sk-[A-Za-z0-9-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{22,}|glpat-[A-Za-z0-9_-]{20,}"
  + "|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16})(?![\\p{L}\\p{N}_])", "gu");
/** PEM, OpenSSH and PGP private key blocks, up to the end line (or the end of the text when it is cut off). */
const privateKey = /-----BEGIN [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----|$)/g;
/** A pure hex token next to one of these words is a checksum or a commit, not a secret. */
const hashContext = new RegExp(
  `${wordStart}(?:sha(?:-?\\d+)?|md5|hash\\p{L}*|checksum\\p{L}*|commit\\p{L}*|git|revi[sz]\\p{L}*|otisk\\p{L}*|kontrolní\\s+součet\\p{L}*)(?![\\p{L}])`, "iu");

const ascii = /^[\x21-\x7E]+$/;
const leadingQuote = /^["'„“‚‘«`]+/;
const trailingPunctuation = /["'“”‘’»`.,;:)\]]+$/;

const entropyThreshold = 3.5;
const entropyMinLength = 12;
const prefixedMinLength = 8;
const classChangeThreshold = 0.45;
const maxRandomPieceLength = 2.5;
/** Hex keys carry at least 128 bits; shorter hex runs are abbreviated commits and IDs. */
const hexMinLength = 32;

export function detectSecrets(text: string): SecretHit[] {
  const hits: SecretHit[] = [];
  const add = (kind: SecretKind, start: number, end: number) => {
    const span = spanOf(text, start, end);
    if (!hits.some(hit => spansOverlap(hit.span, span))) hits.push({ kind, span });
  };

  for (const match of text.matchAll(privateKey)) add("private-key", match.index, match.index + match[0].length);
  for (const match of text.matchAll(keyFormat)) {
    const value = match[0].replace(/-+$/, "");
    add("key-format", match.index, match.index + value.length);
  }
  for (const match of text.matchAll(assignment)) {
    if (!assignedKeyword.test(match[1]!)) continue;
    const token = tokenAt(text, match.index + match[0].length);
    if (token && isPrefixedValue(token.value, false)) add("prefixed", token.start, token.end);
  }
  for (const match of text.matchAll(bare)) {
    const token = tokenAt(text, match.index + match[0].length);
    if (token && isPrefixedValue(token.value, true)) add("prefixed", token.start, token.end);
  }
  const hexIsHash = hashContext.test(text);
  for (const match of text.matchAll(/\S+/g)) {
    const token = trimToken(text, match.index, match.index + match[0].length);
    if (!token || isLookAlike(token.value)) continue;
    const value = assignedValue(token);
    if (isHighEntropy(value.value, hexIsHash)) add("high-entropy", value.start, value.end);
  }
  return hits.sort((a, b) => a.span.start - b.span.start);
}

interface Token {
  readonly value: string;
  readonly start: number;
  readonly end: number;
}

function tokenAt(text: string, start: number): Token | null {
  const run = /^\S+/.exec(text.slice(start, start + 512));
  return run ? trimToken(text, start, start + run[0].length) : null;
}

function trimToken(text: string, start: number, end: number): Token | null {
  const raw = text.slice(start, end);
  const lead = leadingQuote.exec(raw)?.[0].length ?? 0;
  const value = raw.slice(lead).replace(trailingPunctuation, "");
  return value ? { value, start: start + lead, end: start + lead + value.length } : null;
}

/** `KEY=value` or `key:value` in one token: the value is the candidate. Base64 padding (`…NQ==`) is not a separator. */
function assignedValue(token: Token): Token {
  const separator = /[=:]/.exec(token.value);
  if (!separator || !/^[A-Za-z_][\w.-]*$/.test(token.value.slice(0, separator.index))) return token;
  const offset = separator.index + 1;
  if (/^=*$/.test(token.value.slice(offset))) return token;
  return { value: token.value.slice(offset), start: token.start + offset, end: token.end };
}

/**
 * The value after a keyword must look like a literal. Code (`token = provider.ClientSecret`, `password =
 * Hash(input)`), markup and a plain word (`Token: platnost 30 dní`) do not count. Without a separator
 * (`heslo Srv#2026!rack`) it also needs two character classes and a digit or symbol.
 */
function isPrefixedValue(value: string, bareKeyword: boolean): boolean {
  if (value.length < prefixedMinLength || !ascii.test(value) || isLookAlike(value) || /[(){}<>[\]"'`;]/.test(value)) return false;
  if (isIdentifierPath(value) || (!/\d/.test(value) && /^[A-Za-z]+(?:[-_][A-Za-z]+)*[^A-Za-z0-9]*$/.test(value))) return false;
  return !bareKeyword || (characterClasses(value) >= 2 && /[^A-Za-z]/.test(value));
}

/** `site.Password`, `value?.UtcDateTime`, `auth_time`, `account/stamp/scope`: member access, names and paths. */
function isIdentifierPath(value: string): boolean {
  const normalized = value.replace(/[?!]\./g, ".");
  return /^[A-Za-z0-9._/:-]+$/.test(normalized) && /[._/:]/.test(normalized)
    && normalized.split(/[._/:-]+/).every(part => /^[A-Za-z]*\d*$/.test(part));
}

/**
 * A machine token without a keyword: base64 or hex characters only, two character classes, Shannon entropy of at
 * least 3.5 bits per character and a random structure. Identifiers (`X509Certificate2`, `Argon2Iterations`,
 * `e2e-podman.ps1`) switch between lowercase, uppercase and digits rarely, random tokens at almost every other
 * character. Next to "sha", "hash" or "commit" a pure hex token is a checksum; versions with build metadata
 * (`0.1.1+672459f5…`) are not secrets either.
 */
function isHighEntropy(value: string, hexIsHash: boolean): boolean {
  if (value.length < entropyMinLength || !/^[A-Za-z0-9+/=_.~-]+$/.test(value) || characterClasses(value) < 2) return false;
  if (isLookAlike(value) || /^(?:sha\d+|md5)[-:]/i.test(value) || /^AAAA[A-Za-z0-9]{1,2}(?:NzaC1|VjZHNh)/.test(value)) return false;
  if (/^\d+\.\d+\.\d+[+-]/.test(value)) return false;
  const hex = /^[0-9a-f]+$/i.test(value);
  if (hex ? hexIsHash || value.length < hexMinLength : !looksRandom(value)) return false;
  return shannonEntropy(value) >= entropyThreshold;
}

/**
 * Identifiers split into words (`DateTime.UtcNow` → Date, Time, Utc, Now) and switch character class at word
 * boundaries only; a random token splits into pieces of one or two characters and switches almost everywhere.
 */
function looksRandom(value: string): boolean {
  const pieces = value.match(/[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+/g) ?? [];
  const length = pieces.reduce((sum, piece) => sum + piece.length, 0);
  return pieces.length > 0 && length / pieces.length <= maxRandomPieceLength && classChangeRate(value) >= classChangeThreshold;
}

/** Share of neighbouring alphanumeric characters that switch between lowercase, uppercase and digit. */
function classChangeRate(value: string): number {
  const kind = (character: string) => (/\d/.test(character) ? 0 : character === character.toLowerCase() ? 1 : 2);
  let pairs = 0;
  let changes = 0;
  for (const part of value.split(/[^A-Za-z0-9]+/))
    for (let index = 1; index < part.length; index++) {
      pairs++;
      if (kind(part[index - 1]!) !== kind(part[index]!)) changes++;
    }
  return pairs ? changes / pairs : 0;
}

/** URLs, e-mail addresses, paths, UUIDs and other hex groups (IP and MAC addresses), placeholders. */
function isLookAlike(value: string): boolean {
  return /^[a-z][a-z0-9+.-]*:\/\//i.test(value)
    || /^www\./i.test(value)
    || /^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$/.test(value)
    || /^(?:\/|\.{1,2}\/|~\/|\\\\|[A-Za-z]:[\\/])/.test(value) || value.includes("\\")
    || /^(?:[0-9a-f]{0,8}[:.-])+[0-9a-f]{1,12}$/i.test(value)
    || /\$\{|\{\{|^%[^%]+%$|^<[^>]+>$|^\$[A-Z_][A-Z0-9_]*$/.test(value)
    || /^(.)\1+$/.test(value);
}

function characterClasses(value: string): number {
  return [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter(pattern => pattern.test(value)).length;
}

/** Shannon entropy in bits per character. */
function shannonEntropy(value: string): number {
  const counts = new Map<string, number>();
  for (const character of value) counts.set(character, (counts.get(character) ?? 0) + 1);
  let entropy = 0;
  for (const count of counts.values()) {
    const p = count / value.length;
    entropy -= p * Math.log2(p);
  }
  return entropy;
}
