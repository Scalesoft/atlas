/*
 * Named patterns of the linking engine (audit-engine §3): the source of each is read the same by JavaScript (flag `u`) and .NET, and the
 * entity kind that recognizes it declares it by this name (defineMarkdownEntityKind `patterns`), never a copy of the expression.
 * Data only: the plugins load it with every editor; the decision over it (recognize.ts `issueKeyAtEnd`) loads with the lazy engine.
 */

/** A YouTrack issue key: project, hyphen, number („PX2-1235“). */
export const ISSUE_KEY_PATTERN = '[A-Z][A-Z0-9]+-[1-9][0-9]{0,6}'
/** Prefixes of standards and encodings that look like issue keys („UTF-8“, „ISO-9001“): never an issue, whatever the projects are. */
export const ISSUE_KEY_DENY: ReadonlySet<string> = new Set(['AES', 'ANSI', 'CP', 'CVE', 'CWE', 'CSN', 'DIN', 'ECMA', 'EN', 'ES', 'GDPR', 'HTTP', 'IEC',
  'IEEE', 'IPV', 'ISO', 'JSR', 'MD', 'PCI', 'PEP', 'RFC', 'RSA', 'SHA', 'SQL', 'SSL', 'TLS', 'UTF', 'WIN'])
