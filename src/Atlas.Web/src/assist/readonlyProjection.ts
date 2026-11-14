import { AI_TEXT_LIMIT, aiText } from './aiIntents';
import { detectSecrets } from './rules';
/**
 * Host-only readonly selection: ordinary Markdown text, opaque placeholders for every other element. `text` is what an AI intent
 * may send (P11: null for a secret, only elements, or over the one limit), `searchText` what „Kde se to ještě zmiňuje“ searches (null
 * for a secret); the non-AI actions stay when the AI has nothing to take. A selection inside a heading points at its section
 * (`section` is the heading's text): the AI intents then act on the heading and everything under it, or on the selection itself
 * when the section cannot be sent (over the limit, a secret), so the intents never vanish because the target grew.
 */
export interface ReadonlySelectionProjection { readonly text: string | null; readonly searchText: string | null; readonly section?: string }
const ordinary = new Set(['P','H1','H2','H3','H4','H5','H6','UL','OL','LI','BLOCKQUOTE','PRE','CODE','STRONG','B','EM','I','S','DEL','U','MARK','BR','HR','TABLE','THEAD','TBODY','TFOOT','TR','TD','TH']);
const block = new Set(['P','H1','H2','H3','H4','H5','H6','UL','OL','LI','BLOCKQUOTE','PRE','TABLE','TR']);
const headings = 'h1,h2,h3,h4,h5,h6';
// A heading's anchor id is the editor's own (heading-anchors), not a host component: its text is ordinary.
const hostData = (node: HTMLElement) => [...node.attributes].some(attr => attr.name.startsWith('data-') && attr.name !== 'data-heading-id');
export function projectReadonlySelection(root: HTMLElement, range: Range): ReadonlySelectionProjection | null {
  if (range.collapsed || !root.contains(range.startContainer) || !root.contains(range.endContainer)) return null;
  const nonce = Array.from(crypto.getRandomValues(new Uint8Array(8)), byte => byte.toString(16).padStart(2,'0')).join('');
  const own = collect(root, range, nonce), heading = sectionOf(root, range);
  // The section stops being read once it passes the limit: a long page costs a selectionchange nothing (P17).
  const whole = heading ? collect(root, heading.range, nonce, AI_TEXT_LIMIT) : null;
  const section = whole && sendable(whole) ? heading : null, ai = section ? whole! : own;
  const searchable = !own.unsafe && !!own.ordinary && !detectSecrets(own.text).length;
  return { text: sendable(ai) ? ai.text : null, searchText: searchable ? own.ordinary : null, ...(section ? { section: section.title } : {}) };
}
function sendable(ai: ReturnType<typeof collect>) {
  return !ai.unsafe && !ai.over && !!aiText(ai.text) && !!ai.text.replace(/⟦c:[a-f0-9]{16}:[1-9][0-9]{0,5}⟧/g,'').trim();
}

/** The heading the whole selection lies in, and the range of its section: up to the next heading of the same or a higher level. */
function sectionOf(root: HTMLElement, range: Range): { range: Range; title: string } | null {
  const start = (range.startContainer instanceof Element ? range.startContainer : range.startContainer.parentElement)?.closest<HTMLElement>(headings);
  if (!start || !root.contains(start)) return null;
  // Chrome ends a triple-click (or a drag past the line) at the start of the next block: nothing selected after the heading still counts.
  const tail = root.ownerDocument.createRange(); tail.setStart(start, start.childNodes.length); tail.setEnd(range.endContainer, range.endOffset);
  if (tail.toString().trim()) return null;
  let top: HTMLElement = start;
  while (top.parentElement && top.parentElement !== root) top = top.parentElement;
  const level = Number(start.tagName.slice(1)), section = root.ownerDocument.createRange();
  section.setStartBefore(top); section.setEndAfter(root.lastChild!);
  for (let next = top.nextElementSibling; next; next = next.nextElementSibling) {
    const heading = next.matches(headings) ? next : null;
    if (heading && Number(heading.tagName.slice(1)) <= level) { section.setEndBefore(next); break; }
  }
  return { range: section, title: start.textContent?.trim() ?? '' };
}

function collect(root: HTMLElement, range: Range, nonce: string, limit = Infinity) {
  let count = 0, unsafe = false, length = 0, over = false;
  const parts: string[] = [], ordinaryParts: string[] = [];
  const append = (text: string, opaque = false) => { parts.push(text); if (!opaque) ordinaryParts.push(text); if ((length += text.length) > limit) over = true; };
  const opaque = (node: HTMLElement) => (!ordinary.has(node.tagName) && !(node.tagName === 'DIV' && node.className === 'tableWrapper' && node.children.length === 1 && node.firstElementChild?.tagName === 'TABLE')) || hostData(node);
  function ordinaryText(node: Node): string {
    if (node.nodeType === Node.TEXT_NODE) return node.nodeValue ?? '';
    if (!(node instanceof HTMLElement) || node.hidden || node.getAttribute('aria-hidden') === 'true' || opaque(node)) return '￼';
    return Array.from(node.childNodes, ordinaryText).join('');
  }
  function visit(node: Node) {
    if (over || !range.intersectsNode(node)) return;
    if (node.nodeType === Node.TEXT_NODE) {
      const value = node.nodeValue ?? '';
      const start = node === range.startContainer ? range.startOffset : 0;
      const end = node === range.endContainer ? range.endOffset : value.length;
      const text = value.slice(start,end);
      if (/[⟦⟧]/u.test(text)) unsafe = true;
      append(text); return;
    }
    if (!(node instanceof HTMLElement)) return;
    if (node.hidden || node.getAttribute('aria-hidden') === 'true') return;
    if (block.has(node.tagName) && !opaque(node) && detectSecrets(ordinaryText(node)).length) { unsafe = true; return; }
    const tableWrapper = node.tagName === 'DIV' && node.className === 'tableWrapper' && node.children.length === 1 && node.firstElementChild?.tagName === 'TABLE';
    // Unknown data attributes mark host components even when they use a standard HTML tag. Never read their descendants.
    if ((!ordinary.has(node.tagName) && !tableWrapper) || hostData(node)) { append(`⟦c:${nonce}:${++count}⟧`, true); return; }
    if (node.tagName === 'BR' || node.tagName === 'HR') append('\n');
    else node.childNodes.forEach(visit);
    if (block.has(node.tagName)) append('\n');
  }
  root.childNodes.forEach(visit);
  return { text: parts.join('').trim(), ordinary: ordinaryParts.join('').trim(), unsafe, over };
}
