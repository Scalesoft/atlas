import source from './MarkdownDetailWorkspace.vue?raw'
import { compileStyle, parse } from 'vue/compiler-sfc'
import { expect, it } from 'vitest'
it('reduced detail motion targets tabs and never hides the editor root', () => {
  const filename = new URL('./MarkdownDetailWorkspace.vue', import.meta.url)
  const { descriptor } = parse(source)
  const result = compileStyle({ source: descriptor.styles[0]!.content, filename: filename.pathname, id: 'data-v-detail', scoped: true })
  expect(result.errors).toEqual([])
  const explicitReduced = result.code.match(/\[data-md-motion=reduced\][^{]*\{[^}]*\}/g)!
  expect(explicitReduced).toHaveLength(2)
  for (const rule of explicitReduced) expect(rule.split('{')[0]).toContain('.md-detail-tabs')
})

