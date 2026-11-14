import { describe, expect, it } from 'vitest';
import { projectReadonlySelection } from './readonlyProjection';
describe('readonly safe projection', () => {
  it('reads selected ordinary text and replaces whole custom widgets including partial selections with opaque tokens', () => {
    const root = document.createElement('div'); root.innerHTML = '<p>Ordinary <span data-md-module="contact" data-secret-id="metadata">private label<input value="hidden secret"></span> ending</p>';
    const range = document.createRange(); range.setStart(root.firstChild!.firstChild!, 3); range.setEnd(root.firstChild!.lastChild!, 4);
    const result = projectReadonlySelection(root, range)!;
    expect(result.text).toMatch(/^inary ⟦c:[a-f0-9]{16}:1⟧ end$/);
    expect(result.text).not.toMatch(/private|metadata|secret/);
    const partial = document.createRange(); partial.selectNodeContents(root.querySelector('span')!.firstChild!);
    // Only an element: nothing for the AI nor the search, the bar keeps its other actions (P11).
    expect(projectReadonlySelection(root, partial)).toEqual({ text: null, searchText: null });
  });
  it('refuses outside, hidden and detected plaintext secrets without reading their labels or attributes', () => {
    const root = document.createElement('div'); root.innerHTML = '<p>password: Secret123!</p><p hidden>hidden</p>';
    const range = document.createRange(); range.selectNodeContents(root);
    expect(projectReadonlySelection(root, range)).toEqual({ text: null, searchText: null });
    root.innerHTML = '<p>Safe</p><a href="https://private.example">private</a>';
    range.selectNodeContents(root);
    expect(projectReadonlySelection(root, range)!.text).not.toContain('private');
    range.selectNodeContents(document.body);
    expect(projectReadonlySelection(root, range)).toBeNull();
  });
  it('gives the AI nothing over the one limit, while the search keeps the text', () => {
    const root = document.createElement('div'); root.innerHTML = `<p>${'slovo '.repeat(400)}</p>`;
    const range = document.createRange(); range.selectNodeContents(root);
    const result = projectReadonlySelection(root, range)!;
    expect(result.text).toBeNull();
    expect(result.searchText).toContain('slovo');
  });
  it('points a selection inside a heading at its whole section, up to the next heading of the same level', () => {
    const root = document.createElement('div');
    root.innerHTML = '<h2 data-heading-id="a">Úvod</h2><p>První odstavec.</p><h3 data-heading-id="b">Podrobnosti</h3><p>Druhý.</p><h2 data-heading-id="c">Další</h2><p>Mimo.</p>';
    const range = document.createRange(); range.setStart(root.firstChild!.firstChild!, 1); range.setEnd(root.firstChild!.firstChild!, 3);
    const result = projectReadonlySelection(root, range)!;
    expect(result.section).toBe('Úvod');
    expect(result.text).toContain('Úvod');
    expect(result.text).toContain('Podrobnosti');
    expect(result.text).toContain('Druhý.');
    expect(result.text).not.toContain('Mimo');
    expect(result.searchText).toBe('vo');
  });
  it('takes the section also from a Chrome triple-click, which ends at the start of the next block', () => {
    const root = document.createElement('div');
    root.innerHTML = '<h2 data-heading-id="a">Úvod</h2><p>První odstavec.</p><h2 data-heading-id="c">Další</h2><p>Mimo.</p>';
    const range = document.createRange(); range.setStart(root.firstChild!.firstChild!, 0); range.setEnd(root.children[1]!.firstChild!, 0);
    const result = projectReadonlySelection(root, range)!;
    expect(result.section).toBe('Úvod');
    expect(result.text).toContain('První odstavec.');
    range.setEnd(root.children[1]!.firstChild!, 5);
    expect(projectReadonlySelection(root, range)!.section).toBeUndefined();
  });
  it('falls back to the selected words when the section is over the limit, so the AI intents stay', () => {
    const root = document.createElement('div');
    root.innerHTML = `<h2 data-heading-id="a">Úvod</h2>${'<p>Dlouhý odstavec se spoustou slov.</p>'.repeat(100)}`;
    const range = document.createRange(); range.setStart(root.firstChild!.firstChild!, 0); range.setEnd(root.firstChild!.firstChild!, 4);
    expect(projectReadonlySelection(root, range)).toEqual({ text: 'Úvod', searchText: 'Úvod' });
  });
});

it('rejects a partial credential from a secret-bearing ordinary block', () => {
  const root = document.createElement('div'); root.innerHTML = '<p>Public words and password: Secret123!</p>';
  const range = document.createRange(); range.setStart(root.firstChild!.firstChild!,26); range.setEnd(root.firstChild!.firstChild!,36);
  expect(projectReadonlySelection(root,range)).toEqual({ text: null, searchText: null });
});
