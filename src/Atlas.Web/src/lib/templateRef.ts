import type { ComponentPublicInstance } from "vue";

/**
 * The DOM element behind a template ref.
 *
 * The generated shadcn components are single-root wrappers that expose no
 * imperative API, so a template ref on `<Input>` or `<Button>` resolves to the
 * component instance rather than to the element. `$el` is the way through, and
 * it is typed `any`; funnelling every caller through here keeps that cast in
 * one place instead of scattering it over the focus, select and measure sites.
 */
export function hostElement(
  target: HTMLElement | ComponentPublicInstance | null | undefined,
): HTMLElement | null {
  if (!target) return null;
  const element = target instanceof HTMLElement ? target : (target.$el as unknown);
  return element instanceof HTMLElement ? element : null;
}

/** {@link hostElement}, narrowed for the call sites that select text. */
export function fieldElement(
  target: HTMLInputElement | ComponentPublicInstance | null | undefined,
): HTMLInputElement | null {
  const element = hostElement(target);
  return element instanceof HTMLInputElement ? element : null;
}
