import { computed, reactive, shallowRef, watch } from "vue";

/**
 * The state of a list's bulk selection (docs/ux-concepts.md „Bulk selection“, BulkSelectBar draws it): Notion's model, ticking a
 * row starts it, there is no separate mode. `ids` are the rows that may be ticked now (shown and allowed); a row that leaves
 * them leaves the selection, so an action never runs on a row the reader cannot see.
 */
export function useBulkSelection(ids: () => readonly string[]) {
  const selected = shallowRef<ReadonlySet<string>>(new Set());
  const has = (id: string) => selected.value.has(id);
  /** Ticks or unticks one row (`on` omitted: the other way round); a row that may not be ticked stays out. */
  function toggle(id: string, on = !has(id)) {
    if (on === has(id) || (on && !ids().includes(id))) return;
    const next = new Set(selected.value);
    if (on) next.add(id); else next.delete(id);
    selected.value = next;
  }
  const total = computed(() => ids().length);
  const count = computed(() => selected.value.size);
  /** The select-all box: every tickable row, none, or some of them. */
  const all = computed<boolean | "indeterminate">(() => {
    const ticked = ids().filter(has).length;
    return !ticked ? false : ticked === total.value ? true : "indeterminate";
  });
  const toggleAll = () => { selected.value = all.value === true ? new Set() : new Set(ids()); };
  const clear = () => { if (count.value) selected.value = new Set(); };
  watch(() => ids().join("\n"), () => {
    const allowed = new Set(ids());
    if ([...selected.value].some((id) => !allowed.has(id))) selected.value = new Set([...selected.value].filter((id) => allowed.has(id)));
  });
  return reactive({ selected, total, count, all, has, toggle, toggleAll, clear });
}
export type BulkSelection = ReturnType<typeof useBulkSelection>;
