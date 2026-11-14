import { computed, ref } from "vue";
import type { ChoiceOption } from "../../settings/types";

/** Most items one insertion may add to a board (spec §3.5: a grid of cards, at most 100). */
export const maxPicked = 100;

/**
 * The basket of a multi-pick dialog: what was picked, in the order it was picked, kept apart from the results on show (a new
 * search replaces those, the picks stay). Picking an option again removes it; past `limit` nothing more is added.
 */
export function useMultiPick(limit = maxPicked) {
  const picked = ref<ChoiceOption[]>([]);
  const full = computed(() => picked.value.length >= limit);
  const has = (value: string) => picked.value.some(option => option.value === value);
  function toggle(option: ChoiceOption) {
    if (has(option.value)) picked.value = picked.value.filter(item => item.value !== option.value);
    else if (!full.value) picked.value = [...picked.value, option];
  }
  const clear = () => { picked.value = []; };
  return { picked, full, limit, has, toggle, clear };
}
