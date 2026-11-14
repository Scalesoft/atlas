import { expect, test, type Page } from '@playwright/test'

// Replaces artifacts/ui-audit-2026-09-07/scripts/census-assert.cjs, which could
// not run at all -- it requires ./census-lib.cjs, absent from the repo and from
// `git log --all` -- and which typed into a real document (Ctrl+A, Delete) to
// reach editor states. With collaboration on, that deletion syncs to the server
// with no save step, so it could destroy a page in whatever instance it was
// pointed at.
//
// This measures instead: it navigates, reads computed styles and attributes, and
// never mutates content. It covers the invariants the 2026-09-07 UI unification
// won and that a component migration is most likely to undo.

const SHELL = '.atlas'

interface ShellRule { selector: string; text: string }

// Only the shell's own rules. The editor package ships its own stylesheets and
// is deliberately out of scope for the migration, so its rules must not be
// counted against the shell's budget.
async function shellRules(page: Page): Promise<ShellRule[]> {
  return page.evaluate(() =>
    [...document.styleSheets].flatMap((sheet) => {
      let rules: CSSRule[] = []
      try {
        rules = [...sheet.cssRules]
      } catch {
        return [] // cross-origin sheet, nothing to read
      }
      return rules
        .filter((rule): rule is CSSStyleRule => rule instanceof CSSStyleRule)
        .filter((rule) => !rule.selectorText.includes('.md-editor') && !rule.selectorText.includes('.ProseMirror'))
        .map((rule) => ({ selector: rule.selectorText, text: rule.cssText }))
    }),
  )
}

test.beforeEach(async ({ page }) => {
  await page.goto('/')
  await page.locator(SHELL).waitFor()
})

test('the shell paints exactly one focus ring', async ({ page }) => {
  // Two rules deliberately switch the ring *off* rather than paint a different
  // one: the route heading takes programmatic focus, and the search field draws
  // its own frame. Both predate this migration. What must stay singular is the
  // ring that is actually painted.
  const painted = (await shellRules(page))
    .filter((rule) => rule.selector.includes(':focus-visible'))
    .map((rule) => /outline:\s*([^;}]+)/.exec(rule.text)?.[1]?.trim())
    .filter((value): value is string => Boolean(value))
    .filter((value) => !/^(none|0|0px)$/.test(value))
  expect(painted.length, 'no focus-visible rule paints a ring at all').toBeGreaterThan(0)
  expect([...new Set(painted)]).toHaveLength(1)
})

test('the shell defines exactly one disabled treatment', async ({ page }) => {
  const opacities = (await shellRules(page))
    .filter((rule) => rule.selector.includes(':disabled') || rule.selector.includes('[disabled]'))
    .map((rule) => /(?:^|[;{\s])opacity:\s*([\d.]+)/.exec(rule.text)?.[1])
    .filter((value): value is string => Boolean(value))
  expect(opacities.length, 'no disabled rule found at all').toBeGreaterThan(0)
  expect([...new Set(opacities)]).toHaveLength(1)
})

test('every shell icon comes from the shared registry, at one stroke and one viewBox', async ({ page }) => {
  const icons = await page.$$eval(`${SHELL} svg`, (svgs) =>
    svgs
      .filter((svg) => !svg.closest('.md-editor') && !svg.closest('.ProseMirror'))
      .map((svg) => ({
        stamped: svg.hasAttribute('data-icon'),
        stroke: svg.getAttribute('stroke-width'),
        viewBox: svg.getAttribute('viewBox'),
        outer: svg.outerHTML.slice(0, 120),
      })),
  )
  expect(icons.length, 'no icons found; the page probably did not render').toBeGreaterThan(0)
  expect(icons.filter((icon) => !icon.stamped).map((icon) => icon.outer)).toEqual([])
  expect([...new Set(icons.map((icon) => icon.stroke))]).toEqual(['1.75'])
  expect([...new Set(icons.map((icon) => icon.viewBox))]).toEqual(['0 0 24 24'])
})

test('controls are set in one typeface', async ({ page }) => {
  const families = await page.$$eval(
    `${SHELL} button, ${SHELL} input, ${SHELL} select, ${SHELL} [role="menuitem"], ${SHELL} [role="tab"]`,
    (elements) =>
      [...new Set(
        elements
          .filter((element) => !element.closest('.md-editor'))
          .map((element) => getComputedStyle(element).fontFamily.split(',')[0]!.replace(/["']/g, '').trim()),
      )],
  )
  expect(families).toEqual(['DM Sans'])
})

test('every dialog wears the same surface', async ({ page }) => {
  await page.locator('.search-launch').click()
  await page.locator('dialog[open]').waitFor()
  const surfaces = await page.$$eval('dialog[open]', (dialogs) =>
    [...new Set(dialogs.map((dialog) => {
      const style = getComputedStyle(dialog)
      return `${style.backgroundColor}|${style.borderRadius}|${style.boxShadow}`
    }))],
  )
  expect(surfaces).toHaveLength(1)
  await page.keyboard.press('Escape')
})

test('no overlay outlives a route change', async ({ page }) => {
  // The 2026-09-07 audit found dialogs and menus surviving SPA navigation and
  // blocking clicks on the page underneath.
  await page.locator('.search-launch').click()
  await page.locator('dialog[open]').waitFor()
  await page.evaluate(() => { location.hash = '#/' })
  await expect(page.locator('dialog[open]')).toHaveCount(0)
})

test('no menu outlives a route change either', async ({ page }) => {
  // Opened for real: asserting a count of zero without opening one first would
  // pass whether or not the behaviour holds.
  const trigger = page.locator('[aria-haspopup="menu"]').first()
  await trigger.click()
  await expect(page.locator('[role="menu"]')).toHaveCount(1)
  await page.evaluate(() => { location.hash = '#/' })
  await expect(page.locator('[role="menu"]')).toHaveCount(0)
})
