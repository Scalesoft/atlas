<script setup lang="ts">
import t from "../../i18n/contacts-ui";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId, useSlots, watch } from "vue";
import { errorMessage } from "../../api";
import { entityApi, type EntityApi, type EntityItem, type EntityQuery, type EntitySource } from "../../entities/entityApi";
import { createDebouncedLookup } from "../../lib/debouncedLookup";
import { focusFirst } from "../../lib/pickEnter";
import { useSearchKeys } from "../../lib/searchKeys";
import { rowKeys } from "../../keyboard/navigation";
import { boardLink, pageLink } from "../../pageLinks";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import OptionPills from "../settings/OptionPills.vue";
import SearchField from "../settings/SearchField.vue";
import { Button } from "../ui/button";
import ContactCard from "./ContactCard.vue";
import UsagePanel from "../objects/UsagePanel.vue";
import "../settings/settings.css";

/**
 * Kontakty napříč wiki (Atlas 3 plan §14.4): every person the reader may see, merged from contact cards and mentions in pages and from
 * the directory, each with the places it occurs in (a page opens at its heading, the directory at Knihovna → Kontakty), filtered by any
 * field and exported as CSV or vCard with the same rights. One switch above the search names where it looks, with the same words
 * wherever it is shown (UX1-14): in a space Tento prostor / Celá wiki, in Knihovna Adresář / Celá wiki, where the `directory` slot
 * (ContactDirectory) takes the place of the list, and Kdo co umí where the `skills` slot gives the graph (SkillGraph). Read-only by design: contacts are changed where they live (ContactDirectory for
 * the directory, the page for a card), so this is no second directory. Rows are ContactCard as the directory shows them. Typing
 * preselects the first person (`data-first`) and Enter opens its first place; the first Esc empties the search. `source`
 * is replaced by the gallery.
 */
/** `focus`: a topic named by the address (`#/library/contacts?q=`) shows the directory, where its dashboard is. */
const props = defineProps<{ spaceId?: string; source?: EntityApi; focus?: string }>();
const slots = useSlots();
const uid = useId();
const source = computed(() => props.source ?? entityApi);

const scope = ref(props.spaceId ? "space" : slots.directory ? "directory" : "instance");
const scopes = [
  ...(props.spaceId ? [{ value: "space", label: t.overviewView.thisSpace, icon: "book" }] : []),
  ...(slots.directory ? [{ value: "directory", label: t.overviewView.directory, icon: "users" }] : []),
  { value: "instance", label: t.overviewView.wholeWiki, icon: "globe" },
  // Kdo co umí: the graph of who knows what (SkillGraph), where Knihovna gives it.
  ...(slots.skills ? [{ value: "skills", label: t.skills.graphScope, icon: "network" }] : []),
];
const listing = computed(() => scope.value !== "directory" && scope.value !== "skills");
watch(() => props.focus, text => { if (text && slots.directory) scope.value = "directory"; });
const query = ref("");
const items = ref<EntityItem[]>([]);
const total = ref(0), page = ref(1), pageSize = ref(50);
const loading = ref(true), loaded = ref(false), error = ref("");
const root = ref<HTMLElement>();

const request = (q: string, at = 1): EntityQuery => (scope.value === "space" && props.spaceId ? { scope: "space", id: props.spaceId, q, page: at } : { scope: "instance", q, page: at });
const lookup = createDebouncedLookup<{ items: EntityItem[]; total: number; pageSize: number } | null>({
  find: async (text, signal) => {
    try {
      const list = await source.value.list("contact", request(text), signal);
      error.value = "";
      return list;
    } catch (failure) {
      if (signal.aborted) throw failure;
      error.value = errorMessage(failure);
      return null;
    }
  },
  onResult: list => {
    if (list) { items.value = list.items; total.value = list.total; pageSize.value = list.pageSize; page.value = 1; }
    loading.value = false; loaded.value = true;
  },
  empty: () => null,
  delayMs: 250,
  minLength: 0,
});
let typed = false;
watch(query, text => { typed = true; loading.value = true; lookup.update(text); });
watch(scope, () => {
  typed = true;
  if (!listing.value) { lookup.cancel(); return; }
  loading.value = true; lookup.update(query.value);
});
// The first list at once, without the typing delay; what is typed or switched meanwhile wins.
onMounted(async () => {
  if (!listing.value) return;
  const active = document.activeElement;
  if (!active || active === document.body || active.matches("h1")) root.value?.querySelector<HTMLInputElement>("input[data-entity-search]")?.focus({ preventScroll: true });
  try { const list = await source.value.list("contact", request("")); if (!typed) { items.value = list.items; total.value = list.total; pageSize.value = list.pageSize; } }
  catch (failure) { if (!typed) error.value = errorMessage(failure); }
  finally { if (!typed) { loading.value = false; loaded.value = true; } }
});
onBeforeUnmount(lookup.cancel);

async function more() {
  loading.value = true;
  try {
    const list = await source.value.list("contact", request(query.value, page.value + 1));
    items.value = [...items.value, ...list.items]; total.value = list.total; page.value = list.page; error.value = "";
  } catch (failure) { error.value = errorMessage(failure); }
  finally { loading.value = false; }
}

/** A person lists its first five places (PERF2-1); the rest come on demand, and the keyboard lands on the first of them. */
const placesBusy = ref<string>();
async function allPlaces(item: EntityItem) {
  const shown = item.sources.length;
  placesBusy.value = item.identityKey;
  try {
    const all = (await source.value.list("contact", { ...request(query.value), identity: item.identityKey })).items.find(other => other.identityKey === item.identityKey);
    if (all) { item.sources = all.sources; item.sourceCount = all.sourceCount; error.value = ""; }
    void nextTick(() => root.value?.querySelector(`[data-entity-row="${CSS.escape(item.identityKey)}"]`)?.querySelectorAll<HTMLElement>("[data-entity-place]")[shown]?.focus());
  } catch (failure) { error.value = errorMessage(failure); }
  finally { placesBusy.value = undefined; }
}

/** The person Enter takes once something is typed: the first one shown, marked `data-first`. */
const first = computed(() => (query.value.trim() ? items.value[0] : undefined));
/** Taking a person opens its first place (a page at its heading); a person with no place to open gives the keyboard to its first control. */
function openFirst() {
  const row = first.value && root.value?.querySelector<HTMLElement>(`[data-entity-row="${CSS.escape(first.value.identityKey)}"]`);
  if (!row) return false;
  const place = row.querySelector<HTMLElement>("[data-entity-place]");
  if (place) place.click(); else focusFirst(row, "a[href], button");
}
// Enter takes the first person (one typed while the list loads waits for it); the first Esc empties the search (P3, P8). Tab goes on to the places.
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; }, waiting: () => loading.value, tab: false, take: openFirst });
/** ↓ in the search goes to the first person's first place; the rest is every search's. */
function searchKeys(event: KeyboardEvent) {
  if (keys.onKeydown(event) || event.key !== "ArrowDown") return;
  const overview = (event.currentTarget as HTMLElement).closest(".contact-overview");
  if (!overview?.querySelector("[data-entity-row]")) return;
  event.preventDefault();
  // Not focusFirst: the row marked `data-first` is the li, which takes no focus; the keyboard goes to its first place.
  overview.querySelector<HTMLElement>("[data-entity-place]")?.focus();
}

const draftNote = t.overviewView.draftNote;
const href = (place: EntitySource) => (place.resourceType === "atlas.page" ? pageLink(place.resourceId, place.anchor ?? undefined) : "#/library/contacts");
const label = (place: EntitySource) => (place.resourceType === "atlas.page" ? place.title : t.overviewView.directory);
const exportQuery = computed(() => request(query.value));
/** A place of the usage panel: a page or a board opens by its hash route. */
function openPlace(id: string, typeId: string) {
  window.location.assign(typeId === "atlas.board" ? boardLink(id) : pageLink(id));
}
</script>

<template>
  <section ref="root" class="contact-overview settings-ui" :aria-labelledby="`${uid}-title`" :aria-busy="listing && loading">
    <h2 :id="`${uid}-title`" class="sr-only">{{ t.overviewView.title }}</h2>
    <OptionPills v-if="scopes.length > 1" v-model="scope" :options="scopes" :label="t.overviewView.show" hide-label data-entity-scope />
    <slot v-if="scope === 'directory'" name="directory" />
    <slot v-else-if="scope === 'skills'" name="skills" />
    <template v-else>
      <div class="contact-overview-toolbar">
        <SearchField v-model="query" :label="t.overviewView.searchLabel" :placeholder="t.overviewView.searchPlaceholder" wide data-entity-search data-focus-landing @keydown="searchKeys" />
        <div class="contact-overview-export">
          <Button as="a" variant="outline" size="sm" :href="source.exportUrl('contact', exportQuery, 'csv')" download data-entity-export="csv"><AtlasIcon name="download" :size="15" />CSV</Button>
          <Button as="a" variant="outline" size="sm" :href="source.exportUrl('contact', exportQuery, 'vcard')" download data-entity-export="vcard"><AtlasIcon name="download" :size="15" />vCard</Button>
        </div>
      </div>
      <ErrorState v-if="error && !items.length" :title="t.overviewView.loadFailed" :text="error" :retry-label="t.overviewView.reload" :busy="loading" @retry="lookup.update(query)" />
      <LoadingSkeleton v-else-if="!loaded" :label="t.overviewView.loading" />
      <EmptyState v-else-if="!items.length" icon="users" :title="query.trim() ? t.overviewView.noMatch : t.overviewView.none"
        :text="t.overviewView.noneText" />
      <ul v-if="items.length" class="contact-overview-items atlas-stagger" :aria-label="t.overviewView.contacts" @keydown="rowKeys($event, '[data-entity-row]')">
        <li v-for="item in items" :key="item.identityKey" :data-entity-row="item.identityKey" :data-first="item === first || undefined">
          <ContactCard :contact="item.attributes" />
          <!-- A directory contact's places are the usage service's, by its id (drafts, boards and hidden places included); only a person
               without one keeps the places found by identity. -->
          <UsagePanel v-if="item.contactId" collapsible :object-id="item.contactId" data-entity-usage @open="openPlace" />
          <p v-else class="contact-overview-places">
            <span class="contact-overview-places-label">{{ t.overviewView.occurrences(item.sourceCount) }}:</span>
            <a v-for="place in item.sources" :key="`${place.resourceId}:${place.anchor ?? ''}`" :href="href(place)" :title="place.spaceName ? `${label(place)} · ${place.spaceName}` : label(place)"
              data-entity-place><AtlasIcon :name="place.resourceType === 'atlas.page' ? 'page' : 'users'" :size="13" />{{ label(place) }}<span v-if="place.revisionKind === 'working'" class="contact-overview-draft" :title="draftNote">{{ t.overviewView.draft }}<span class="sr-only">: {{ draftNote }}</span></span></a>
            <Button v-if="item.sourceCount > item.sources.length" variant="link" size="xs" type="button" :disabled="placesBusy === item.identityKey"
              :aria-busy="placesBusy === item.identityKey" data-entity-all-places @click="allPlaces(item)">{{ t.overviewView.allPlaces }}</Button>
          </p>
        </li>
      </ul>
      <footer v-if="items.length" class="contact-overview-footer">
        <p class="help" role="status">{{ t.directory.count(items.length, total) }}</p>
        <Button v-if="total > items.length" variant="outline" type="button" :disabled="loading" :aria-busy="loading" data-entity-more @click="more"><AtlasIcon name="refresh" :size="16" />{{ t.overviewView.more }}</Button>
      </footer>
    </template>
  </section>
</template>

<style scoped>
.contact-overview { display: grid; gap: 14px; min-height: 306px; align-content: start; color: var(--text); }
.contact-overview-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
.contact-overview-toolbar :deep(.search-field) { flex: 1 1 280px; }
.contact-overview-export { display: flex; gap: 6px; }
.contact-overview-items { margin: 0; padding: 0; list-style: none; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); overflow: hidden; }
.contact-overview-items > li { display: grid; gap: 8px; padding: 12px 16px; }
.contact-overview-items > li + li { border-top: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface)); }
/* The person Enter takes, while the search has the focus (P8). */
.contact-overview:has(input[data-entity-search]:focus) .contact-overview-items > li[data-first] { background: var(--state-active-row-bg); box-shadow: var(--state-active-row-mark); }
.contact-overview-places { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 12px; margin: 0; font-size: 13px; color: var(--muted); }
.contact-overview-places-label { font-weight: 600; }
.contact-overview-places a { display: inline-flex; align-items: center; gap: 4px; max-width: 280px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--accent-strong);
  text-decoration: none; transition: color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .contact-overview-places a:hover { text-decoration: underline; } }
.contact-overview-places a:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }
.contact-overview-draft { margin-left: 4px; color: var(--muted); font-size: 12px; }
.contact-overview-footer { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 10px 18px; }
.contact-overview-footer .help { margin: 0; color: var(--muted); font-size: 13px; }
</style>
