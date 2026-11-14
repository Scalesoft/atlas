import type { IntentId } from "./catalog";
import { detectSecrets } from "./secretDetector";
import { spanOf, spansOverlap, type TextSpan, wordEnd, wordStart, words } from "./span";

/**
 * Deterministic intent rules (design spec §7.4.2 step 1, principle P4), a port of the prototype's IntentRules.cs
 * extended with the phase 1 intents. Pure functions over the text of one block: no editor, no network. The rules
 * propose candidates only; a resolver (H2) confirms them against the authoritative source before anything is offered.
 */
export type CalloutVariant = "note" | "tip" | "important" | "warning" | "caution";

export interface RuleHit {
  readonly intent: IntentId;
  /** Inside the given text, never empty. Block intents span the whole text. */
  readonly span: TextSpan;
  /** Lookup hint for the resolver (issue key, name as written, system name, page id). Never entity data. */
  readonly query?: string;
  /** For `callout.wrap`: the variant named by the opening word. */
  readonly callout?: CalloutVariant;
}

export interface IntentRuleOptions {
  /**
   * Short names of the YouTrack projects allowed in the space (S7a). When given, only their keys are proposed.
   * When unknown (decision O4: no projects endpoint), every key except common standards is a candidate, and the
   * resolver filters it through `resolve` with a negative cache.
   */
  readonly allowedProjects?: Iterable<string>;
  /** The resolver's negative cache: keys `resolve` already reported missing or not visible. */
  readonly isKnownMissingIssue?: (key: string) => boolean;
  /** The pasted range of the text (E6). Atlas page URLs are proposed as page links only when pasted. */
  readonly pasted?: TextSpan;
}

/**
 * Hits in priority order: secrets, YouTrack issues, page links, the block conversion (at most one: table, task list,
 * code, contact card, callout, diagram), the vault reference, inline contacts. Every span is verified to lie inside
 * `text`; `spanOf` throws on a rule bug instead of returning a range that would point elsewhere.
 */
export function detectIntents(text: string, options: IntentRuleOptions = {}): RuleHit[] {
  if (!text.trim()) return [];
  const hits: RuleHit[] = detectSecrets(text).map(secret => ({ intent: "secret.plaintext", span: secret.span }));
  hits.push(...issues(text, options));
  if (options.pasted) hits.push(...pageLinks(text, options.pasted));
  const block = blockIntent(text);
  if (block) hits.push(block);
  const reference = secretReference(text);
  if (reference) hits.push(reference);
  hits.push(...contacts(text));
  return hits;
}

export interface DocumentOutline {
  readonly headings: readonly { readonly level: number }[];
  readonly hasToc: boolean;
}

export interface DocumentRuleHit {
  readonly intent: "toc.insert";
}

/** Document-level rules: a table of contents for six or more H2 headings when the document has none. */
export function detectDocumentIntents(outline: DocumentOutline): DocumentRuleHit[] {
  return !outline.hasToc && outline.headings.filter(heading => heading.level === 2).length >= 6 ? [{ intent: "toc.insert" }] : [];
}

// --- YouTrack -------------------------------------------------------------------------------------------------

/** Prefixes of standards and encodings that look like issue keys ("UTF-8", "ISO-9001"); skipped while projects are unknown. */
const standards = new Set([
  "AES", "ANSI", "CP", "CVE", "CWE", "DIN", "ECMA", "EN", "ES", "GDPR", "HTTP", "IEC", "IEEE", "IPV", "ISO", "JSR", "MD",
  "PCI", "PEP", "RFC", "RSA", "SHA", "SQL", "SSL", "TLS", "UTF", "WIN",
]);
const issueKey = new RegExp(`${wordStart}([A-Z][A-Z0-9]+)-[1-9]\\d*${wordEnd}`, "gu");
const issueUrl = /\/issue\/([A-Z][A-Z0-9]+-[1-9]\d*)(?=[/?#]|$)/;
const url = /https?:\/\/[^\s<>"'„“”]+/giu;
const email = /(?<![A-Za-z0-9._%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+/g;
const trailingPunctuation = /[.,;:!?)\]]+$/;

function issues(text: string, options: IntentRuleOptions): RuleHit[] {
  const allowed = options.allowedProjects ? new Set(options.allowedProjects) : null;
  const accepts = (key: string) => {
    const project = key.slice(0, key.indexOf("-"));
    return (allowed ? allowed.has(project) : !standards.has(project)) && !options.isKnownMissingIssue?.(key);
  };
  const hits: RuleHit[] = [];
  const excluded: TextSpan[] = [];
  for (const match of text.matchAll(url)) {
    const link = match[0].replace(trailingPunctuation, "");
    const span = spanOf(text, match.index, match.index + link.length);
    excluded.push(span);
    const key = issueUrl.exec(link)?.[1];
    if (key && accepts(key)) hits.push({ intent: "youtrack.issue", span, query: key });
  }
  for (const match of text.matchAll(email)) excluded.push(spanOf(text, match.index, match.index + match[0].length));
  for (const match of text.matchAll(issueKey)) {
    const span = spanOf(text, match.index, match.index + match[0].length);
    if (!excluded.some(range => spansOverlap(range, span)) && accepts(match[0])) hits.push({ intent: "youtrack.issue", span, query: match[0] });
  }
  return hits.sort((a, b) => a.span.start - b.span.start);
}

// --- Page links (E6) ------------------------------------------------------------------------------------------

const pageUrl = /(?:https?:\/\/[^\s#<>"']*)?#\/pages\/([A-Za-z0-9%_-]+)(?![A-Za-z0-9%_/-])(?:\?[^\s<>"']*)?/gu;

function pageLinks(text: string, pasted: TextSpan): RuleHit[] {
  const hits: RuleHit[] = [];
  for (const match of text.matchAll(pageUrl)) {
    const link = match[0].replace(trailingPunctuation, "");
    const span = spanOf(text, match.index, match.index + link.length);
    if (span.start < pasted.start || span.end > pasted.end) continue;
    let id = match[1]!;
    try {
      id = decodeURIComponent(id);
    } catch {
      continue;
    }
    hits.push({ intent: "page.link", span, query: id });
  }
  return hits;
}

// --- Block conversions ----------------------------------------------------------------------------------------

const task = new RegExp(`^(?:\\[ \\]|- \\[ \\]|TODO${wordEnd}|úkol:)`, "iu");
const callout = new RegExp(`^(upozornění|pozor|důležité|poznámka|tip|varování)${wordEnd}`, "iu");
const calloutVariants: Record<string, CalloutVariant> = {
  upozornění: "warning", varování: "warning", pozor: "caution", důležité: "important", poznámka: "note", tip: "tip",
};
const arrows = /\S\s*(?:->|→)\s*\S.*(?:->|→)/;
const mermaid = new RegExp(`^\\s*(?:(?:graph|flowchart)\\s+(?:TD|TB|BT|LR|RL)|sequenceDiagram|classDiagram|stateDiagram(?:-v2)?|erDiagram|gantt)${wordEnd}`, "mu");
const codeLine = new RegExp(
  `[{};]\\s*$|^\\s*[{}]|=>|==|!=|&&|\\|\\||:=|^\\s{2,}\\S|[A-Za-z_]\\w*\\([^)]*\\)`
  + `|${wordStart}(?:const|let|var|function|def|class|import|export|return|public|private|using|namespace|SELECT|INSERT|UPDATE|DELETE|echo|sudo)${wordEnd}`,
  "u");

function blockIntent(text: string): RuleHit | null {
  const whole = spanOf(text, 0, text.length);
  const lines = text.split("\n").map(line => line.trim()).filter(Boolean);
  if (isTable(lines)) return { intent: "table.fromText", span: whole };
  if (lines.length >= 2 && lines.every(line => task.test(line))) return { intent: "tasklist.fromText", span: whole };
  if (isCode(text)) return { intent: "code.fromText", span: whole };
  const card = contactCard(text, lines.length);
  if (card) return { intent: "contact.card", span: whole, query: card };
  const opener = callout.exec(text);
  if (opener) return { intent: "callout.wrap", span: whole, callout: calloutVariants[opener[1]!.toLowerCase()] ?? "note" };
  if (arrows.test(text) || mermaid.test(text)) return { intent: "diagram.fromText", span: whole };
  return null;
}

/** At least three lines with the same number of the first line's separator (tab, then semicolon, then pipe). */
function isTable(lines: readonly string[]): boolean {
  const separator = ["\t", ";", "|"].find(candidate => lines[0]?.includes(candidate));
  if (lines.length < 3 || !separator) return false;
  const count = (line: string) => line.split(separator).length - 1;
  return lines.every(line => count(line) === count(lines[0]!));
}

/** A shebang, or at least three lines (and at least half of them) with code characters, keywords or indentation. */
function isCode(text: string): boolean {
  if (/^#!\//.test(text)) return true;
  const lines = text.split("\n").filter(line => line.trim());
  const code = lines.filter(line => codeLine.test(line)).length;
  return code >= 3 && code * 2 >= lines.length;
}

// --- Contacts -------------------------------------------------------------------------------------------------

const namePart = "\\p{Lu}\\p{Ll}+(?:-\\p{Lu}\\p{Ll}+)?";
const fullName = new RegExp(`${wordStart}${namePart}(?:[ \\u00A0]${namePart}){1,2}${wordEnd}`, "gu");
const firstName = new RegExp(fullName.source, "u");
const firstEmail = new RegExp(email.source);
const phone = /(?<![\p{N}+])(?:\+\d{2,3}[ \u00A0]?)?\d{3}[ \u00A0]?\d{3}[ \u00A0]?\d{3}(?!\p{N})/u;
const position = words(
  "vedouc\\p{L}*|ředitel\\p{L}*|manažer\\p{L}*|zástupc\\p{L}*|specialist\\p{L}*|asistent\\p{L}*|referent\\p{L}*|technik\\p{L}*"
  + "|správc\\p{L}*|koordinátor\\p{L}*|konzultant\\p{L}*|jednatel\\p{L}*|obchodník\\p{L}*|CEO|CTO|CFO|COO|manager",
  "iu");
/** Words that hand the reader over to a person (§7.4.3), in their Czech forms. */
const contactTrigger = words(
  "kontakt\\p{L}*|obra[tť]\\p{L}*\\s+se|zavolej\\p{L}*|volej\\p{L}*|správc\\p{L}*|odpovědn\\p{L}*\\s+osob\\p{L}*"
  + "|ozv\\p{L}*\\s+se|napiš\\p{L}*|pište|koleg\\p{L}*");
/** The clause after a trigger ends at a sentence mark; a title or company abbreviation does not end it. */
const clauseEnd = /[!?;\n]|(?<!(?<![\p{L}\p{N}])(?:Ing|Mgr|Bc|Dr|MUDr|JUDr|PhDr|RNDr|doc|prof|s\.r\.o|a\.s))\.(?=\s|$)/u;

/**
 * At least two of name, e-mail, phone and position, one of them a way to reach the person. A single line needs three:
 * "Hlaste na Service Desk přes helpdesk@firma.example.test" is a sentence, not a card.
 */
function contactCard(text: string, lineCount: number): string | undefined {
  const name = firstName.exec(text)?.[0];
  const mail = firstEmail.exec(text)?.[0];
  const call = phone.test(text);
  const signals = [Boolean(name), Boolean(mail), call, position.test(text)].filter(Boolean).length;
  return (mail || call) && signals >= (lineCount >= 2 ? 2 : 3) ? name ?? mail : undefined;
}

/** A full name (two or three capitalised words) or an e-mail address in the clause after a contact trigger. */
function contacts(text: string): RuleHit[] {
  const hits: RuleHit[] = [];
  for (const trigger of text.matchAll(contactTrigger)) {
    const from = trigger.index + trigger[0].length;
    const rest = text.slice(from);
    const clause = rest.slice(0, clauseEnd.exec(rest)?.index ?? rest.length);
    for (const pattern of [fullName, email])
      for (const match of clause.matchAll(pattern)) {
        const span = spanOf(text, from + match.index, from + match.index + match[0].length);
        if (!hits.some(hit => spansOverlap(hit.span, span))) hits.push({ intent: "contact.inline", span, query: match[0] });
      }
  }
  return hits.sort((a, b) => a.span.start - b.span.start);
}

// --- Vault reference ------------------------------------------------------------------------------------------

const credential = words("hesl\\p{L}*|password\\p{L}*|login\\p{L}*|přihlašovací\\p{L}*\\s+údaj\\p{L}*|credentials", "iu");
const vault = words("trezor\\p{L}*|keepass\\p{L}*|uložen\\p{L}*", "iu");
const queryStopWords = new Set(["jsou", "najdete", "naleznete", "najdeš", "bylo", "byla", "mají", "máte", "nebo", "také", "viz", "pro", "the", "are", "for", "from"]);

/** A credential word together with a vault word: offer a reference to the stored secret, with the system as the query. */
function secretReference(text: string): RuleHit | null {
  const word = credential.exec(text);
  const stored = vault.exec(text);
  if (!word || !stored) return null;
  const from = word.index + word[0].length;
  const between = stored.index > word.index ? text.slice(from, stored.index) : text.slice(from).split(/[.!?;\n]/)[0]!;
  const query = (between.match(/[\p{L}\p{N}][\p{L}\p{N}.-]*/gu) ?? [])
    .filter(part => part.length > 2 && !queryStopWords.has(part.toLowerCase()) && !credential.test(part) && !vault.test(part))
    .slice(0, 5).join(" ").slice(0, 120);
  return { intent: "secret.reference", span: spanOf(text, word.index, from), ...(query ? { query } : {}) };
}
