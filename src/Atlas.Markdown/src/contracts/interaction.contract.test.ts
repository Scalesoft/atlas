// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import type { Editor, JSONContent } from '@tiptap/core'
import { NodeSelection } from '@tiptap/pm/state'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin } from '../core'
import { editorForController } from '../core/internal'
import { configureMarkdownEditorView } from '../vue/controllerView'
import { createAttachmentsPlugin } from '../plugins/attachments'
import { createChartsPlugin } from '../plugins/charts'
import { createConcealedContentPlugin } from '../plugins/concealed'
import { createContactsPlugin } from '../plugins/contacts'
import { createDiagramsPlugin } from '../plugins/diagrams'
import { createCodeBlockHighlighting, createEditorToolsPlugin } from '../plugins/editor-tools'
import { createMathPlugin } from '../plugins/math'
import { createOpenApiPlugin, defaultOpenApi } from '../plugins/openapi'
import { createPagesPlugin } from '../plugins/pages'
import { createSecretsPlugin } from '../plugins/secrets'
import { createTocPlugin } from '../plugins/toc'
import { createYouTrackPlugin, pickYouTrackIssue } from '../plugins/youtrack'
import deviations from './interaction-deviations.json'

/*
 * The interaction contract of the page editor (docs/interaction-rules.md, P16): every block and inline chip in a real editor with
 * every plugin, one descriptor per element, the same probes for all. A deviation in interaction-deviations.json is accepted while it
 * still fails; when it passes, the test asks for the row to be deleted. A node type of the full editor without a descriptor (or a
 * reason in `plainNodes`) fails the registration test, so a new component cannot skip the rules. Atlas-only blocks (the requirement)
 * have their own runner in src/Atlas.Web/src/contracts. The check ids are the web's `interactionChecks` (src/Atlas.Web/src/contracts/interaction.ts).
 */

vi.mock('mermaid', () => ({ default: { initialize() {}, render: async () => ({ svg: '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"></svg>' }) } }))
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }
document.elementFromPoint ??= () => null
HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute('open', '') }
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { if (this.open) { this.removeAttribute('open'); this.dispatchEvent(new Event('close')) } }

type Check = 'P1.start' | 'P1.enter' | 'P1.toggle' | 'P2.menu' | 'P3.esc' | 'P3.esc-second' | 'P4.outside' | 'P4.blur' | 'P6.undo' | 'P7.focus' | 'P12.selected' | 'P17.sync'
interface Deviation { element: string; check: string; reason: string; ref?: string }

interface Descriptor {
  readonly id: string
  readonly label: string
  /** P5: `live` saves as it goes (Esc closes and keeps), `draft` a single-line concept Esc throws away, `apply` a panel with „Použít“, `none` no value. */
  readonly saves: 'live' | 'draft' | 'apply' | 'none'
  /** P17: how it works at 375 px with touch, for the browser review cycles (jsdom has no layout or touch). */
  readonly touch: string
  /** The node type and the node; an inline chip sits in a paragraph. */
  readonly type: string
  readonly node: JSONContent
  readonly inline?: boolean
  /** Where a click starts the editing (in the node's DOM); the node's DOM itself by default. */
  readonly target?: string
  /** A block whose surface is read, not edited (contract 3.6, J2.1): a click only selects it, Enter or F2 on the selected block starts the editing. */
  readonly selectsOnClick?: boolean
  /** A link chip (contract J3.5): a click follows the link and opens nothing, Enter or F2 on the selected chip (or its toolbar) starts the editing. */
  readonly followsOnClick?: boolean
  /** What is open while it is edited (in the document); by default any popover, dialog or a focused field of the element. */
  readonly editing?: string
  /** The field the edit types into (in the document), the value typed, and where the value is read from the node. */
  readonly field?: string
  readonly value?: string
  /** A panel of choices (tiles) with no field to type in: the choice clicked as the edit (instead of `field` and `value`). */
  readonly choose?: string
  readonly read?: (node: JSONContent) => unknown
  /** How the edit is finished in its own field: Enter (single-line), Ctrl+Enter (a panel's „Použít“), Esc (live text). */
  readonly commit?: 'Enter' | 'Mod-Enter' | 'Escape'
  readonly checks: readonly Check[]
  /** Checks that apply but cannot be measured in jsdom, with the reason (`it.todo`). */
  readonly unchecked?: Partial<Record<Check, string>>
}

const all: readonly Check[] = ['P1.start', 'P1.enter', 'P1.toggle', 'P2.menu', 'P3.esc', 'P3.esc-second', 'P4.outside', 'P4.blur', 'P6.undo', 'P7.focus', 'P12.selected', 'P17.sync']
const noValue: readonly Check[] = all.filter(check => !['P4.outside', 'P4.blur', 'P6.undo'].includes(check))
// A field in place: a click into the open field keeps it (P1's second click closes a panel, not the field one types in).
const inPlace: readonly Check[] = all.filter(check => check !== 'P1.toggle')
const block = (type: string, data: object): JSONContent => ({ type, attrs: { data: { version: 1, ...data } } })
const paragraph = (text: string): JSONContent => ({ type: 'paragraph', content: [{ type: 'text', text }] })
const data = (node: JSONContent) => node.attrs?.data as Record<string, any>
const secretId = 'd847b3af-4a87-4e97-aab8-a74a4cf48c89'

const editorDescriptors: readonly Descriptor[] = [
  { id: 'contact.card', label: 'Kontakt (karta)', saves: 'apply', type: 'contactCard', node: block('contactCard', { contacts: [{ name: 'Jan Novák', email: 'jan@example.cz' }] }),
    target: '.md-contact-card', field: '.md-context-popover input[name=name]', value: 'Petr Nový', read: node => data(node).contacts[0].name, commit: 'Enter', checks: all,
    touch: 'Klepnutí na kartu otevře formulář u karty; odkaz e-mailu se klepnutím otevře; formulář se vejde do 375 px.' },
  { id: 'contact.mention', label: 'Kontakt (zmínka)', saves: 'apply', type: 'contactInline', inline: true, node: block('contactInline', { contacts: [{ name: 'Jan Novák' }] }),
    field: '.md-context-popover input[name=name]', value: 'Petr Nový', read: node => data(node).contacts[0].name, commit: 'Enter', checks: all,
    touch: 'Klepnutí na čip otevře úpravu; čip má cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny).' },
  { id: 'contact.unresolved', label: 'Nerozhodnutý kontakt', saves: 'none', type: 'contactInline', inline: true, node: block('contactInline', { contacts: [{ name: 'Jana Nováková', unresolved: true }] }),
    checks: noValue, touch: 'Klepnutí na nerozhodnutý čip nabídne lidi; seznam se vejde do 375 px.' },
  { id: 'math.block', selectsOnClick: true, label: 'Vzorec (blok)', saves: 'live', type: 'mathBlock', node: block('mathBlock', { expression: 'x^2' }),
    target: '.md-math', editing: '.md-math-panel:not([hidden])', field: '.md-math-panel textarea', value: 'y^3', read: node => data(node).expression, commit: 'Escape', checks: all,
    touch: 'Klepnutí na vzorec otevře zdroj pod ním; klávesnice telefonu nepřekryje náhled.' },
  { id: 'math.inline', label: 'Vzorec (v textu)', saves: 'live', type: 'mathInline', inline: true, node: block('mathInline', { expression: 'a' }),
    target: '.md-math', field: '.md-context-popover textarea', value: 'ab', read: node => data(node).expression, commit: 'Escape', checks: all,
    touch: 'Klepnutí na vzorec otevře panel u něj; panel se vejde do 375 px.' },
  { id: 'toc', label: 'Obsah dokumentu', saves: 'draft', type: 'tableOfContents', node: block('tableOfContents', { title: 'Obsah', minLevel: 1, maxLevel: 3 }),
    target: '.md-toc-title', field: '.md-toc-title-input', value: 'Kapitoly', read: node => data(node).title, commit: 'Enter', checks: inPlace,
    touch: 'Klepnutí na název otevře pole; rozsah úrovní jde vybrat dotykem.' },
  { id: 'chart', selectsOnClick: true, label: 'Graf (data)', saves: 'apply', type: 'chart', node: block('chart', { kind: 'bar', title: 'Tržby', grid: [['Měsíc', 'Kč'], ['Leden', '1']] }),
    target: '.md-chart-visual, .md-chart-canvas, .md-chart', field: '.md-chart-data input[aria-label="Popisek 1"]', value: 'Únor', read: node => JSON.stringify(data(node).grid), commit: 'Mod-Enter', checks: all,
    touch: 'Klepnutí na graf otevře data; tabulka dat se na 375 px posouvá uvnitř panelu, ne stránka.' },
  { id: 'chart.title', label: 'Graf (název)', saves: 'draft', type: 'chart', node: block('chart', { kind: 'bar', title: 'Tržby', grid: [['Měsíc', 'Kč'], ['Leden', '1']] }),
    target: '.md-chart-title', field: '.md-chart-title-input, input.md-chart-title', value: 'Náklady', read: node => data(node).title, commit: 'Enter', checks: inPlace,
    touch: 'Klepnutí na název otevře pole na místě.' },
  { id: 'diagram', selectsOnClick: true, label: 'Diagram', saves: 'live', type: 'diagram', node: block('diagram', { source: 'graph TD\n  A --> B', caption: '' }),
    target: '.md-diagram', field: '.md-diagram-source-editor textarea, textarea', value: 'graph TD\n  A --> C', read: node => data(node).source, commit: 'Escape', checks: all,
    touch: 'Klepnutí na diagram otevře zdroj pod ním; náhled zůstane vidět.' },
  { id: 'attachment', label: 'Příloha', saves: 'draft', type: 'attachment', node: block('attachment', { name: 'smlouva.pdf', url: '/files/smlouva.pdf' }),
    target: '.md-attachment-name[data-editable]', field: 'input', value: 'dodatek.pdf', read: node => data(node).name, commit: 'Enter', checks: inPlace,
    touch: 'Klepnutí na název přejmenuje; stažení je samostatné tlačítko, cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny).' },
  { id: 'openapi', selectsOnClick: true, label: 'OpenAPI', saves: 'apply', type: 'openapi', node: { type: 'openapi', attrs: { data: { ...defaultOpenApi, filename: 'orders.yaml' } } },
    target: '.md-openapi-card', checks: noValue.filter(check => check !== 'P1.toggle'), unchecked: { 'P4.outside': 'zdroj YAML je Monaco v dialogu, v jsdom se nenačte', 'P6.undo': 'totéž', 'P1.toggle': 'úprava je modální dialog, druhý klik na kartu nedosáhne' },
    touch: 'Klepnutí na kartu otevře zdroj; dialog na 375 px na celou obrazovku.' },
  { id: 'secret.card', label: 'Heslo (karta)', saves: 'apply', type: 'secretReference', node: { type: 'secretReference', attrs: { secretId } },
    target: '.md-secret-card__header', field: '.md-context-popover input[name=autoHideSeconds]', value: '45', read: node => node.attrs?.autoHideSeconds, commit: 'Mod-Enter', checks: all, touch: 'Klepnutí na hlavičku otevře nastavení; odhalení a kopírování jsou tlačítka, každé cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny).' },
  { id: 'secret.inline', label: 'Heslo (v textu)', saves: 'apply', type: 'secretInlineReference', inline: true, node: { type: 'secretInlineReference', attrs: { secretId } },
    field: '.md-context-popover input[name=autoHideSeconds]', value: '45', read: node => node.attrs?.autoHideSeconds, commit: 'Mod-Enter', checks: all, touch: 'Klepnutí na čip otevře nastavení u čipu.' },
  { id: 'concealed', label: 'Skrytý obsah', saves: 'apply', type: 'concealedContent', node: { type: 'concealedContent', content: [paragraph('Překvapení')] },
    target: '.md-concealed-block__chip', choose: '.md-concealed-settings__tile[data-reveal=button]', read: node => node.attrs?.reveal, commit: 'Mod-Enter', checks: all, touch: 'Klepnutí na štítek otevře nastavení; odhalení stíráním funguje prstem.' },
  { id: 'callout', label: 'Informační box', saves: 'live', type: 'callout', node: { type: 'callout', attrs: { variant: 'note' }, content: [paragraph('Pozor')] },
    target: '.md-callout-label', checks: noValue, touch: 'Klepnutí na štítek nabídne typ; text uvnitř se píše jako jinde.' },
  { id: 'code', label: 'Blok kódu', saves: 'live', type: 'codeBlock', node: { type: 'codeBlock', attrs: { language: 'ts' }, content: [{ type: 'text', text: 'const a = 1' }] },
    checks: ['P1.enter', 'P2.menu', 'P3.esc-second', 'P12.selected'], unchecked: { 'P1.start': 'klik do kódu je kurzor v textu, jsdom kurzor klikem neposouvá' },
    touch: 'Klepnutí do kódu je kurzor; jazyk a kopírování v liště mají cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny); dlouhé řádky se posouvají v bloku.' },
  { id: 'table', label: 'Tabulka', saves: 'live', type: 'table', node: { type: 'table', content: [{ type: 'tableRow', content: [{ type: 'tableHeader', content: [paragraph('A')] }, { type: 'tableHeader', content: [paragraph('B')] }] }, { type: 'tableRow', content: [{ type: 'tableCell', content: [paragraph('1')] }, { type: 'tableCell', content: [paragraph('2')] }] }] },
    checks: ['P2.menu', 'P12.selected'], unchecked: { 'P1.start': 'buňky jsou text; lišty řádků a sloupců potřebují rozložení (jsdom nemá)' },
    touch: 'Tabulka se na 375 px posouvá v sobě; lišty řádků a sloupců mají cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny).' },
  { id: 'image', label: 'Obrázek', saves: 'apply', type: 'image', node: { type: 'image', attrs: { src: 'https://example.com/a.png', alt: 'Obrázek' } },
    target: 'img', field: '.md-image-panel-text textarea', value: 'Graf tržeb', read: node => node.attrs?.alt, commit: 'Mod-Enter', checks: all, touch: 'Klepnutí vybere a ukáže lištu; ořez a efekty v panelu na celou šířku.' },
  { id: 'youtrack', followsOnClick: true, label: 'Úkol YouTrack', saves: 'apply', type: 'youtrackIssue', inline: true, node: { type: 'youtrackIssue', attrs: { id: '2-1', key: 'PX2-1', title: 'Přístup VPN', url: 'https://youtrack.example/issue/PX2-1', state: 'Open' } },
    checks: noValue, touch: 'Klepnutí na čip otevře úkol; úpravu adresy nabídne lišta vybraného čipu; čip má cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny).' },
  { id: 'page-reference', followsOnClick: true, label: 'Odkaz na stránku', saves: 'apply', type: 'pageReference', inline: true, node: { type: 'pageReference', attrs: { id: 'wiki-1', title: 'Provozní příručka', url: '/wiki/1' } },
    checks: noValue, touch: 'Klepnutí na čip otevře stránku; úpravu odkazu nabídne lišta vybraného čipu.' },
]

/** Node types of the full editor that are text structure, not an element with an editing of its own. */
const plainNodes: Record<string, string> = {
  doc: 'kořen', text: 'text', paragraph: 'text', heading: 'text (kotva nadpisu je samostatná akce lišty)', blockquote: 'text', bulletList: 'text', orderedList: 'text',
  listItem: 'text', taskList: 'text', taskItem: 'zaškrtávací políčko je nativní', hardBreak: 'text', horizontalRule: 'čára bez úpravy',
  tableRow: 'část tabulky', tableCell: 'část tabulky', tableHeader: 'část tabulky', emoji: 'znak v textu', details: 'část rozbalovacího bloku',
  detailsSummary: 'část rozbalovacího bloku', detailsContent: 'část rozbalovacího bloku',
  timedList: 'text (razítko je součást textu položky)', timedItem: 'text (razítko je součást textu položky)',
}

const plugins = (): MarkdownPlugin[] => [
  createContactsPlugin({ directory: { search: async () => people, get: async () => null }, known: async () => people }), createMathPlugin(), createTocPlugin(), createChartsPlugin(), createDiagramsPlugin(), createAttachmentsPlugin(), createOpenApiPlugin({}),
  createSecretsPlugin({ services: {
    discover: async () => 'available', requestAccess: async () => {}, copy: async () => {}, subscribeRevocation: () => () => {},
    reveal: async () => ({ values: {}, dispose() {} }),
  }, pickReplacement: async () => { swaps.push(Date.now()); return null } }),
  createConcealedContentPlugin(), createEditorToolsPlugin(), createYouTrackPlugin({ client: { search: async () => [] } }), createPagesPlugin(),
]

// Two people of one name, for the unresolved chip; the swaps of a password the host picker was asked for (P1: Enter is not a swap).
const people = [{ name: 'Jana Nováková', objectId: '6f1c1d3e-8a4b-4c55-9d1e-000000000001', organization: 'Modelsoft' }, { name: 'Jana Nováková', objectId: '6f1c1d3e-8a4b-4c55-9d1e-000000000002', organization: 'Example' }]
const swaps: number[] = []
const controllers: MarkdownEditorController[] = []
afterEach(() => { swaps.length = 0; controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })
// The code view Atlas passes (DocumentEditor.vue), so the code block is measured as the product shows it.
let codeBlock: Awaited<ReturnType<typeof createCodeBlockHighlighting>>
// Lazy chunks the probes open, loaded once here: their first transform on a busy CI worker took longer than a probe waits (800 ms).
beforeAll(async () => { await import('../concealment'); await import('../plugins/youtrack/pickerRuntime'); codeBlock = await createCodeBlockHighlighting() })

interface Mounted { editor: Editor; host: HTMLElement; outside: HTMLButtonElement; element: () => HTMLElement; position: () => number; value: () => unknown }
async function mount(descriptor: Descriptor): Promise<Mounted> {
  const list = plugins()
  const content = descriptor.inline ? [{ type: 'paragraph', content: [{ type: 'text', text: 'Před ' }, descriptor.node, { type: 'text', text: ' po.' }] }] : [paragraph('Před'), descriptor.node, paragraph('Po')]
  const value = { format: 'atlas-rich', version: 1, plugins: list.filter(plugin => plugin.id !== 'editor-tools').map(plugin => ({ id: plugin.id, version: plugin.schemaVersion ?? 1 })), document: { type: 'doc', content } }
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: list, codeBlock, source: { kind: 'local', value } as never }); controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Editor' })
  const outside = document.createElement('button'); outside.textContent = 'Jinde'; document.body.append(outside)
  const editor = editorForController(controller)! as Editor
  const position = () => { let found = -1; editor.state.doc.descendants((node, pos) => { if (found < 0 && node.type.name === descriptor.type) found = pos; return found < 0 }); return found }
  const element = () => {
    const dom = editor.view.nodeDOM(position()) as HTMLElement
    return (descriptor.target && (dom.matches?.(descriptor.target) ? dom : dom.querySelector?.<HTMLElement>(descriptor.target))) || dom
  }
  const read = () => { const node = editor.state.doc.nodeAt(position()); return node && descriptor.read ? descriptor.read(node.toJSON() as JSONContent) : undefined }
  // A runtime loads lazily (KaTeX, Mermaid, Scalar): the element is ready when its target is drawn.
  await vi.waitFor(() => { expect(position()).toBeGreaterThanOrEqual(0); const dom = editor.view.nodeDOM(position()) as HTMLElement; expect(!descriptor.target || dom.matches(descriptor.target) || !!dom.querySelector(descriptor.target)).toBe(true) }, { timeout: 15_000 })
  await settle()
  return { editor, host, outside, element, position, value: read }
}

const settle = async () => { for (let index = 0; index < 5; index++) { await Promise.resolve(); await new Promise(resolve => setTimeout(resolve, 0)) } }
const visible = (element: Element | null) => !!element && element.isConnected && !(element as HTMLElement).closest('[hidden]')
function editing(descriptor: Descriptor, mounted: Mounted) {
  if (descriptor.editing) return visible(document.querySelector(descriptor.editing))
  if ([...document.querySelectorAll('.md-context-popover:not(.md-context-menu), .md-context-menu, dialog[open], .md-suggestion-popup')].some(visible)) return true
  const active = document.activeElement as HTMLElement | null
  return !!active && active !== mounted.editor.view.dom && active.matches('input, textarea, select, [contenteditable="true"]') && (mounted.editor.view.nodeDOM(mounted.position()) as HTMLElement | null)?.contains(active) === true
}
const describeState = (mounted: Mounted) => {
  const active = document.activeElement as HTMLElement | null
  const open = [...document.querySelectorAll('.md-context-popover, dialog[open], .md-suggestion-popup')].filter(visible).map(element => element.className.toString().split(' ').slice(0, 2).join('.')).join(',') || 'nic'
  return `otevřeno: ${open}${swaps.length ? ' + výměna hesla' : ''}; fokus: ${active === document.body ? 'body' : active === mounted.editor.view.dom ? 'editor' : `${active?.tagName.toLowerCase()}.${active?.className}`}; výběr: ${mounted.editor.state.selection.constructor.name}`
}

/** The key codes a browser sends with Enter and Esc: ProseMirror prevents both by their code (captureKeyDown), as it does in Chrome. */
const keyCodes: Readonly<Record<string, number>> = { Enter: 13, Escape: 27 }
function key(target: Element, key: string, init: KeyboardEventInit = {}) {
  const down = new KeyboardEvent('keydown', { key, keyCode: keyCodes[key] ?? 0, bubbles: true, cancelable: true, ...init })
  target.dispatchEvent(down)
  // A browser submits a form on a plain Enter in its text field (implicit submission); jsdom does not.
  if (key === 'Enter' && !down.defaultPrevented && !init.ctrlKey && !init.metaKey && !init.shiftKey && target instanceof HTMLInputElement && target.form) target.form.requestSubmit()
  // A browser cancels an open modal dialog on Esc (its cancel event, then close); jsdom does not.
  const dialog = target.closest('dialog[open]')
  if (key === 'Escape' && !down.defaultPrevented && dialog instanceof HTMLDialogElement && dialog.dispatchEvent(new Event('cancel', { cancelable: true }))) dialog.close()
  target.dispatchEvent(new KeyboardEvent('keyup', { key, bubbles: true, cancelable: true, ...init }))
}
const keyboard = (mounted: Mounted) => document.activeElement && document.activeElement !== document.body ? document.activeElement : mounted.editor.view.dom
function click(target: Element) {
  const init = { bubbles: true, cancelable: true, button: 0 }
  target.dispatchEvent(new PointerEvent('pointerdown', init)); target.dispatchEvent(new MouseEvent('mousedown', init))
  if (target instanceof HTMLElement && target.matches('button, input, textarea, select, a[href], [tabindex]')) target.focus()
  target.dispatchEvent(new PointerEvent('pointerup', init)); target.dispatchEvent(new MouseEvent('mouseup', init)); target.dispatchEvent(new MouseEvent('click', init))
}
function select(mounted: Mounted) {
  mounted.editor.view.focus(); mounted.editor.commands.setNodeSelection(mounted.position())
}
const nodeSelected = (mounted: Mounted) => mounted.editor.state.selection instanceof NodeSelection && mounted.editor.state.selection.from === mounted.position()
async function until(condition: () => boolean, timeout = 800) {
  try { await vi.waitFor(() => { if (!condition()) throw new Error('not yet') }, { timeout, interval: 20 }); return true } catch { return false }
}
/**
 * Starts the editing as a person does on the page: a click on the element's content; on a block that only selects on a click, the click
 * and then Enter on the selected block; on a link chip, whose click follows the link (J3.5), Enter on the selected chip.
 */
async function start(descriptor: Descriptor, mounted: Mounted) {
  beginEdit(descriptor, mounted)
  return until(() => editing(descriptor, mounted))
}
function beginEdit(descriptor: Descriptor, mounted: Mounted) {
  if (descriptor.followsOnClick) { select(mounted); key(mounted.editor.view.dom, 'Enter'); return }
  click(mounted.element())
  if (descriptor.selectsOnClick && nodeSelected(mounted)) key(mounted.editor.view.dom, 'Enter')
}
/** The second gesture of P1: a second click closes a panel; on a selecting block or a link chip the second Enter does. */
function beginEditAgain(descriptor: Descriptor, mounted: Mounted) {
  if (descriptor.selectsOnClick || descriptor.followsOnClick) key(mounted.editor.view.dom, 'Enter'); else click(mounted.element())
}
function type(descriptor: Descriptor) {
  if (descriptor.choose) { const choice = [...document.querySelectorAll<HTMLElement>(descriptor.choose)].find(visible); if (choice) { choice.focus(); click(choice) } return !!choice }
  const field = [...document.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>(descriptor.field!)].find(visible)
  if (!field) return false
  field.focus(); field.value = descriptor.value!; field.dispatchEvent(new Event('input', { bubbles: true })); field.dispatchEvent(new Event('change', { bubbles: true }))
  return true
}
async function edit(descriptor: Descriptor, mounted: Mounted): Promise<true | string> {
  if (!await start(descriptor, mounted)) return `klik úpravu neotevřel (${describeState(mounted)})`
  if (!type(descriptor)) return `pole ${descriptor.field ?? descriptor.choose} se neukázalo (${describeState(mounted)})`
  await settle()
  return true
}
const pressCommit = (descriptor: Descriptor, mounted: Mounted) => key(keyboard(mounted), descriptor.commit === 'Mod-Enter' ? 'Enter' : descriptor.commit!, descriptor.commit === 'Mod-Enter' ? { ctrlKey: true } : {})

type Probe = (descriptor: Descriptor, mounted: Mounted) => Promise<true | string>
const probes: Record<Check, Probe> = {
  async 'P1.start'(descriptor, mounted) {
    if (descriptor.selectsOnClick) {
      // The surface is read: a click only selects the block and opens nothing; Enter on it starts the editing.
      click(mounted.element()); await settle()
      if (editing(descriptor, mounted)) return 'klik na plochu otevřel úpravu, má blok jen vybrat'
      if (!nodeSelected(mounted)) return `klik blok nevybral (${describeState(mounted)})`
      key(mounted.editor.view.dom, 'Enter')
      return await until(() => editing(descriptor, mounted)) || `Enter na vybraném bloku úpravu neotevřel (${describeState(mounted)})`
    }
    if (descriptor.followsOnClick) {
      // J3.5: a click follows the link as any link (the host's rule) and opens nothing; Enter on the selected chip starts the editing.
      const at = mounted.element(); const follow = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 })
      ;(at.matches('a') ? at : at.querySelector('a') ?? at).dispatchEvent(follow); await settle()
      if (follow.defaultPrevented) return 'klik na čip zrušil přechod na odkaz'
      if (editing(descriptor, mounted)) return 'klik na čip otevřel úpravu, má následovat odkaz'
      return await start(descriptor, mounted) || `Enter na vybraném čipu úpravu neotevřel (${describeState(mounted)})`
    }
    return await start(descriptor, mounted) || `klik jen ${nodeSelected(mounted) ? 'vybral' : 'nic neudělal'} (${describeState(mounted)})`
  },
  async 'P17.sync'(descriptor, mounted) {
    beginEdit(descriptor, mounted)
    // The same task: microtasks (a resolved promise, a cached answer) may run, a timer or the network may not.
    for (let index = 0; index < 20; index++) await Promise.resolve()
    if (editing(descriptor, mounted)) return true
    return await until(() => editing(descriptor, mounted)) ? 'úprava se ukázala až po čekání' : 'úprava se neotevřela'
  },
  async 'P1.enter'(descriptor, mounted) {
    const missing: string[] = []
    for (const name of ['Enter', 'F2']) {
      select(mounted); await settle()
      key(mounted.editor.view.dom, name)
      if (!await until(() => editing(descriptor, mounted))) missing.push(`${name}: ${describeState(mounted)}`)
      key(keyboard(mounted), 'Escape'); await settle()
    }
    return missing.length ? `neotevře úpravu – ${missing.join(' | ')}` : true
  },
  async 'P1.toggle'(descriptor, mounted) {
    if (!await start(descriptor, mounted)) return 'klik úpravu neotevřel'
    beginEditAgain(descriptor, mounted)
    return await until(() => !editing(descriptor, mounted)) || `druhý klik úpravu nezavřel (${describeState(mounted)})`
  },
  async 'P2.menu'(_descriptor, mounted) {
    const menu = () => [...document.querySelectorAll('.md-suggestion-popup, [role=menu], [role=listbox]')].find(visible)
    // Every entry point of the one menu (P2): Ctrl+Space, Shift+F10 and the right button on the selected element (its node, not a picture
    // or a link inside it: those keep the browser's menu, as on the board).
    const gestures: [string, () => void][] = [
      ['Ctrl+mezerník', () => key(mounted.editor.view.dom, ' ', { code: 'Space', ctrlKey: true })],
      ['Shift+F10', () => key(mounted.editor.view.dom, 'F10', { shiftKey: true })],
      // Where a person points: the element's target or chip button (a node view must not swallow it), the node itself for a picture.
      ['pravé tlačítko', () => { const at = mounted.element(); (at.matches('img') ? mounted.editor.view.nodeDOM(mounted.position()) as HTMLElement : at.querySelector<HTMLElement>('.md-chip') ?? at).dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, button: 2 })) }],
    ]
    for (const [name, open] of gestures) {
      select(mounted); await settle()
      open()
      if (!await until(() => !!menu()?.querySelector('[role=option], [role=menuitem], button'))) return menu() ? `${name}: nabídka akcí je pro vybraný prvek prázdná („${menu()!.textContent?.trim().slice(0, 60)}“)` : `${name} nabídku neotevřel (${describeState(mounted)})`
      if (!menu()!.querySelector('[aria-selected=true], .is-selected, .selected')) return `${name}: první řádek není předvybraný`
      // The element's own actions, at least its delete (P2: „úpravy prvku, převody … smazat“) in the rules' words (P12: Odebrat, Smazat).
      if (![...menu()!.querySelectorAll('[role=option], [role=menuitem], button')].some(row => /Odebrat|Smazat/.test(row.textContent ?? ''))) return `${name}: nabídka nenabízí smazání prvku („${menu()!.textContent?.trim().slice(0, 60)}“)`
      key(keyboard(mounted), 'Escape')
      if (!await until(() => !menu())) return `${name}: Esc nabídku nezavřel`
    }
    return true
  },
  async 'P3.esc'(descriptor, mounted) {
    const before = mounted.value()
    const result = (descriptor.field || descriptor.choose) ? await edit(descriptor, mounted) : await start(descriptor, mounted) || `klik úpravu neotevřel (${describeState(mounted)})`
    if (result !== true) return result
    key(keyboard(mounted), 'Escape')
    if (!await until(() => !editing(descriptor, mounted))) return `Esc úpravu nezavřel (${describeState(mounted)})`
    if (!descriptor.field && !descriptor.choose) return true
    const after = mounted.value()
    if (descriptor.saves === 'live') return after !== before ? true : 'Esc zahodil průběžně uložený text'
    return after === before ? true : `Esc koncept nezahodil (hodnota ${JSON.stringify(after)})`
  },
  async 'P3.esc-second'(descriptor, mounted) {
    if (descriptor.checks.includes('P1.start') && !descriptor.unchecked?.['P1.start']) {
      if (!await start(descriptor, mounted)) { select(mounted); await settle() }
      else { key(keyboard(mounted), 'Escape'); await settle() }
    } else { select(mounted); await settle() }
    if (editing(descriptor, mounted)) return `první Esc úpravu nezavřel (${describeState(mounted)})`
    key(keyboard(mounted), 'Escape'); await settle()
    if (document.activeElement === document.body) return 'klávesnice skončila na <body>'
    return nodeSelected(mounted) ? 'druhý Esc výběr nezrušil' : true
  },
  async 'P4.outside'(descriptor, mounted) {
    const before = mounted.value()
    const result = await edit(descriptor, mounted); if (result !== true) return result
    click(mounted.outside); await settle()
    if (!await until(() => mounted.value() !== before)) return `klik mimo hodnotu neuložil (${describeState(mounted)})`
    return await until(() => !editing(descriptor, mounted)) || `klik mimo úpravu nezavřel (${describeState(mounted)})`
  },
  async 'P4.blur'(descriptor, mounted) {
    const before = mounted.value()
    const result = await edit(descriptor, mounted); if (result !== true) return result
    mounted.outside.focus(); await settle()
    return await until(() => mounted.value() !== before) || `odchod fokusu hodnotu neuložil (${describeState(mounted)})`
  },
  async 'P6.undo'(descriptor, mounted) {
    const before = mounted.value()
    const result = await edit(descriptor, mounted); if (result !== true) return result
    pressCommit(descriptor, mounted)
    if (!await until(() => mounted.value() !== before && !editing(descriptor, mounted))) return `${descriptor.commit} úpravu neuložil a nezavřel (${describeState(mounted)})`
    key(keyboard(mounted), 'z', { ctrlKey: true })
    return await until(() => mounted.value() === before) || `Ctrl+Z (fokus ${describeState(mounted)}) změnu nevrátil`
  },
  async 'P7.focus'(descriptor, mounted) {
    if (descriptor.field || descriptor.choose) {
      const result = await edit(descriptor, mounted); if (result !== true) return result
      pressCommit(descriptor, mounted)
    } else {
      if (!await start(descriptor, mounted)) return `klik úpravu neotevřel (${describeState(mounted)})`
      key(keyboard(mounted), 'Escape')
    }
    await until(() => !editing(descriptor, mounted)); await settle()
    const active = document.activeElement
    if (active === document.body || !active) return 'klávesnice skončila na <body>'
    if (!mounted.editor.view.dom.contains(active) && !mounted.element().contains(active)) return `klávesnice skončila jinde (${describeState(mounted)})`
    return nodeSelected(mounted) || `prvek po úpravě není vybraný (${describeState(mounted)})`
  },
  async 'P12.selected'(_descriptor, mounted) {
    select(mounted)
    const dom = () => mounted.editor.view.nodeDOM(mounted.position()) as HTMLElement
    const marked = () => [dom(), ...dom().querySelectorAll('*')].some(element => element.classList.contains('md-component-selected') || element.classList.contains('ProseMirror-selectednode'))
    return await until(marked) || `vybraný prvek nemá sdílenou třídu (třídy: ${dom().className})`
  },
}

describe('interaction contract: page editor', { timeout: 60_000 }, () => {
  const listed = new Map((deviations as Deviation[]).map(row => [`${row.element} ${row.check}`, row]))
  it('has a descriptor (or a reason in plainNodes) for every node type of the full editor', async () => {
    const mounted = await mount(editorDescriptors.find(descriptor => descriptor.id === 'callout')!)
    const covered = new Set(editorDescriptors.map(descriptor => descriptor.type))
    const missing = Object.keys(mounted.editor.schema.nodes).filter(name => !covered.has(name) && !(name in plainNodes))
    expect(missing, 'add a descriptor to editorDescriptors (or a reason to plainNodes)').toEqual([])
  })
  it('lists only deviations of elements and checks the suite runs, each once, with a reason', () => {
    const described: readonly { id: string; touch: string; checks: readonly string[] }[] = [...editorDescriptors, ...pickerDescriptors]
    const problems = (deviations as Deviation[]).flatMap(row => {
      const descriptor = described.find(candidate => candidate.id === row.element)
      if (!descriptor) return [`${row.element}: no descriptor`]
      if (!descriptor.checks.includes(row.check)) return [`${row.element} ${row.check}: the descriptor does not run it`]
      return row.reason.trim() ? [] : [`${row.element} ${row.check}: no reason`]
    })
    const keys = (deviations as Deviation[]).map(row => `${row.element} ${row.check}`)
    expect([...problems, ...keys.filter((entry, index) => keys.indexOf(entry) !== index)]).toEqual([])
    expect(described.filter(descriptor => !descriptor.touch.trim()).map(descriptor => descriptor.id)).toEqual([])
    // D5: a touch note never approves a target under 44 px.
    expect(described.filter(descriptor => [...descriptor.touch.matchAll(/(\d+) ?px/g)].some(([, size]) => Number(size) < 44)).map(descriptor => descriptor.id)).toEqual([])
  })
  for (const descriptor of editorDescriptors) {
    describe(descriptor.id, () => {
      for (const check of descriptor.checks) {
        const deviation = listed.get(`${descriptor.id} ${check}`)
        it(`${check}${deviation ? ' (expected deviation)' : ''}`, async () => {
          const mounted = await mount(descriptor)
          let result: true | string
          try { result = await probes[check](descriptor, mounted) } catch (error) { result = `threw: ${error instanceof Error ? error.message : String(error)}` }
          if (deviation && result === true) expect.fail(`${descriptor.id} ${check} now follows the rule: delete its deviation („${deviation.reason}“)`)
          if (!deviation && result !== true) expect.fail(`${descriptor.id} ${check}: ${result}`)
        })
      }
      for (const [check, reason] of Object.entries(descriptor.unchecked ?? {})) it.todo(`${check}: not measured in jsdom (${reason})`)
    })
  }
})

/*
 * P8 in the editor's pickers: the in-text lists (@ people, the slash menu) and the panel search fields („Vložit úkol“, „Přidat
 * z adresáře“, createSuggestionField), with the probes of the app's pickers (src/Atlas.Web/src/contracts/appInteraction.contract.test.ts).
 * An in-text list has no field of its own: Esc closes it and keeps the typed text (P3 for live text), so P3.search-esc is the panels'.
 */
type PickerCheck = 'P8.first' | 'P8.enter' | 'P8.arrows' | 'P8.tab' | 'P3.search-esc'
interface PickerSession { type(query: string): Promise<void>; key(name: string): Promise<void>; rows(): HTMLElement[]; pre(): HTMLElement | null; taken(): string[]; value(): string; closed(): boolean }
interface PickerDescriptor { readonly id: string; readonly label: string; readonly touch: string; readonly query: string; readonly first: string; readonly second: string; readonly checks: readonly PickerCheck[]; open(): Promise<PickerSession> }

async function editorPicker(node: JSONContent, taken: (editor: Editor) => string[]) {
  // The @ list as Atlas turns it on (contact suggestions); the slash menu as the editor view configures it (DocumentEditor.vue).
  const list = [createContactsPlugin({ directory: { search: async () => people, get: async () => null }, suggestions: true }), ...plugins().slice(1)]
  const value = { format: 'atlas-rich', version: 1, plugins: list.filter(plugin => plugin.id !== 'editor-tools').map(plugin => ({ id: plugin.id, version: plugin.schemaVersion ?? 1 })), document: { type: 'doc', content: [node] } }
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: list, codeBlock, source: { kind: 'local', value } as never }); controllers.push(controller)
  configureMarkdownEditorView(controller, { compact: false })
  const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Editor' })
  const editor = editorForController(controller)! as Editor
  editor.view.focus(); await settle()
  return { editor, taken: () => taken(editor) }
}
const popupRows = () => [...document.querySelectorAll<HTMLElement>('.md-suggestion-popup [role=option]')].filter(visible)
/** An in-text list: `char` and the query typed into the page, the keys pressed in the editor. */
async function inTextPicker(char: string, taken: (editor: Editor) => string[]): Promise<PickerSession> {
  const { editor, taken: took } = await editorPicker({ type: 'paragraph' }, taken)
  const root = editor.view.dom
  return {
    async type(query) { editor.commands.insertContent(char + query); await until(() => popupRows().length > 1, 3000) },
    async key(name) { key(root, name); await settle() },
    rows: popupRows,
    pre: () => { const id = root.getAttribute('aria-activedescendant'); return id ? document.getElementById(id) : null },
    taken: took, value: () => editor.getText(), closed: () => !popupRows().length,
  }
}
/** A panel's search field: the keys pressed in the field. */
function fieldPicker(panel: () => HTMLElement | null, taken: () => string[]): PickerSession {
  const input = () => panel()?.querySelector<HTMLInputElement>('input') ?? null
  const rows = () => [...panel()?.querySelectorAll<HTMLElement>('[role=option]') ?? []].filter(visible)
  return {
    async type(query) { const field = input()!; field.value = query; field.dispatchEvent(new Event('input', { bubbles: true })); await until(() => rows().length > 1, 3000) },
    async key(name) { const field = input(); if (field) key(field, name); await settle() },
    rows,
    pre: () => { const id = input()?.getAttribute('aria-activedescendant'); return id ? document.getElementById(id) : null },
    taken, value: () => input()?.value ?? '', closed: () => !panel()?.isConnected,
  }
}

const issues = [{ id: '2-1', key: 'PX2-1', title: 'Přístup VPN', url: 'https://youtrack.example/issue/PX2-1' }, { id: '2-2', key: 'PX2-2', title: 'Přístup Wi-Fi', url: 'https://youtrack.example/issue/PX2-2' }]
/** The people of the nodes of `type` in the document, by name and organisation (the two Jana Nováková differ by it). */
const contactsIn = (type: string) => (editor: Editor) => {
  const names: string[] = []
  editor.state.doc.descendants(node => { if (node.type.name === type) names.push(...data(node.toJSON() as JSONContent).contacts.map((contact: Record<string, string>) => `${contact.name} ${contact.organization}`)) })
  return names
}
const pickerDescriptors: readonly PickerDescriptor[] = [
  { id: 'picker.people', label: '@ lidé v textu', query: 'jana', first: 'Modelsoft', second: 'Example', checks: ['P8.first', 'P8.enter', 'P8.arrows', 'P8.tab'],
    touch: 'Seznam u kurzoru; klepnutí na řádek osobu vloží, řádek je cíl dotyku aspoň 44 × 44 px.',
    open: () => inTextPicker('@', contactsIn('contactInline')) },
  { id: 'picker.slash', label: 'Nabídka lomítka', query: 'nadpis', first: 'Nadpis 1', second: 'Nadpis 2', checks: ['P8.first', 'P8.enter', 'P8.arrows', 'P8.tab'],
    touch: 'Seznam u kurzoru; klepnutí na řádek vloží blok, řádek je cíl dotyku aspoň 44 × 44 px.',
    open: () => inTextPicker('/', editor => editor.state.doc.firstChild?.type.name === 'heading' ? [`Nadpis ${editor.state.doc.firstChild.attrs.level}`] : []) },
  { id: 'picker.youtrack', label: 'Vložit úkol (YouTrack)', query: 'přístup', first: 'PX2-1', second: 'PX2-2', checks: ['P8.first', 'P8.enter', 'P8.arrows', 'P8.tab', 'P3.search-esc'],
    touch: 'Pole a řádky v panelu u kurzoru, na telefonu dialog; řádek je cíl dotyku aspoň 44 × 44 px.',
    async open() {
      const taken: string[] = [], panel = () => document.querySelector<HTMLElement>('[data-testid=youtrack-picker]')
      void pickYouTrackIssue({ client: { search: async () => issues }, signal: new AbortController().signal, ownerDocument: document }).then(issue => { if (issue) taken.push(issue.key) })
      // The panel's runtime loads lazily.
      await until(() => !!panel(), 3000)
      return fieldPicker(panel, () => taken)
    } },
  { id: 'picker.directory', label: 'Přidat z adresáře (kontakt)', query: 'jana', first: 'Modelsoft', second: 'Example', checks: ['P8.first', 'P8.enter', 'P8.arrows', 'P8.tab', 'P3.search-esc'],
    touch: 'Pole a řádky v panelu u karty; řádek je cíl dotyku aspoň 44 × 44 px.',
    async open() {
      const { editor, taken } = await editorPicker(block('contactCard', { contacts: [{ name: 'Jan Novák' }] }), contactsIn('contactCard'))
      const add = () => document.querySelector('[aria-label="Přidat z adresáře"]'), panel = () => document.querySelector<HTMLElement>('.md-contact-directory')
      editor.commands.setNodeSelection(0)
      await until(() => !!add(), 3000)
      click(add()!)
      await until(() => !!panel(), 3000)
      return fieldPicker(panel, taken)
    } },
]
const words = (element: Element | null | undefined) => element?.textContent?.replace(/\s+/g, ' ').trim() ?? ''
const sameRow = (a: Element | null | undefined, b: Element | null | undefined) => !!a && !!b && a === b
const pickerProbes: Record<PickerCheck, (d: PickerDescriptor) => Promise<true | string>> = {
  async 'P8.first'(d) {
    const s = await d.open(); await s.type(d.query)
    const [first] = s.rows()
    if (!first || !words(first).includes(d.first)) return `první výsledek je „${words(first)}“, ne „${d.first}“`
    return sameRow(s.pre(), first) || `předvybraný je „${words(s.pre())}“, ne první výsledek`
  },
  async 'P8.enter'(d) {
    const s = await d.open(); await s.type(d.query); await s.key('Enter')
    return await until(() => s.taken().join(' ').includes(d.first)) || `psaní + Enter vzalo ${JSON.stringify(s.taken())}, ne první výsledek`
  },
  async 'P8.arrows'(d) {
    const s = await d.open(); await s.type(d.query)
    const [first, second] = s.rows()
    if (!second || !words(second).includes(d.second)) return `druhý výsledek je „${words(second)}“, ne „${d.second}“`
    await s.key('ArrowDown')
    if (!sameRow(s.pre(), second)) return `ArrowDown nechal klávesnici na „${words(s.pre())}“`
    await s.key('ArrowUp')
    return sameRow(s.pre(), first) || `ArrowUp nechal klávesnici na „${words(s.pre())}“`
  },
  async 'P8.tab'(d) {
    const s = await d.open(); await s.type(d.query); await s.key('Tab')
    return await until(() => s.taken().join(' ').includes(d.first)) || `Tab vzal ${JSON.stringify(s.taken())}, ne předvybraný návrh`
  },
  async 'P3.search-esc'(d) {
    const s = await d.open(); await s.type(d.query); await s.key('Escape')
    if (s.closed()) return 'první Esc panel hned zavřel'
    if (s.value()) return `první Esc nechal napsané „${s.value()}“`
    await s.key('Escape')
    return s.closed() || 'druhý Esc nic nezavřel'
  },
}

describe('interaction contract: editor pickers (P8)', { timeout: 60_000 }, () => {
  const listed = new Map((deviations as Deviation[]).map(row => [`${row.element} ${row.check}`, row]))
  for (const descriptor of pickerDescriptors) {
    describe(descriptor.id, () => {
      for (const check of descriptor.checks) {
        const deviation = listed.get(`${descriptor.id} ${check}`)
        it(`${check}${deviation ? ' (expected deviation)' : ''}`, async () => {
          let result: true | string
          try { result = await pickerProbes[check](descriptor) } catch (error) { result = `threw: ${error instanceof Error ? error.message : String(error)}` }
          if (deviation && result === true) expect.fail(`${descriptor.id} ${check} now follows the rule: delete its deviation („${deviation.reason}“)`)
          if (!deviation && result !== true) expect.fail(`${descriptor.id} ${check}: ${result}`)
        })
      }
    })
  }
})

// P17 „Psaní nikdy nezaostává“: the synchronous work of one keystroke (transaction, every plugin's state, decorations, node views) in a
// long document with every plugin and the name recognition on (its underlines drawn before the first key), against the same in a
// short one. The budget is per keystroke at the first response (100 ms) with room for a loaded CI host (measured 2026-10-30: 1.8 ms
// in 2 000 blocks, 0.4 ms in 20); the ratio catches work that grows with the document (a scan of every block per key).
// The work the typing leaves for later runs in tasks of its own: every timer it set (the names' scan of all 2 000 blocks: known names
// are in every tenth block, under the underline limit) shorter than a long task (50 ms), so the next key is not held (measured: 10 ms).
// The projection a collaborative save pushes every second or two serializes the whole document (measured: 52 ms, of it 44 ms the
// Markdown): its median of five stays within the first response (100 ms) against a regression; the rule's own target, a task shorter
// than a long task, is the known gap below (the todo) until documentCodec projects incrementally.
describe('interaction contract: typing in a long document (P17.keystroke)', { timeout: 120_000 }, () => {
  const budgetMs = 25, growth = 5, longTaskMs = 50, projectionMs = 100
  async function type(blocks: number) {
    const list = plugins()
    const content = Array.from({ length: blocks }, (_, index) => index % 50 === 0 ? { type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: `Kapitola ${index / 50 + 1}` }] }
      : paragraph(`Odstavec ${index}: ${index % 10 === 1 ? 'Jana Nováková' : 'Jana'} posílá podklady k Provozní příručce a čeká na odpověď do pátku.`))
    const value = { format: 'atlas-rich', version: 1, plugins: list.filter(plugin => plugin.id !== 'editor-tools').map(plugin => ({ id: plugin.id, version: plugin.schemaVersion ?? 1 })), document: { type: 'doc', content } }
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: list, source: { kind: 'local', value } as never }); controllers.push(controller)
    const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Editor' })
    const editor = editorForController(controller)! as Editor, view = editor.view
    await settle()
    // The recognition loads after the first render: typing is measured with it on. The first projection is the one an open saves.
    await vi.waitFor(() => expect(host.querySelector('.md-recognition-candidate')).not.toBeNull(), { timeout: 10_000 })
    controller.getProjection()
    let middle = 0; editor.state.doc.forEach((node, offset, index) => { if (index === Math.floor(blocks / 2)) middle = offset + node.nodeSize - 1 })
    view.focus(); editor.commands.setTextSelection(middle)
    const times: number[] = [], later: number[] = []
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    try {
      for (const character of ' a Petr Svoboda dodá') {
        const started = performance.now()
        const { from, to } = view.state.selection
        if (!view.someProp('handleTextInput', handle => handle(view, from, to, character, () => view.state.tr.insertText(character, from, to)))) view.dispatch(view.state.tr.insertText(character, from, to))
        times.push(performance.now() - started)
        await Promise.resolve()
      }
      for (let task = 0; task < 50 && vi.getTimerCount(); task++) { const started = performance.now(); vi.advanceTimersToNextTimer(); later.push(performance.now() - started) }
    } finally { vi.useRealTimers() }
    // A median, not one sample: one GC pause in 2 000 blocks on a loaded CI host must not fail a release.
    const projections = Array.from({ length: 5 }, () => { const started = performance.now(); controller.getProjection(); return performance.now() - started })
    const median = (samples: number[]) => samples.sort((a, b) => a - b)[Math.floor(samples.length / 2)]!
    return { median: median(times), later: Math.max(0, ...later), projection: median(projections) }
  }
  it('keeps a keystroke within budget in 2 000 blocks, and its cost does not grow with the document', async () => {
    const short = (await type(20)).median, { median: long, later, projection } = await type(2000)
    expect(long, `median ${long.toFixed(1)} ms in 2 000 blocks (20 blocks: ${short.toFixed(1)} ms)`).toBeLessThan(budgetMs)
    expect(long, `median ${long.toFixed(1)} ms in 2 000 blocks vs ${short.toFixed(1)} ms in 20`).toBeLessThan(Math.max(short, 1) * growth)
    expect(later, `the longest timer the typing left for later took ${later.toFixed(1)} ms in 2 000 blocks`).toBeLessThan(longTaskMs)
    expect(projection, `the projection after the typing took ${projection.toFixed(1)} ms (median of 5) in 2 000 blocks`).toBeLessThan(projectionMs)
  })
  // Known gap (P17): the save's projection is a long task today (52 ms); the per-block Markdown cache in engine/documentCodec.ts closes
  // it, then projectionMs above drops to longTaskMs and this todo goes.
  it.todo(`runs the projection a collaborative save pushes in 2 000 blocks within a long task (${longTaskMs} ms)`)
})
