import { afterEach, expect, it, vi } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';
import ActionDisclosure from './ActionDisclosure.vue';
import ActionDisclosureItem from './ActionDisclosureItem.vue';

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); document.body.replaceChildren(); vi.restoreAllMocks(); });

// The panel is portalled and only exists while open, so it is found in the
// document rather than inside the wrapper. Actions are components now rather
// than slotted plain buttons: stamping `role="menuitem"` from an onUpdated hook
// labelled them but could never make them participate in keyboard navigation.
const settle = () => new Promise(resolve => setTimeout(resolve, 30));
const menu = () => document.querySelector('[role=menu]');
const items = () => [...document.querySelectorAll<HTMLElement>('[role=menu] [role=menuitem]')];

function open(props: Record<string, unknown> = {}, slots?: Record<string, string>) {
  wrapper = mount({
    components: { ActionDisclosure, ActionDisclosureItem },
    template: `<ActionDisclosure v-bind="$attrs">${slots?.default ?? '<ActionDisclosureItem>Export</ActionDisclosureItem>'}${slots?.trigger ? `<template #trigger>${slots.trigger}</template>` : ''}</ActionDisclosure>`,
    inheritAttrs: false,
  }, { attachTo: document.body, attrs: { label: 'Akce prostoru', ...props } });
  return wrapper.get('[aria-expanded]');
}

it('advertises the trigger as a menu button (finding 15)', () => {
  expect(open().attributes('aria-haspopup')).toBe('menu');
});

it('draws the trigger with the shared ellipsis icon, not a bundled Lucide component', () => {
  expect(open().find('svg[data-icon="ellipsis"]').exists()).toBe(true);
});

it('renders the caller’s trigger instead of the ellipsis (space actions on the breadcrumb crumb)', async () => {
  const trigger = open({}, {
    default: '<ActionDisclosureItem>Export</ActionDisclosureItem>',
    trigger: 'Vývoj<svg data-icon="chevron-down"></svg>',
  });
  // The trigger is the generated Button in its ghost/icon-sm shape; the caller
  // supplies what goes inside it, not how it is styled.
  expect(trigger.classes()).toContain('size-8');
  expect(trigger.text()).toBe('Vývoj');
  expect(trigger.find('svg[data-icon="chevron-down"]').exists()).toBe(true);
  expect(trigger.attributes('aria-label')).toBe('Akce prostoru');
  await trigger.trigger('click'); await settle();
  expect(menu()).not.toBeNull();
});

it('exposes the panel as a menu and its actions as menu items', async () => {
  const trigger = open({}, { default: '<ActionDisclosureItem>Export</ActionDisclosureItem><ActionDisclosureItem>Uspořádat</ActionDisclosureItem>' });
  await trigger.trigger('click'); await settle();
  expect(menu()!.getAttribute('role')).toBe('menu');
  expect(items().map(item => item.textContent)).toEqual(['Export', 'Uspořádat']);
});

it('treats an action added after the first render as a menu item', async () => {
  wrapper = mount({
    components: { ActionDisclosure, ActionDisclosureItem },
    template: '<ActionDisclosure label="Akce prostoru"><ActionDisclosureItem>Export</ActionDisclosureItem><ActionDisclosureItem v-if="extra">Uspořádat</ActionDisclosureItem></ActionDisclosure>',
    data: () => ({ extra: false }),
  }, { attachTo: document.body });
  await wrapper.get('[aria-expanded]').trigger('click'); await settle();
  await wrapper.setData({ extra: true }); await settle();
  expect(items().map(item => item.textContent)).toEqual(['Export', 'Uspořádat']);
});

it('does not treat arbitrary markup inside the panel as a menu item (finding 15)', async () => {
  const trigger = open({}, { default: '<ActionDisclosureItem>Export</ActionDisclosureItem><div class="wrapper"><button>Vnořené</button></div>' });
  await trigger.trigger('click'); await settle();
  expect(items().map(item => item.textContent)).toEqual(['Export']);
  expect(document.querySelector('.wrapper > button')?.getAttribute('role')).toBeNull();
});

it('opens from keyboard, skips disabled actions, and restores the trigger on Escape', async () => {
  const trigger = open({}, { default: '<ActionDisclosureItem disabled>Nedostupné</ActionDisclosureItem><ActionDisclosureItem>Export</ActionDisclosureItem><ActionDisclosureItem>Historie</ActionDisclosureItem>' });
  expect(trigger.attributes('aria-expanded')).toBe('false');
  expect(menu()).toBeNull();
  await trigger.trigger('keydown', { key: 'ArrowDown' }); await settle();
  expect(trigger.attributes('aria-expanded')).toBe('true');
  expect(document.activeElement?.textContent).toBe('Export');
  document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
  await settle();
  expect(document.activeElement?.textContent).toBe('Historie');
  document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  await settle();
  expect(trigger.attributes('aria-expanded')).toBe('false');
  expect(document.activeElement).toBe(trigger.element);
});

it('moves with the arrows, Home and End among the shown actions only (the page menu keeps its phone-only actions in the DOM)', async () => {
  const trigger = open({}, { default: '<ActionDisclosureItem style="display: none">Hledat ve wiki</ActionDisclosureItem><ActionDisclosureItem>Export</ActionDisclosureItem><ActionDisclosureItem style="display: none">Kopírovat odkaz</ActionDisclosureItem><ActionDisclosureItem>Historie</ActionDisclosureItem>' });
  // Opened by the pointer, focus rests on the menu itself: the first arrow has to enter it.
  await trigger.trigger('click'); await settle();
  const press = async (key: string) => { document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true })); await settle(); };
  (menu() as HTMLElement).focus();
  await press('ArrowDown');
  expect(document.activeElement?.textContent).toBe('Export');
  await press('ArrowUp');
  expect(document.activeElement?.textContent).toBe('Export');
  await press('ArrowDown');
  expect(document.activeElement?.textContent).toBe('Historie');
  await press('ArrowDown');
  expect(document.activeElement?.textContent).toBe('Historie');
  await press('Home');
  expect(document.activeElement?.textContent).toBe('Export');
  await press('End');
  expect(document.activeElement?.textContent).toBe('Historie');
});

it('focuses the last action on ArrowUp', async () => {
  const trigger = open({}, { default: '<ActionDisclosureItem>Export</ActionDisclosureItem><ActionDisclosureItem>Uspořádat</ActionDisclosureItem><ActionDisclosureItem>Historie</ActionDisclosureItem>' });
  (trigger.element as HTMLButtonElement).focus();
  await trigger.trigger('keydown', { key: 'ArrowUp' });
  await settle(); await settle();
  expect(document.activeElement?.textContent).toBe('Historie');
});

it('closes after selecting an action, and on a click outside', async () => {
  const trigger = open();
  await trigger.trigger('click'); await settle();
  items()[0]!.click(); await settle();
  expect(trigger.attributes('aria-expanded')).toBe('false');
  await trigger.trigger('click'); await settle();
  expect(menu()).not.toBeNull();
  document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
  await settle();
  expect(trigger.attributes('aria-expanded')).toBe('false');
});

it('closes when the SPA route changes', async () => {
  const trigger = open();
  await trigger.trigger('click'); await settle();
  expect(trigger.attributes('aria-expanded')).toBe('true');
  window.dispatchEvent(new HashChangeEvent('hashchange'));
  await settle();
  expect(trigger.attributes('aria-expanded')).toBe('false');
  expect(menu()).toBeNull();
});

it('keeps surrounding navigation accessible and accepts its first outside click', async () => {
  const destination = document.createElement('button');
  const navigate = vi.fn();
  destination.textContent = 'Jiná stránka';
  destination.addEventListener('click', navigate);
  document.body.append(destination);
  const trigger = open();
  await trigger.trigger('click'); await settle();
  expect(destination.closest('[aria-hidden="true"]')).toBeNull();
  expect(document.body.style.pointerEvents).not.toBe('none');
  destination.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
  destination.focus();
  destination.click();
  await settle();
  expect(navigate).toHaveBeenCalledOnce();
  expect(trigger.attributes('aria-expanded')).toBe('false');
  expect(document.activeElement).toBe(destination);
});

it('closes rather than relocating when its trigger scrolls out of view', async () => {
  const trigger = open();
  await trigger.trigger('click'); await settle();
  expect(menu()).not.toBeNull();
  vi.spyOn(trigger.element, 'getBoundingClientRect').mockReturnValue(new DOMRect(20, -100, 36, 36));
  document.dispatchEvent(new Event('scroll'));
  await settle();
  expect(trigger.attributes('aria-expanded')).toBe('false');
  expect(menu()).toBeNull();
});

it('closes when its trigger becomes disabled', async () => {
  wrapper = mount({
    components: { ActionDisclosure, ActionDisclosureItem },
    template: '<ActionDisclosure label="Akce prostoru" :disabled="off"><ActionDisclosureItem>Export</ActionDisclosureItem></ActionDisclosure>',
    data: () => ({ off: false }),
  }, { attachTo: document.body });
  await wrapper.get('[aria-expanded]').trigger('click'); await settle();
  expect(menu()).not.toBeNull();
  await wrapper.setData({ off: true }); await settle();
  expect(menu()).toBeNull();
});
