import { expect, it } from 'vitest'
import { COLLABORATOR_COLORS, collaboratorColor, markdownUserColor, markdownUserColorSlot } from './userColor'

const luminance = (hex: string) => {
  const [r, g, b] = [1, 3, 5].map(i => Number.parseInt(hex.slice(i, i + 2), 16) / 255).map(c => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4))
  return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!
}
const against = (hex: string, surface: number) => (Math.max(luminance(hex), surface) + 0.05) / (Math.min(luminance(hex), surface) + 0.05)

it('is the one palette: the 12 appearance hues, literal colours, each readable as a caret on its surface and under a white label', () => {
  expect(COLLABORATOR_COLORS.map(color => color.id)).toEqual(['red', 'blue', 'amber', 'violet', 'green', 'pink', 'cyan', 'orange', 'indigo', 'lime', 'slate', 'teal'])
  const darkSurface = luminance('#1c1c1e')
  for (const color of COLLABORATOR_COLORS) {
    expect(color.light).toMatch(/^#[0-9a-f]{6}$/)
    expect(color.dark).toMatch(/^#[0-9a-f]{6}$/)
    expect(against(color.light, 1)).toBeGreaterThanOrEqual(4.5) // on white, and a white label on it
    expect(against(color.dark, 1)).toBeGreaterThanOrEqual(4.5) // the label stays white on the dark variant
    expect(against(color.dark, darkSurface)).toBeGreaterThanOrEqual(3) // a caret on a dark surface
  }
  expect(new Set(COLLABORATOR_COLORS.flatMap(color => [color.light, color.dark])).size).toBe(24)
})

it('starts from the hash of the user id (the server\'s CollaboratorPalette.Auto, same values) and swaps a variant by surface', () => {
  expect([markdownUserColorSlot('verified-id'), markdownUserColorSlot('00000000-0000-0000-0000-000000000001'), markdownUserColorSlot('ffffffff-0000-0000-0000-000000000000')]).toEqual([4, 1, 8])
  expect(markdownUserColor('verified-id')).toBe(COLLABORATOR_COLORS[4]!.light)
  expect(collaboratorColor('#1863b6', true)).toBe('#2e76cb')
  expect(collaboratorColor('#2e76cb', false)).toBe('#1863b6')
  expect(collaboratorColor('#7c3aed', true)).toBe('#7c3aed') // an agent's or a host's own colour stays
})
