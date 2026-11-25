<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from "vue";
import { ApiError, errorMessage } from "../../api";
import { useToast } from "../../composables/useToast";
import { rowKeys } from "../../keyboard/navigation";
import { unlinkedApi, type UnlinkedApi, type UnlinkedChoice, type UnlinkedMention } from "../../linking/unlinkedApi";
import ActionMenu, { type ActionMenuItem } from "../ActionMenu.vue";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import RowActionsMenu from "../RowActionsMenu.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import { Button } from "../ui/button";
import t from "../../i18n/components-objects";

/**
 * Nepropojené zmínky of a space (P15 „viditelné mezery“): the names, page titles and issue keys its pages mention without a link,
 * grouped by page, each in its sentence with the best candidate first („nejspíš X · nebo Y“, as the unresolved chip says). One
 * confirmation per row: Enter or „Propojit“ links the first candidate, „Jiný…“ picks another (the shared action menu, first row lit),
 * „Nechat textem“ leaves it text; all of them also from the row's „Co s tím jde dělat“ (RowActionsMenu). Only the page says whether
 * something is linked (owner decision): an unresolved chip left as text becomes its text in the page, a plain-text mention left as text
 * changes nothing and only leaves this view, offered again next time. The row goes at once and the keyboard moves to the next one;
 * a 409 (the page changed meanwhile) brings that page's rows back as they are now and says so. Why not ContactDuplicates: that one pairs directory records; this one confirms a place in a page.
 * `api` is replaced by tests and the gallery.
 */
const props = defineProps<{ spaceId: string; api?: UnlinkedApi }>();
const emit = defineEmits<{ open: [pageId: string]; counted: [count: number, more: boolean] }>();
const source = computed(() => props.api ?? unlinkedApi);
const toaster = useToast();

const items = ref<UnlinkedMention[]>([]);
const cursor = ref<string | null>(null);
const loading = ref(true), more = ref(false), error = ref("");
const root = ref<HTMLElement>();
const keyOf = (row: UnlinkedMention) => `${row.pageId}:${row.state}:${row.ordinal}:${row.text}`;
const counted = () => emit("counted", items.value.length, !!cursor.value);

async function load(next = false) {
  if (next) more.value = true; else { loading.value = true; error.value = ""; }
  try {
    const page = await source.value.list(props.spaceId, { cursor: next ? cursor.value : null });
    const known = new Set(items.value.map(keyOf));
    items.value = next ? [...items.value, ...page.items.filter(row => !known.has(keyOf(row)))] : page.items;
    cursor.value = page.nextCursor;
    counted();
  } catch (failure) {
    if (next) toaster.error(errorMessage(failure)); else error.value = errorMessage(failure);
  } finally { loading.value = false; more.value = false; }
}
onMounted(() => load());

const groups = computed(() => {
  const pages = new Map<string, { pageId: string; pageTitle: string; rows: UnlinkedMention[] }>();
  for (const row of items.value) {
    let group = pages.get(row.pageId);
    if (!group) pages.set(row.pageId, group = { pageId: row.pageId, pageTitle: row.pageTitle, rows: [] });
    group.rows.push(row);
  }
  return [...pages.values()];
});

const kinds = { person: { icon: "user", label: t.unlinked.person }, page: { icon: "file-text", label: t.unlinked.page }, issue: { icon: "ticket", label: t.unlinked.issue } } as const;
/** The sentence around the mention, the mention itself marked; the text alone when the server sent no sentence holding it. */
function inContext(row: UnlinkedMention) {
  const at = row.context.indexOf(row.text);
  return at < 0 ? { before: "", text: row.text, after: "" } : { before: row.context.slice(0, at), text: row.text, after: row.context.slice(at + row.text.length) };
}
const candidateItems = (row: UnlinkedMention): ActionMenuItem[] =>
  row.candidates.map(candidate => ({ id: `link:${candidate.id}`, label: candidate.hint ? `${candidate.label} · ${candidate.hint}` : candidate.label, icon: kinds[row.kind].icon }));
const actionsOf = (row: UnlinkedMention): ActionMenuItem[] => [...candidateItems(row).map(item => ({ ...item, label: t.unlinked.linkTo(item.label), group: "link" })),
  { id: "text", label: t.unlinked.keepText, icon: "type", group: "leave" }];

const choiceOf = (id: string): UnlinkedChoice | null => (id.startsWith("link:") ? { targetId: id.slice(5) } : id === "text" ? id : null);
// A decided row stays in the DOM while it fades out (TransitionGroup); its button must not take the keyboard.
const primaries = () => [...(root.value?.querySelectorAll<HTMLElement>("[data-unlinked-row]:not(.unlinked-row-leave-active) [data-unlinked-primary]") ?? [])];
const said = (row: UnlinkedMention, choice: UnlinkedChoice) => typeof choice === "object"
  ? t.unlinked.linked(row.text, row.candidates.find(candidate => candidate.id === choice.targetId)?.label ?? row.text)
  : t.unlinked.staysText(row.text);

/** One confirmation: the row goes at once, the next one takes the keyboard; the server's 409 brings the page's rows back as they are. */
async function decide(row: UnlinkedMention, id: string) {
  const choice = choiceOf(id);
  // Rows are listed page by page in the order of `items`, one first button each: the index is the row's place on screen too.
  const at = items.value.indexOf(row);
  if (!choice || at < 0) return;
  items.value = items.value.filter(other => other !== row);
  counted();
  await nextTick();
  const next = primaries();
  (next[Math.min(at, next.length - 1)] ?? root.value?.querySelector<HTMLElement>("[data-unlinked-more]") ?? root.value)?.focus();
  // Plain text kept as text: nothing in the page changes, so nothing is stored; the row only leaves this view.
  if (choice === "text" && row.state === "suggested") return void toaster.success(said(row, choice));
  try {
    const { version } = await source.value.resolve(row, choice);
    // The page's other rows were read at the old version; this one is now the page's.
    items.value = items.value.map(other => (other.pageId === row.pageId ? { ...other, version } : other));
    const undo = source.value.undo;
    toaster.success(said(row, choice), undo ? { undo: () => void undo(row, choice, version).then(() => reloadPage(row), failure => toaster.error(errorMessage(failure))) } : undefined);
  } catch (failure) {
    if (failure instanceof ApiError && failure.status === 409) {
      await reloadPage(row, at);
      toaster.warning(t.unlinked.pageChanged(row.pageTitle));
    } else {
      // Back where it was, untouched.
      items.value.splice(Math.min(at, items.value.length), 0, row);
      counted();
      toaster.error(errorMessage(failure));
    }
  }
}
/** The rows of one page as the server has them now, in the place of the page's group (at `at` when it has no row left). */
async function reloadPage(row: UnlinkedMention, at = items.value.length) {
  try {
    // Only the page's rows, also from a server that ignores the filter.
    const fresh = (await source.value.list(props.spaceId, { pageId: row.pageId })).items.filter(other => other.pageId === row.pageId);
    const first = items.value.findIndex(other => other.pageId === row.pageId);
    const rest = items.value.filter(other => other.pageId !== row.pageId);
    const place = first < 0 ? Math.min(at, rest.length) : first;
    items.value = [...rest.slice(0, place), ...fresh, ...rest.slice(place)];
    counted();
  } catch (failure) { toaster.error(errorMessage(failure)); }
}
</script>

<template>
  <section ref="root" class="unlinked" aria-labelledby="unlinked-title" :aria-busy="loading" tabindex="-1" @keydown="rowKeys($event, '[data-unlinked-row]:not(.unlinked-row-leave-active)')">
    <header class="unlinked-header">
      <h3 id="unlinked-title">{{ t.unlinked.title }}</h3>
      <p>{{ t.unlinked.intro }}</p>
    </header>
    <ErrorState v-if="error" :title="t.unlinked.loadFailed" :text="error" :retry-label="t.unlinked.reload" :busy="loading" @retry="load()" />
    <LoadingSkeleton v-else-if="loading && !items.length" :rows="4" row-height="64px" :label="t.unlinked.loading" />
    <EmptyState v-else-if="!items.length" icon="link" :title="t.unlinked.allLinked" :text="t.unlinked.allLinkedText" />
    <section v-for="group in groups" :key="group.pageId" class="unlinked-group" :aria-label="group.pageTitle">
      <h4><button type="button" class="unlinked-page" data-unlinked-page @click="emit('open', group.pageId)"><AtlasIcon name="file-text" :size="15" />{{ group.pageTitle }}</button></h4>
      <TransitionGroup tag="ul" name="unlinked-row" class="unlinked-items" :aria-label="t.unlinked.mentionsOn(group.pageTitle)">
        <li v-for="(row, index) in group.rows" :key="keyOf(row)" :style="{ '--i': Math.min(index, 8) }" :data-unlinked-row="keyOf(row)">
          <span class="unlinked-kind" :title="kinds[row.kind].label"><AtlasIcon :name="kinds[row.kind].icon" :size="16" /><span class="visually-hidden">{{ kinds[row.kind].label }}</span></span>
          <span class="unlinked-copy">
            <span class="unlinked-context">{{ inContext(row).before }}<mark>{{ inContext(row).text }}</mark>{{ inContext(row).after }}</span>
            <small v-if="row.candidates.length" class="unlinked-candidates">
              <span class="unlinked-likely">{{ t.unlinked.likely }} {{ row.candidates[0]!.label }}<template v-if="row.candidates[0]!.hint"> ({{ row.candidates[0]!.hint }})</template></span>
              <template v-for="candidate in row.candidates.slice(1, 3)" :key="candidate.id">{{ " " + t.unlinked.orCandidate(candidate.label) }}</template>
              <template v-if="row.candidates.length > 3">{{ " " + t.unlinked.andMore(row.candidates.length - 3) }}</template>
            </small>
          </span>
          <span class="unlinked-actions">
            <Button v-if="row.candidates.length" variant="outline" size="sm" type="button" data-unlinked-primary data-unlinked-link @click="decide(row, `link:${row.candidates[0]!.id}`)">
              <AtlasIcon name="link" :size="15" />{{ t.unlinked.link }}
            </Button>
            <ActionMenu v-if="row.candidates.length > 1" :items="candidateItems(row)" :label="t.unlinked.linkWith(row.text)" item-attr="data-unlinked-candidate" side="bottom" align="end" @select="decide(row, $event)">
              <Button variant="ghost" size="sm" type="button" data-unlinked-other>{{ t.unlinked.other }}</Button>
            </ActionMenu>
            <Button variant="ghost" size="sm" type="button" :data-unlinked-primary="row.candidates.length ? undefined : ''" data-unlinked-text @click="decide(row, 'text')">{{ t.unlinked.keepText }}</Button>
          </span>
          <RowActionsMenu :items="actionsOf(row)" :label="t.unlinked.actions(row.text)" @select="decide(row, $event)" />
        </li>
      </TransitionGroup>
    </section>
    <Button v-if="cursor && !error" variant="outline" type="button" class="unlinked-more" :disabled="more" :aria-busy="more" data-unlinked-more @click="load(true)">{{ t.unlinked.loadMore }}</Button>
  </section>
</template>

<style scoped>
.unlinked { display: grid; gap: 14px; align-content: start; outline: none; }
.unlinked-header { display: grid; gap: 4px; }
.unlinked-header h3 { margin: 0; font-size: 16px; }
.unlinked-header p { margin: 0; color: var(--muted); font-size: 12.5px; }
.unlinked-group { display: grid; gap: 6px; }
.unlinked-group h4 { margin: 0; font-size: 14px; }
.unlinked-page { display: inline-flex; align-items: center; gap: 6px; min-height: 44px; padding: 0; border: 0; background: none; color: var(--text); font: inherit; font-weight: 600; cursor: pointer; }
.unlinked-page:hover { color: var(--accent); }
.unlinked-page:focus-visible { outline: var(--focus-ring); outline-offset: 2px; border-radius: var(--radius-sm, 6px); }
.unlinked-items { position: relative; margin: 0; padding: 0; list-style: none; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); overflow: hidden; }
.unlinked-items > li { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 12px; padding: 10px 12px 10px 14px; }
.unlinked-items > li + li { border-top: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface)); }
.unlinked-kind { display: grid; place-items: center; flex: 0 0 28px; height: 28px; border: 1px solid var(--line); border-radius: 7px; color: var(--text-dim, var(--muted)); }
.unlinked-copy { display: grid; flex: 1 1 200px; gap: 2px; min-width: 0; }
.unlinked-context { overflow-wrap: anywhere; }
.unlinked-context mark { background: var(--accent-soft); color: var(--accent); border-radius: 3px; font-weight: 650; padding: 0 1px; margin: 0 -1px; }
.unlinked-candidates { color: var(--muted); font-size: 12.5px; }
.unlinked-likely { color: var(--text); font-weight: 600; }
.unlinked-actions { display: flex; flex-wrap: wrap; gap: 6px; margin-left: auto; }
.unlinked-more { justify-self: center; }
/* D5: every button a 44 px target on touch, the look unchanged. */
@media (pointer: coarse) { .unlinked-actions :deep(button) { min-height: 44px; } }
.unlinked-row-enter-active { animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards; animation-delay: calc(var(--i, 0) * var(--st-stagger)); }
.unlinked-row-leave-active { position: absolute; animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-in) both; pointer-events: none; }
.unlinked-row-move { transition: transform var(--atlas-motion-base) var(--atlas-ease-inout); }
</style>
