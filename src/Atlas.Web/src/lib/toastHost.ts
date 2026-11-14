import { shallowRef } from "vue";

/**
 * Where the toasts are drawn: inside the topmost open modal dialog, else in place. A modal dialog is in the top layer and
 * makes everything outside itself inert, so a toast beside it could neither be seen nor pressed ("Zpět").
 */
export const toastHost = shallowRef<HTMLElement | null>(null);
const open: HTMLElement[] = [];

export function toastsInside(dialog: HTMLElement, opened: boolean) {
  const at = open.indexOf(dialog);
  if (at >= 0) open.splice(at, 1);
  if (opened) open.push(dialog);
  toastHost.value = open.at(-1) ?? null;
}
