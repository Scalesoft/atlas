/**
 * The full text of a row as its native hover hint, only while the row cuts the text short: for
 * `@pointerenter` on a link or button whose label ellipsises. A `title` that repeats fully visible text
 * would only be read twice by assistive technology, and rendering one per row costs a long list nothing
 * this way. `label` selects the clipped element inside the hovered one; without it the element itself.
 */
export function truncatedTitle(event: Event, label?: string) {
  const host = event.currentTarget;
  if (!(host instanceof HTMLElement)) return;
  const text = label ? host.querySelector<HTMLElement>(label) : host;
  if (text && text.scrollWidth > text.clientWidth) host.title = text.textContent?.trim() ?? "";
  else host.removeAttribute("title");
}
