import { defineMarkdownBlockModule, defineMarkdownEntityKind, type MarkdownPlugin } from '../../core'
import { createMathInput } from './inputRules'
import { mathStrings } from './strings'

export interface MarkdownMathData { version: 1; expression: string }
export function normalizeMath(value: unknown): MarkdownMathData {
  const data = value as Partial<MarkdownMathData> | null
  if (!data || data.version !== 1 || typeof data.expression !== 'string' || !data.expression.trim() || data.expression.length > 10_000) {
    throw new TypeError(mathStrings.expressionRequired)
  }
  return { version: 1, expression: data.expression }
}
const defaults: MarkdownMathData = { version: 1, expression: 'E = mc^2' }
/** Core marks Markdown `$…$`/`$$…$$`/```math (the editor dialect) with the untouched TeX source. */
const importedMath = (display: 'block' | 'inline') => [{
  tag: `${display === 'block' ? 'div' : 'span'}[data-md-math="${display}"]`,
  data: (element: HTMLElement) => ({ version: 1, expression: element.getAttribute('data-md-expression') ?? '' }),
}]
/** Both forms come back trimmed, and markdown-it normalizes line endings and NUL. */
const representable = (expression: string) => expression === expression.trim() && !/[\r\0]/.test(expression)
/**
 * `$$` lines as written by hand; a ```math fence (longer than any backtick run) for source those lines
 * cannot carry: a blank or indented line, or `$$` inside.
 */
function blockMarkdown({ expression }: MarkdownMathData): string | undefined {
  if (!representable(expression)) return undefined
  if (expression.split('\n').every(line => line && line === line.trim() && !line.includes('$$'))) return `$$\n${expression}\n$$`
  const ticks = '`'.repeat(Math.max(3, ...(expression.match(/`+/g) ?? []).map(run => run.length + 1)))
  return `${ticks}math\n${expression}\n${ticks}`
}
/** `$…$` for one line without an unescaped `$` or a trailing lone backslash, which would move the closing `$`. */
function inlineMarkdown({ expression }: MarkdownMathData): string | undefined {
  if (!representable(expression) || expression.includes('\n')) return undefined
  for (let index = 0; index < expression.length; index++) {
    if (expression[index] === '$') return undefined
    // The parser skips the character after a backslash, so a final lone backslash would escape the closing `$`.
    if (expression[index] === '\\' && ++index === expression.length) return undefined
  }
  return `$${expression}$`
}
const block = defineMarkdownBlockModule<MarkdownMathData>({
  // A line of symbols is about 15 px; a fraction, sum, integral, root or matrix stands about twice as tall (23–36 px drawn).
  id: 'math', name: 'mathBlock', openOnInsert: true, stubHeight: data => /\\(?:d?frac|sum|prod|int|oint|lim|sqrt|binom|begin)\b/.test(data.expression) ? 28 : 15, dataValidator: 'math', get label() { return mathStrings.blockTitle }, get description() { return mathStrings.blockDescription }, icon: 'sigma', keywords: ['vzorec', 'matematika', 'rovnice', 'latex'], defaults, normalize: normalizeMath,
  text: data => `\\[${data.expression}\\]`, markdown: blockMarkdown, parseHTML: importedMath('block'),
  load: () => import('./runtime').then(module => module.mathBlockRuntime),
})
const inline = defineMarkdownBlockModule<MarkdownMathData>({
  id: 'math-inline', name: 'mathInline', dataValidator: 'math', get label() { return mathStrings.inlineTitle }, get description() { return mathStrings.inlineDescription }, icon: 'square-code', keywords: ['vzorec', 'matematika', 'rovnice', 'latex'], defaults, normalize: normalizeMath, inline: true,
  text: data => `\\(${data.expression}\\)`, markdown: inlineMarkdown, parseHTML: importedMath('inline'),
  load: () => import('./runtime').then(module => module.mathInlineRuntime),
})
/** One plugin registers both representations and persists one schema version. */
export function createMathPlugin(): MarkdownPlugin {
  const blockPlugin = block.createPlugin(); const inlinePlugin = inline.createPlugin()
  return { ...blockPlugin, entities: [defineMarkdownEntityKind({ kind: 'math', inline: 'mathInline' })], create(context) {
    const a = blockPlugin.create(context); const b = inlinePlugin.create(context)
    return { extensions: [...a.extensions ?? [], ...b.extensions ?? [], createMathInput(defaults.expression)], commands: [...a.commands ?? [], ...b.commands ?? []],
      actions: { slash: [...a.actions?.slash ?? [], ...b.actions?.slash ?? []], toolbar: [...a.actions?.toolbar ?? [], ...b.actions?.toolbar ?? []] },
      markdownLossy: document => !!a.markdownLossy?.(document) || !!b.markdownLossy?.(document),
      onRender(context) { const cleanA = a.onRender?.(context); const cleanB = b.onRender?.(context); return () => { cleanB?.(); cleanA?.() } },
    }
  } }
}
export const insertMathBlock = block.insert
export const insertMathInline = inline.insert
export const createMathBlockNode = block.node
export const createMathInlineNode = inline.node
