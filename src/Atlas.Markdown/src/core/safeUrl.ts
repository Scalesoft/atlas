/** Protocols a document link may use: the `set-link` command and imported wiki links/embeds. */
export const MARKDOWN_LINK_PROTOCOLS: readonly string[] = ['http:', 'https:', 'mailto:', 'tel:']
/** Protocols an imported image may load from. */
export const MARKDOWN_IMAGE_PROTOCOLS: readonly string[] = ['http:', 'https:']

/**
 * The URL itself when it is unambiguous and uses one of `protocols`, else null. URL() silently
 * percent-encodes whitespace, but the raw href is serialized to Markdown, so whitespace and control
 * characters are rejected (callers may supply an encoded URL). The protocol comes from browser URL
 * parsing (which strips ASCII control whitespace), not from a prefix regex.
 */
export function safeMarkdownUrl(value: unknown, protocols: readonly string[] = MARKDOWN_LINK_PROTOCOLS): string | null {
  if (typeof value !== 'string' || !value || /[\s\u0000-\u001f\u007f]/u.test(value)) return null
  try { return protocols.includes(new URL(value, 'https://editor.invalid/').protocol) ? value : null }
  catch { return null }
}
