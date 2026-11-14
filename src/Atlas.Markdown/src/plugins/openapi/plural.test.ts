import { describe, expect, it } from 'vitest'
import { operationsCount, schemasCount } from './plural'

describe('openapi Czech plurals', () => {
  it('uses the right form for 0, 1, 2–4 and 5+', () => {
    expect([0, 1, 2, 4, 5].map(operationsCount)).toEqual(['0 operací', '1 operace', '2 operace', '4 operace', '5 operací'])
    expect([0, 1, 2, 4, 5].map(schemasCount)).toEqual(['0 schémat', '1 schéma', '2 schémata', '4 schémata', '5 schémat'])
  })
})
