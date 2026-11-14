import { computed, ref, type InjectionKey } from "vue";
import { adminApi, type Group, type UserDirectorySummary } from "../../../administration";

/**
 * The numbers Nastavení wiki shows about its people outside the Lidé pages (refresh 2.5): the counts in the
 * settings rail and the Lidé and Skupiny tiles on Přehled. Read when the surface opens and again on every move
 * among the people pages and Přehled, so an invited person or a new group counts at once; a failed read only
 * leaves the numbers out. The people are counted by the server (the account list itself stops at one page).
 */
export function createDirectorySummary() {
  const people = ref<UserDirectorySummary>({ total: 0, pending: 0, deactivated: 0, admins: 0 });
  const groups = ref<Group[]>([]);
  const loaded = ref(false);
  const failed = ref(false);
  let generation = 0;
  async function load() {
    const current = ++generation;
    try {
      const [counted, teams] = await Promise.all([adminApi.userSummary(), adminApi.groups()]);
      // Reloads follow navigation: an older answer must not overwrite a newer one.
      if (current !== generation) return;
      people.value = counted;
      groups.value = teams;
      loaded.value = true;
      failed.value = false;
    } catch {
      if (current === generation) failed.value = true;
    }
  }
  const total = computed(() => people.value.total);
  const pending = computed(() => people.value.pending);
  const hubGroups = computed(() => groups.value.filter((group) => group.source === "hub").length);
  return { people, total, groups, loaded, failed, pending, hubGroups, load };
}
export type DirectorySummary = ReturnType<typeof createDirectorySummary>;
/** GlobalSettings' summary for its pages: Lidé takes its tallies from it instead of asking the server a second time. */
export const directorySummaryKey: InjectionKey<DirectorySummary> = Symbol("directorySummary");
