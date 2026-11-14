import { computed, ref, watch } from "vue";
import { isPickEnter } from "./pickEnter";

/**
 * The first Esc of a search: empties the typed text and stops there (the browser would clear a search field and close a dialog around
 * it on its own, so both are taken over); with nothing typed the Esc goes on to the owner. True when it emptied the field.
 */
export function clearOnEscape(event: KeyboardEvent, text: string, clear: () => void): boolean {
  if (event.key !== "Escape" || event.isComposing || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey || !text) return false;
  event.preventDefault();
  event.stopPropagation();
  clear();
  return true;
}

/**
 * The `data-md-own-escape` of a search input: while it holds text it tells an editor popover around it (contextToolbar.ts closes the
 * whole panel on Esc, in the capture phase) to leave that first Esc to the field, which empties itself. Harmless outside a popover.
 */
export const ownEscape = (text: string) => (text ? "" : undefined);

/**
 * The keys of the editor's suggestion list (createSuggestionList) under a search field, after `useSearchKeys` had its say: the list selects
 * its first row all along, but an untouched one draws none, so its first arrow only shows that row instead of skipping it; every later
 * key is the list's (arrows, Home, End, Enter on the row it is on) and counts as moving. True when it handled the key.
 */
export function listKeys(event: KeyboardEvent, keys: { lit: { value: boolean }; move(): void }, list: { onKeyDown(event: KeyboardEvent): boolean }): boolean {
  if (!keys.lit.value && (event.key === "ArrowDown" || event.key === "ArrowUp")) { event.preventDefault(); keys.move(); return true; }
  if ((event.key !== "Enter" || isPickEnter(event)) && list.onKeyDown(event)) {
    if (event.key !== "Enter") keys.move();
    event.preventDefault();
    return true;
  }
  return false;
}

export interface SearchKeysOptions {
  /** What is typed in the field. */
  text: () => string;
  /** Empties the field: the first Esc. */
  clear: () => void;
  /** Takes the preselected first result (or the row the arrows moved to); false when there is none. */
  take: () => boolean | void;
  /** The shown results are still those of an earlier text: an Enter or Tab then waits for the answer and takes its first result. */
  waiting?: () => boolean;
  /**
   * Something that changes when the shown results are replaced (the options array). An Enter given while `waiting` then ends with the
   * answer's arrival, not with the first moment `waiting` reads false: an owner's flag can drop for an instant between its debounce
   * and its request while the rows shown are still the previous text's.
   */
  fresh?: () => unknown;
  /** Enter in the empty field. True when the owner did something with it (confirms what is ticked); else nothing is taken. */
  empty?: (event: KeyboardEvent) => boolean | void;
  /** false for a list where a pick is a tick among several: Tab then only goes on to the list. */
  tab?: boolean;
  /** The pick closes the picker or goes on with the same field (a popover, several tags): Tab takes it and the focus stays instead of going on to the next field. */
  keepFocus?: boolean;
}

/**
 * The one keyboard model of every search field and picker (docs/interaction-rules.md P3, P8), PartyInput's and SearchField's:
 * the first Esc empties the text and the next one goes on to the owner (which closes); Enter takes the preselected first result
 * once something is typed, and an Enter given before the answer came waits for it; Tab takes it too and goes on to the next
 * field; Enter in the empty, untouched field takes nothing (the owner's `empty`), and once the arrows `move()` it takes the row they reached. The owner forwards the field's keydown to `onKeydown`, which says whether it
 * handled the key, and keeps its arrows, Home and End. `waiting` can be a reactive getter (a `loading` prop): the wait then ends by
 * itself; with a plain flag the owner calls `settled()` once the answer is shown. Typing after an Enter forgets that Enter.
 */
export function useSearchKeys(options: SearchKeysOptions) {
  let pending = false, shown: unknown;
  const typed = () => options.text().trim().length > 0;
  // The owner's arrows (or the pointer) moved to a row; a new text forgets it.
  const moved = ref(false);
  function takeOrWait() {
    if (options.waiting?.()) { pending = true; shown = options.fresh?.(); return true; }
    return options.take() !== false;
  }
  function settled() {
    if (!pending) return;
    pending = false;
    if (typed()) options.take();
  }
  // Sync, so a text the owner sets and then asks `ahead()` for (a picker opened by typing) keeps its Enter.
  watch(options.text, () => { pending = false; moved.value = false; }, { flush: "sync" });
  watch(() => options.waiting?.() ?? false, now => { if (!now && (!options.fresh || options.fresh() !== shown)) settled(); }, { flush: "post" });
  // The rows were replaced after the flag had dropped (the gap above): the wait ends now.
  if (options.fresh) watch(options.fresh, () => { if (!(options.waiting?.() ?? false)) settled(); }, { flush: "post" });
  return {
    onKeydown(event: KeyboardEvent): boolean {
      if (event.isComposing || event.keyCode === 229) return false;
      if (event.key === "Escape") return clearOnEscape(event, options.text(), options.clear);
      if (isPickEnter(event)) {
        if (!typed() && !moved.value) {
          if (!options.empty?.(event)) return false;
          event.preventDefault();
          return true;
        }
        event.preventDefault();
        takeOrWait();
        return true;
      }
      // Tab goes on to the next field (unless the pick keeps the focus); what the owner shows first goes in with it.
      if (options.tab !== false && event.key === "Tab" && !event.shiftKey && !event.ctrlKey && !event.metaKey && !event.altKey && typed()) {
        const took = takeOrWait();
        if (took && options.keepFocus) event.preventDefault();
        return took;
      }
      return false;
    },
    settled,
    /** The arrows (or the pointer) moved to a row of the owner's list: Enter then takes that row even with nothing typed. */
    move() { moved.value = true; },
    /** A picker that opens again: nothing has been moved to yet. */
    rest() { moved.value = false; },
    /** Something is typed or the arrows moved: only then is a row highlighted (and announced), because only then does Enter take it. */
    lit: computed(() => moved.value || typed()),
    /** An Enter that was pressed before the picker existed (typed while it opened): taken as soon as the first answer is shown. */
    ahead() { pending = true; },
  };
}
