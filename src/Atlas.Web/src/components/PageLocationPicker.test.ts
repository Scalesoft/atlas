import { afterEach, expect, it } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';
import PageLocationPicker from './PageLocationPicker.vue';

let wrapper: VueWrapper;
afterEach(() => wrapper?.unmount());
it('browses collapsed branches, finds deep titles without accents and excludes forbidden destinations', async () => {
  wrapper = mount(PageLocationPicker, { props: { modelValue: '', label: 'Umístění', items: [
    { id: 'root', parentId: null, title: 'Dokumentace', canEdit: false },
    { id: 'child', parentId: 'root', title: 'Přístupy', canEdit: true },
    { id: 'self', parentId: null, title: 'Aktuální', canEdit: true },
  ], excluded: new Set(['self']) } });
  expect(wrapper.findAll('[role=treeitem]')).toHaveLength(2);
  await wrapper.get('button[aria-label="Rozbalit Dokumentace"]').trigger('click');
  await wrapper.get('[data-page-id="child"]').trigger('keydown', { key: 'Enter' });
  expect(wrapper.emitted('update:modelValue')).toEqual([['child']]);
  await wrapper.get('input').setValue('pristupy');
  expect(wrapper.text()).toContain('Dokumentace / Přístupy');
  expect(wrapper.text()).not.toContain('Aktuální');
});
it('bounds a large level and reveals the selected branch', async () => {
  wrapper = mount(PageLocationPicker, { props: { modelValue: 'deep', label: 'Umístění', items: [
    { id: 'parent', parentId: null, title: 'A parent', canEdit: true },
    { id: 'deep', parentId: 'parent', title: 'Deep', canEdit: true },
    ...Array.from({ length: 1000 }, (_, i) => ({ id: String(i), parentId: null, title: 'Z page ' + i, canEdit: true })),
  ] } });
  expect(wrapper.findAll('[role=treeitem]').length).toBeLessThan(100);
  expect(wrapper.get('[data-page-id="deep"]').attributes('aria-selected')).toBe('true');
  expect(wrapper.text()).toContain('Zobrazit další');
});
it('does not let Enter in destination search submit the surrounding move form', async () => {
  wrapper = mount(PageLocationPicker, { props: { modelValue: '', label: 'Umístění', items: [] } });
  const enter = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
  wrapper.get('input').element.dispatchEvent(enter);
  expect(enter.defaultPrevented).toBe(true);
  expect(wrapper.emitted('update:modelValue')).toBeUndefined();
});
it('preselects the first allowed match of a search and takes it on Enter, not on a composing or modified Enter', async () => {
  wrapper = mount(PageLocationPicker, { props: { modelValue: '', label: 'Umístění', items: [
    { id: 'locked', parentId: null, title: 'Návody (jen čtení)', canEdit: false },
    { id: 'ok', parentId: null, title: 'Návody', canEdit: true },
  ] } });
  const input = wrapper.get('input');
  await input.setValue('navody');
  expect(wrapper.findAll('[data-first]').map(row => row.attributes('data-page-id'))).toEqual(['ok']);
  await input.trigger('keydown', { key: 'Enter', isComposing: true });
  await input.trigger('keydown', { key: 'Enter', ctrlKey: true });
  expect(wrapper.emitted('update:modelValue')).toBeUndefined();
  await input.trigger('keydown', { key: 'Enter' });
  expect(wrapper.emitted('update:modelValue')).toEqual([['ok']]);
});
it('moves from the search into the tree with Down, onto the preselected match', async () => {
  wrapper = mount(PageLocationPicker, { attachTo: document.body, props: { modelValue: '', label: 'Umístění', items: [
    { id: 'locked', parentId: null, title: 'Návody (jen čtení)', canEdit: false },
    { id: 'ok', parentId: null, title: 'Návody', canEdit: true },
  ] } });
  const input = wrapper.get('input');
  await input.setValue('navody');
  await input.trigger('keydown', { key: 'ArrowDown' });
  expect(document.activeElement).toBe(wrapper.get('[data-page-id="ok"]').element);
});
