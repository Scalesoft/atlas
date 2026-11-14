import { describe, expect, it } from 'vitest'
import { estimateDiagramHeight } from './estimate'

/** J8.1: a drawing's stub has about its size before it is drawn: the heights measured on real diagrams (px, with the source line and padding). */
describe('first-visit height of a diagram', () => {
  const data = (source: string, caption = '') => ({ version: 1 as const, source, caption })
  it('grows with the nodes of a vertical flowchart', () => {
    const chain = 'flowchart TD\n  A[a] --> B[b]\n  B --> C[c]\n  C --> D[d]\n  D --> E[e]'
    expect(estimateDiagramHeight(data(chain))).toBeGreaterThan(estimateDiagramHeight(data('flowchart TD\n  A --> B')))
    expect(Math.abs(estimateDiagramHeight(data(chain)) - 628)).toBeLessThan(60)
  })
  it('keeps a horizontal flowchart a band and reads the caption in', () => {
    expect(estimateDiagramHeight(data('flowchart LR\n  A --> B --> C'))).toBeLessThan(220)
    expect(estimateDiagramHeight(data('flowchart LR\n  A --> B', 'Postup'))).toBeGreaterThan(estimateDiagramHeight(data('flowchart LR\n  A --> B')))
  })
  it('adds a band for each branch of a horizontal flowchart', () => {
    const branched = 'flowchart LR\n  A[Start] --> B{Rozhodnutí}\n  B -->|ano| C[Server]\n  B -->|ne| D[Klient]\n  C --> E[Konec]\n  D --> E'
    expect(Math.abs(estimateDiagramHeight(data(branched, 'Diagram')) - 258)).toBeLessThan(20)
  })
})
