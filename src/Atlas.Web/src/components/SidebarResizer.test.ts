import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';
import SidebarResizer from './SidebarResizer.vue';

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); vi.restoreAllMocks(); });
function render(value = 256, max = 480) {
  wrapper = mount(SidebarResizer, { props: { modelValue: value, max, 'onUpdate:modelValue': (width: number) => wrapper!.setProps({ modelValue: width }) } });
  // The separator is wrapped in a Tooltip whose root renders no element of its
  // own, so the separator is still the rendered root element -- but it is no
  // longer the wrapper's *component* root. Route DOM access to the separator and
  // component access to the wrapper, so every assertion below stays unchanged.
  const handle = wrapper.get('.sidebar-resizer');
  return {
    element: handle.element,
    attributes: (name: string) => handle.attributes(name),
    trigger: (event: string, options?: object) => handle.trigger(event, options),
    emitted: (event?: string) => (event ? wrapper!.emitted(event) : wrapper!.emitted()),
    setProps: (props: object) => wrapper!.setProps(props),
    vm: wrapper!.vm,
    unmount: () => wrapper!.unmount(),
  };
}
function pointer(type: string, x: number, id = 1) {
  const event = new Event(type, { bubbles: true, cancelable: true });
  Object.assign(event, { clientX: x, pointerId: id, button: 0 });
  return event;
}
describe('sidebar resizing', () => {
  it('supports keyboard changes, bounds and a default-width reset', async () => {
    const app = render();
    await app.trigger('keydown', { key: 'ArrowRight' });
    expect(app.attributes('aria-valuenow')).toBe('272');
    await app.trigger('keydown', { key: 'End' });
    expect(app.attributes('aria-valuenow')).toBe('480');
    await app.trigger('keydown', { key: 'ArrowRight' });
    expect(app.attributes('aria-valuenow')).toBe('480');
    await app.trigger('keydown', { key: 'Home' });
    expect(app.attributes('aria-valuenow')).toBe('220');
    // Double-click restores the default rail of the 2.5 refresh (272 px).
    await app.trigger('dblclick');
    expect(app.attributes('aria-valuenow')).toBe('272');
    expect(app.emitted('commit')).toBeDefined();
  });
  it('resizes by pointer delta and persists only the final drag', async () => {
    const app = render();
    app.element.dispatchEvent(pointer('pointerdown', 256));
    window.dispatchEvent(pointer('pointermove', 356));
    await app.vm.$nextTick();
    expect(app.attributes('aria-valuenow')).toBe('356');
    expect(app.emitted('commit')).toBeUndefined();
    window.dispatchEvent(pointer('pointerup', 356));
    expect(app.emitted('commit')).toHaveLength(1);
  });
  it('restores the width on Escape and ignores another pointer', async () => {
    const app = render(300);
    app.element.dispatchEvent(pointer('pointerdown', 300));
    window.dispatchEvent(pointer('pointermove', 450, 2));
    await app.vm.$nextTick();
    expect(app.attributes('aria-valuenow')).toBe('300');
    window.dispatchEvent(pointer('pointermove', 450));
    await app.vm.$nextTick();
    await app.trigger('keydown', { key: 'Escape' });
    expect(app.attributes('aria-valuenow')).toBe('300');
    expect(app.emitted('commit')).toBeUndefined();
  });
  it('keeps the pointer while it drags, so the resize cursor and the hover stay with the handle past the limits', async () => {
    // Dragged past 480 or 220 px the pointer leaves the 9 px handle: over the article it turned into the default
    // cursor, over a link of the rail into a hand, and whatever lay under it answered as if pointed at.
    const app = render();
    const capture = vi.fn(), release = vi.fn();
    Object.assign(app.element, { setPointerCapture: capture, releasePointerCapture: release });
    app.element.dispatchEvent(pointer('pointerdown', 256, 7));
    expect(capture).toHaveBeenCalledWith(7);
    expect(release).not.toHaveBeenCalled();
    window.dispatchEvent(pointer('pointerup', 300, 7));
    expect(release).toHaveBeenCalledWith(7);
  });
  it('uses the available viewport bound and removes listeners when unmounted', async () => {
    const app = render(256, 280);
    const update = vi.fn();
    await app.setProps({ 'onUpdate:modelValue': update });
    await app.trigger('keydown', { key: 'End' });
    expect(update).toHaveBeenCalledWith(280);
    app.element.dispatchEvent(pointer('pointerdown', 280));
    app.unmount();
    window.dispatchEvent(pointer('pointermove', 350));
    expect(update).toHaveBeenCalledTimes(1);
  });
  it('cancels on window blur or changed bounds without saving and ignores another pointer cancellation', async () => {
    const app = render(300);
    app.element.dispatchEvent(pointer('pointerdown', 300));
    window.dispatchEvent(pointer('pointermove', 380));
    window.dispatchEvent(pointer('pointercancel', 380, 2));
    await app.vm.$nextTick();
    expect(app.attributes('aria-valuenow')).toBe('380');
    window.dispatchEvent(new Event('blur'));
    window.dispatchEvent(pointer('pointermove', 420));
    window.dispatchEvent(pointer('pointerup', 420));
    await app.vm.$nextTick();
    expect(app.attributes('aria-valuenow')).toBe('300');
    expect(app.emitted('commit')).toBeUndefined();
    app.element.dispatchEvent(pointer('pointerdown', 300));
    window.dispatchEvent(pointer('pointermove', 420));
    await app.vm.$nextTick();
    await app.setProps({ max: 340 });
    window.dispatchEvent(pointer('pointerup', 420));
    await app.vm.$nextTick();
    expect(app.attributes('aria-valuenow')).toBe('300');
    expect(app.emitted('commit')).toBeUndefined();
  });
});
