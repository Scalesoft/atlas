// @vitest-environment jsdom
import { expect, it } from 'vitest'
import { copyMarkdownOverlayTheme } from './overlayTheme'

it('copies semantic colors and surface tokens, drops stale tokens, and excludes host variables', () => {
  const source = document.createElement('div'), overlay = document.createElement('div')
  const values = { '--green': '#123456', '--amber': '#987654', '--paper': '#111111', '--line-2': '#222222', '--chip-green-fg': '#333333', '--shadow-1': 'none', '--host-secret': 'private' }
  for (const [key, value] of Object.entries(values)) source.style.setProperty(key, value)
  source.style.colorScheme = 'dark'; document.body.append(source)
  copyMarkdownOverlayTheme(source, overlay)
  for (const [key, value] of Object.entries(values).filter(([key]) => key !== '--host-secret')) expect(overlay.style.getPropertyValue(key)).toBe(value)
  expect(overlay.style.getPropertyValue('--host-secret')).toBe('')
  expect(overlay.style.colorScheme).toBe('dark')
  source.style.removeProperty('--green'); copyMarkdownOverlayTheme(source, overlay)
  expect(overlay.style.getPropertyValue('--green')).toBe('')
  source.remove()
})

it('copies the shared dimension tokens so a portal renders the same radius/shadow scale', () => {
  const source = document.createElement('div'), overlay = document.createElement('div')
  source.style.setProperty('--radius-md', '8px')
  source.style.setProperty('--shadow-menu', '0 8px 28px rgb(16 32 57 / .14)')
  document.body.append(source)
  copyMarkdownOverlayTheme(source, overlay)
  expect(overlay.style.getPropertyValue('--radius-md')).toBe('8px')
  expect(overlay.style.getPropertyValue('--shadow-menu')).toBe('0 8px 28px rgb(16 32 57 / .14)')
  source.remove()
})
