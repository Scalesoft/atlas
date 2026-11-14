/**
 * `@before-leave` of a `<Transition>` whose leaving child fades out while its replacement is already there (the page
 * cross-fade of the settings, a list that gives way to its empty state). For the length of the fade the document holds
 * both: without this the keyboard and assistive technology find two sets of the same controls, and a test or a script
 * that looks for one by its role finds two. The child is removed when its leave ends, so nothing is put back.
 */
export function outOfReach(element: Element) {
  element.setAttribute("inert", "");
  element.setAttribute("aria-hidden", "true");
}
