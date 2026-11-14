<script setup lang="ts">
import t from "../../../i18n/boards";
import { computed, inject, ref } from "vue";
import { useToast } from "../../../composables/useToast";
import { organizationApi } from "../../../organization";
import { commentCountsKey } from "../../../comments/boardComments";
import { readPageTarget, type BoardCardItem } from "../../../boards/boardTargets";
import { boardLink, isPlainNavigationClick, pageLink } from "../../../pageLinks";
import { typeInfo } from "../../../objects/relationsApi";
import AtlasIcon from "../../AtlasIcon.vue";
import CardSwap from "./CardSwap.vue";
import "./boardCard.css";

/**
 * The card of a page on a board: icon, title, space, the first lines of the text and „Otevřít“. A page the reader may not see is
 * a locked placeholder without a title (the server sends none). Drawn in a canvas node, so the link carries `nodrag nopan`
 * (Vue Flow) and nothing here asks the network: the board's session resolves the target and passes it in `item.target`, or here.
 * `target` undefined means „still resolving“: a placeholder after `--atlas-motion-defer`, then a cross-fade to the content (CardSwap).
 * `open` is a plain click on „Otevřít“; a modified click opens the link as usual. The page's live comments are a quiet count in
 * the head, from the board's counts (BoardView provides them; none elsewhere). It has no „Upravit“ (3.6): who may edit the page edits
 * it by Enter, F2 or a double-click on the selected card, in the page's editor over the board (BoardCanvas, BoardPageEditor, COL2-9).
 */
const props = defineProps<{ item: BoardCardItem; target?: unknown }>();
const emit = defineEmits<{ open: [resourceId: string] }>();

const view = computed(() => readPageTarget(props.target !== undefined ? props.target : props.item.target, props.item.targetResourceId));
const state = computed(() => (!view.value ? "pending" : view.value.available ? "ready" : "locked"));
// A board and a contact come from the relations graph; a contact has no address of its own (it opens in the dock).
const href = computed(() => (!view.value || view.value.typeId === "atlas.contact" ? undefined : view.value.typeId === "atlas.board" ? boardLink(view.value.resourceId) : pageLink(view.value.resourceId)));
const icon = computed(() => typeInfo(view.value?.typeId ?? "atlas.page").icon);
const commentCount = inject(commentCountsKey, () => 0);
const comments = computed(() => (view.value?.available ? commentCount(view.value.resourceId) : 0));
const toaster = useToast();
const restoring = ref(false);
/** Back from the trash to its old parent, else to the space's root (the Admin's restore, as the space trash does it); the card follows the page. */
async function restore() {
  const page = view.value;
  if (!page?.spaceId || restoring.value) return;
  restoring.value = true;
  try {
    let trash = await organizationApi.trashList(page.spaceId), item = trash.items.find(entry => entry.id === page.resourceId);
    for (let offset = trash.items.length; !item && trash.hasMore; offset += trash.items.length) {
      trash = await organizationApi.trashList(page.spaceId, offset);
      item = trash.items.find(entry => entry.id === page.resourceId);
    }
    if (!item) throw new Error("not in the trash");
    await organizationApi.untrash(item.id, item.parentId, false, trash.version).catch(() => organizationApi.untrash(item.id, null, false, trash.version));
    toaster.success(t.pageCard.restored);
  } catch { toaster.error(t.pageCard.restoreFailed); } finally { restoring.value = false; }
}
function open(event: MouseEvent) {
  if (!view.value || !isPlainNavigationClick(event)) return;
  event.preventDefault();
  emit("open", view.value.resourceId);
}
</script>

<template>
  <CardSwap :state="state">
    <template #pending>
      <article class="bcard" data-board-card="page">
        <header class="bcard-head">
          <span class="bcard-icon" aria-hidden="true"><AtlasIcon :name="icon" :size="18" /></span>
          <span class="bcard-heading"><span class="bcard-bar" aria-hidden="true" /><span class="bcard-sr">{{ t.pageCard.loading }}</span></span>
        </header>
        <span class="bcard-bar bcard-bar-wide" aria-hidden="true" />
      </article>
    </template>
    <template #locked>
      <!-- A page in the trash is not one the reader may not see: it says so, and who may restore it can (the space's Admin). -->
      <article v-if="view?.trashed" class="bcard bcard-locked" data-board-card="page" data-unavailable data-board-trashed>
        <span class="bcard-icon" data-tone="neutral" aria-hidden="true"><AtlasIcon name="trash" :size="18" /></span>
        <span class="bcard-heading"><strong class="bcard-title">{{ t.pageCard.inTrash }}</strong></span>
        <button v-if="view.canRestore" type="button" class="bcard-link nodrag nopan" :disabled="restoring" data-board-restore-page @click.stop="restore">{{ t.pageCard.restore }}<AtlasIcon name="undo-2" :size="14" /></button>
      </article>
      <article v-else class="bcard bcard-locked" data-board-card="page" data-unavailable>
        <span class="bcard-icon" data-tone="neutral" aria-hidden="true"><AtlasIcon name="lock" :size="18" /></span>
        <span class="bcard-heading"><strong class="bcard-title">{{ t.pageCard.unavailable }}</strong></span>
      </article>
    </template>
    <article v-if="view?.available" class="bcard" data-board-card="page">
      <header class="bcard-head">
        <span class="bcard-icon" aria-hidden="true"><AtlasIcon :name="icon" :size="18" /></span>
        <span class="bcard-heading">
          <strong class="bcard-title" :title="view.title ?? undefined">{{ view.title ?? t.pageCard.untitled }}</strong>
          <small v-if="view.spaceTitle" class="bcard-muted" :title="view.spaceTitle">{{ view.spaceTitle }}</small>
        </span>
        <span v-if="comments" class="bcard-comments" data-board-card-comments><AtlasIcon name="quote" :size="12" /><span class="bcard-sr">{{ t.pageCard.comments }}</span>{{ comments }}</span>
      </header>
      <p v-if="view.excerpt" class="bcard-fade" data-board-excerpt>{{ view.excerpt }}</p>
      <span v-else class="bcard-fade" aria-hidden="true" />
      <span class="bcard-actions">
        <a class="bcard-link nodrag nopan" :href="href" :role="href ? undefined : 'button'" :tabindex="href ? undefined : 0" data-board-open-page @click="open" @keydown.enter="!href && emit('open', view.resourceId)" @keydown.space="!href && ($event.preventDefault(), emit('open', view.resourceId))">{{ t.pageCard.open }}<AtlasIcon name="external" :size="14" /></a>
      </span>
    </article>
  </CardSwap>
</template>

<style scoped>
.bcard-actions { display: flex; gap: 12px; }
.bcard-locked button.bcard-link { padding: 0; border: 0; background: none; font: inherit; font-size: 13px; font-weight: 600; }
.bcard-locked button.bcard-link { margin-left: auto; }
.bcard-comments { display: inline-flex; flex: none; align-items: center; gap: 3px; margin-left: auto; color: var(--muted); font-size: 12px; font-variant-numeric: tabular-nums; }
</style>
