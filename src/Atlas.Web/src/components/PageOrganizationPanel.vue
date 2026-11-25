<script setup lang="ts">
import t from "../i18n/components-b";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from "vue";
import { Button } from "./ui/button";
import { ApiError, errorMessage, type Page, type Space } from "../api";
import { organizationApi, type OrganizationChanged, type OrganizationTree, type TrashItem } from "../organization";
import { isPageNode } from "../treeMove";
import AppDialog from "./AppDialog.vue";
import ConfirmDialog from "./settings/ConfirmDialog.vue";
import AtlasIcon from './AtlasIcon.vue';
import PageLocationPicker from './PageLocationPicker.vue';
import ObjectRelations from "./objects/ObjectRelations.vue";
import { Label } from "./ui/label";
import { formatDateTime } from "../formatDate";
import { objectApi } from "../objects/objectApi";
import LoadingNote from "./settings/LoadingNote.vue";
import TrashList from "./settings/TrashList.vue";
import InlineReveal from "./settings/InlineReveal.vue";
import SearchableChoiceList from "./settings/SearchableChoiceList.vue";
import LoadMoreButton from "./LoadMoreButton.vue";

const props = defineProps<{
  spaceId: string;
  spaceRole: Space["role"];
  isWikiAdmin: boolean;
  page?: Page;
  /** A board (module Boards) to place in the tree: only its location; it is renamed, trashed and restored on the board itself. */
  board?: { id: string; title: string };
  spaces?: Space[];
  beforeMutation?: () => boolean | Promise<boolean>;
  /** „Přesunout do koše“ of the page menu: only the confirmation, as narrow as every other one (ConfirmDialog), and its Zrušit closes. */
  startWithTrash?: boolean;
}>();
const emit = defineEmits<{ close: []; changed: [change: OrganizationChanged] }>();
const dialogOpen = ref(true);
const tree = shallowRef<OrganizationTree>();
const trashItems = shallowRef<TrashItem[]>([]);
const trashVersion = ref(0), hasMore = ref(false), headVersion = ref(props.page?.version ?? 0);
const parentId = ref(props.page?.parentId ?? ""), beforeId = ref("");
const includeSubtree = ref(false), restoreSubtree = ref(false), restoreParent = ref("");
const positionOpen = ref(false), transferOpen = ref(false), spaceQuery = ref("");
const spaceOptions = computed(() => writableSpaces.value.map(space => ({ value: space.id, label: space.name })));
const targetSpaceId = ref(""), targetParentId = ref(""), targetSubtree = ref(false), acknowledgeAccess = ref(false);
const targetTree = shallowRef<OrganizationTree>();
const confirmTrash = ref(!!props.startWithTrash && !!props.page);
const body = ref<HTMLElement>();
// Back from the confirmation, the caret returns to the button that opened it (the ConfirmDialog places its own focus).
watch(confirmTrash, async value => {
  if (value) return;
  await nextTick();
  body.value?.querySelector<HTMLElement>('.organization-trash-trigger')?.focus();
});
// Opening {{ t.org.location }} is choosing where: the caret waits in the search, so typing finds the place and Enter takes it.
async function focusLocationSearch() {
  await nextTick();
  body.value?.querySelector<HTMLElement>('#organization-parent .location-search input')?.focus();
}
const mutationsDisabled = computed(() => busy.value || loading.value || conflict.value || !tree.value);
const sourceName = computed(() => props.spaces?.find(space => space.id === props.spaceId)?.name ?? t.org.rootLevel);
const targetLoading = ref(false);
const writableSpaces = computed(() => (props.spaces ?? []).filter(space => space.id !== props.spaceId && (props.isWikiAdmin || space.role !== "Reader")));
let targetRequest = 0;
async function loadTarget() {
  const request = ++targetRequest; targetTree.value = undefined;
  if (!targetSpaceId.value) { targetLoading.value = false; return; }
  targetLoading.value = true;
  try {
    const result = await organizationApi.tree(targetSpaceId.value);
    if (!disposed && request === targetRequest) targetTree.value = result;
  } catch (reason) { if (!disposed && request === targetRequest) error.value = errorMessage(reason); }
  finally { if (!disposed && request === targetRequest) targetLoading.value = false; }
}
watch(targetSpaceId, () => { targetParentId.value = ""; acknowledgeAccess.value = false; void loadTarget(); });
const selectedTrash = shallowRef<TrashItem>();
const loading = ref(false), busy = ref(false), error = ref(""), notice = ref(""), conflict = ref(false);
const canWrite = computed(() => props.isWikiAdmin || props.spaceRole !== "Reader");
const canRestore = computed(() => props.isWikiAdmin || props.spaceRole === "Admin");
// A page is organised by its own right (a Writer grant on the one page needs no space role); a board by the space role, the trash by Admin.
const allowed = computed(() => props.page ? !!props.page.canEdit : props.board ? canWrite.value : canRestore.value);
let disposed = false, generation = 0, context = 0;
/** The tree's pages: a board (module Boards) is a leaf, never a parent, and follows its page into the trash and back. */
const pageItems = computed(() => (tree.value?.items ?? []).filter(isPageNode));
/** Boards under the page and its subpages: they go to the trash with it. */
const boardsBelow = computed(() => (tree.value?.items ?? []).filter(item => item.type === "board" && !!item.parentId && excluded.value.has(item.parentId)).length);
let abort: AbortController | undefined;

const excluded = computed(() => {
  const ids = new Set<string>();
  if (!props.page) return ids;
  ids.add(props.page.id);
  const children = new Map<string, string[]>();
  for (const item of pageItems.value) if (item.parentId) {
    const group = children.get(item.parentId) ?? [];
    group.push(item.id); children.set(item.parentId, group);
  }
  const pending = [props.page.id];
  while (pending.length) for (const child of children.get(pending.pop()!) ?? []) {
    if (!ids.has(child)) { ids.add(child); pending.push(child); }
  }
  return ids;
});
const hasChildren = computed(() => excluded.value.size > 1);
const destinations = computed(() => pageItems.value.filter(item => !excluded.value.has(item.id) && item.canEdit));
const siblings = computed(() => destinations.value.filter(item => item.parentId === (parentId.value || null)));
const positionOptions = computed(() => siblings.value.map(item => ({ ...item, parentId: null })));
watch(parentId, () => { beforeId.value = ""; });
watch(() => props.page?.version, value => { if (value) headVersion.value = Math.max(headVersion.value, value); });

async function load(append = false) {
  if (!allowed.value || busy.value || append && (loading.value || !hasMore.value)) return;
  abort?.abort(); const controller = abort = new AbortController(), current = ++generation;
  loading.value = true; error.value = "";
  try {
    if (append) {
      const result = await organizationApi.trashList(props.spaceId, trashItems.value.length, controller.signal);
      if (disposed || generation !== current) return;
      if (result.version !== trashVersion.value) {
        conflict.value = true; error.value = t.org.trashChanged; return;
      }
      trashItems.value = [...trashItems.value, ...result.items.filter(item => !trashItems.value.some(old => old.id === item.id))];
      hasMore.value = result.hasMore;
    } else {
      const [currentTree, trash] = await Promise.all([
        organizationApi.tree(props.spaceId, controller.signal),
        props.page || props.board ? Promise.resolve(null) : organizationApi.trashList(props.spaceId, 0, controller.signal),
      ]);
      if (disposed || generation !== current) return;
      tree.value = currentTree;
      const currentPage = currentTree.items.find(item => item.id === props.page?.id);
      if (currentPage) headVersion.value = currentPage.version;
      // A board's place comes with the tree (the board's own view does not carry it).
      const currentBoard = props.board && !parentId.value && currentTree.items.find(item => item.id === props.board!.id);
      if (currentBoard) parentId.value = currentBoard.parentId ?? "";
      if (trash) {
        trashItems.value = trash.items; trashVersion.value = trash.version; hasMore.value = trash.hasMore;
        if (selectedTrash.value && !trash.items.some(item => item.id === selectedTrash.value!.id)) selectedTrash.value = undefined;
      }
      if (conflict.value) notice.value = t.org.listCurrent;
      conflict.value = false;
      if (targetSpaceId.value) await loadTarget();
    }
  } catch (reason) {
    if (!controller.signal.aborted && !disposed && current === generation) error.value = errorMessage(reason);
  } finally { if (!disposed && current === generation) loading.value = false; }
}
async function mutate(action: (isCurrent: () => boolean) => Promise<void>) {
  if (!allowed.value || busy.value || loading.value || conflict.value || !tree.value) return;
  busy.value = true; error.value = ""; notice.value = "";
  const current = context;
  try {
    if (props.beforeMutation && !await props.beforeMutation()) return;
    await nextTick();
    if (disposed || current !== context || !allowed.value) return;
    await action(() => !disposed && current === context && allowed.value);
  } catch (reason) {
    if (disposed || current !== context) return;
    conflict.value = reason instanceof ApiError && reason.status === 409 && reason.code !== "subtree_confirmation_required" && reason.code !== "collaboration_unsaved";
    error.value = conflict.value
      ? t.org.structureChanged
      : errorMessage(reason);
  } finally { if (!disposed && current === context) busy.value = false; }
}
async function move() {
  const id = props.page?.id ?? props.board?.id;
  if (!id) return;
  await mutate(async isCurrent => {
    const where = [id, parentId.value || null, beforeId.value || null, tree.value!.version] as const;
    const result = await (props.board ? organizationApi.move(...where, "board") : organizationApi.move(...where));
    if (!isCurrent()) return;
    emit("changed", { kind: "move", affectedIds: result.affectedIds, titleOrderCancelled: result.titleOrderCancelled });
  });
  if (!error.value && !disposed) await load();
}
async function moveSpace() {
  const page = props.page;
  if (!page || !targetTree.value || targetLoading.value || !acknowledgeAccess.value || hasChildren.value && !targetSubtree.value) return;
  await mutate(async isCurrent => {
    const result = await organizationApi.moveSpace(page.id, {
      targetSpaceId: targetSpaceId.value, parentId: targetParentId.value || null, beforePageId: null,
      includeSubtree: targetSubtree.value, expectedSourceTreeVersion: tree.value!.version,
      expectedTargetTreeVersion: targetTree.value!.version, expectedVersion: headVersion.value,
      acknowledgeAccessChange: acknowledgeAccess.value,
    });
    if (!isCurrent()) return;
    emit("changed", { kind: "move-space", affectedIds: result.affectedIds,
      sourceSpaceId: result.sourceSpaceId, targetSpaceId: result.targetSpaceId });
    dialogOpen.value = false;
  });
}
async function trash() {
  const page = props.page;
  if (!page || !confirmTrash.value || hasChildren.value && !includeSubtree.value) return;
  await mutate(async isCurrent => {
    const result = await organizationApi.trash(page.id, includeSubtree.value, tree.value!.version, headVersion.value);
    if (!isCurrent()) return;
    emit("changed", { kind: "trash", affectedIds: result.affectedIds });
    dialogOpen.value = false;
  });
}
/** Obnovit of a trash row asks where first: the restore form opens below and the row stays until it is restored (false). */
async function selectTrash(item: TrashItem) {
  selectedTrash.value = item;
  restoreParent.value = pageItems.value.some(page => page.id === item.parentId) ? item.parentId! : "";
  restoreSubtree.value = false; error.value = ""; notice.value = "";
  // Picking what to restore leads on to where (AD5-5): the caret waits in the restore form's location search.
  void nextTick(() => body.value?.querySelector<HTMLElement>(".organization-restore .location-search input")?.focus());
  return false;
}
const trashDetail = (item: TrashItem) => [item.type === "board" ? t.org.board : t.org.page, t.org.trashedAt(formatDateTime(item.trashedAt)), item.trashedByName].filter(Boolean).join(" · ");
/** A purge takes the subpages trashed with the page along: the trash is read again. */
function purged(item: TrashItem) {
  if (selectedTrash.value?.id === item.id) selectedTrash.value = undefined;
  if (item.type !== "board") void load();
}
/** After a restore the form is gone: the reader goes on from the first item left in the trash (AD5-5), unless they moved on. */
function focusFirstTrashItem() {
  if (props.page || props.board) return;
  const active = document.activeElement;
  if (active && active !== document.body && body.value?.closest("dialog")?.contains(active) && !active.matches(".app-dialog-header button")) return;
  body.value?.querySelector<HTMLElement>("[data-trash-restore]")?.focus();
}
async function untrash() {
  const selected = selectedTrash.value;
  if (!selected || !canRestore.value) return;
  await mutate(async isCurrent => {
    const board = selected.type === "board";
    const result = await organizationApi.untrash(selected.id, restoreParent.value || null, restoreSubtree.value, trashVersion.value, selected.type);
    if (!isCurrent()) return;
    selectedTrash.value = undefined;
    emit("changed", { kind: "untrash", affectedIds: result.affectedIds });
    notice.value = board ? t.org.boardRestored : result.hasSiblingTitleCollision
      ? t.org.restoredCollision
      : t.org.pageRestored;
  });
  if (!error.value && !disposed) { await load(); await nextTick(); focusFirstTrashItem(); }
}
function close() {
  if (busy.value) return;
  if (confirmTrash.value && !props.startWithTrash) { confirmTrash.value = false; return; }
  dialogOpen.value = false;
}
function reset() {
  confirmTrash.value = !!props.startWithTrash && !!props.page;
  context++; generation++; targetRequest++; abort?.abort();
  targetSpaceId.value = ""; targetTree.value = undefined; targetLoading.value = false;
  tree.value = undefined; trashItems.value = []; selectedTrash.value = undefined;
  parentId.value = props.page?.parentId ?? "";
  headVersion.value = props.page?.version ?? 0; beforeId.value = ""; includeSubtree.value = false;
  busy.value = false; loading.value = false; conflict.value = false; error.value = ""; notice.value = "";
  void load();
}
watch([() => props.spaceId, () => props.page?.id, () => props.board?.id, allowed], reset);
onMounted(() => { void load().then(() => { if (props.board) void focusLocationSearch(); }); });
onBeforeUnmount(() => {
  disposed = true; context++; generation++; targetRequest++; abort?.abort();
});
</script>

<template>
  <!-- „Přesunout do koše“ (the page menu or this dialog's own button): the one shared confirmation (over this dialog when opened from its button), as a board's, with the subpages' choice inside it. -->
  <ConfirmDialog v-if="page && (startWithTrash || confirmTrash)" :open="dialogOpen && confirmTrash" :title="t.org.trashPageTitle" :confirm-label="t.org.moveToTrash" confirm-icon="trash"
    :busy="busy" :error="error || (!allowed ? t.org.orgAvailable : undefined)"
    :confirm-disabled="!allowed || loading || conflict || !tree || (hasChildren && !includeSubtree)" @confirm="trash" @cancel="close" @closed="startWithTrash && emit('close')">
    <p><strong>{{ page.title }}</strong> {{ t.org.trashNoteTitled }}<template v-if="boardsBelow">{{ " " + t.org.trashBoards(boardsBelow) }}</template></p>
    <template v-if="hasChildren">
      <Label class="organization-check"><input v-model="includeSubtree" type="checkbox" />{{ t.org.includeSubtree(excluded.size - 1, excluded.size) }}</Label>
      <p class="help">{{ t.org.hasSubpages }}</p>
    </template>
    <LoadingNote :active="loading" :label="t.org.loading" />
    <Button v-if="conflict" variant="outline" class="organization-refresh" :disabled="loading || busy" @click="load()">{{ t.org.reloadList }}</Button>
    <!-- Where it is used (spec space-contents §5.3): links, cards and tree references stay and show it as unavailable. -->
    <ObjectRelations :object-id="page.id" mode="usage" still />
  </ConfirmDialog>
  <AppDialog
    v-if="!(startWithTrash && page)"
    class="organization-dialog"
    :title="page ? t.org.organizePage : board ? t.org.moveBoard : t.org.pageTrash"
    :close-label="page || board ? t.org.close : t.org.closeTrash"
    :open="dialogOpen"
    @close="close" @closed="emit('close')"
  >
    <div ref="body" class="organization-body" :aria-busy="loading || busy">
      <p v-if="!allowed" class="notice error" role="alert">{{ page || board ? t.org.orgAvailable : t.org.trashAdminOnly }}</p>
      <template v-else>
        <div class="organization-status">
          <LoadingNote :active="loading" :label="t.org.loading" />
          <!-- A result or an error unfolds into its place instead of pushing the form down at once. -->
          <InlineReveal :open="!!(error || notice || conflict)">
            <p v-if="error" class="notice error" role="alert">{{ error }}</p>
            <p v-if="notice" class="notice success" role="status">{{ notice }}</p>
            <Button v-if="conflict || error" variant="outline" class="organization-refresh" :disabled="loading || busy" @click="load()">{{ conflict ? t.org.reloadList : t.org.retry }}</Button>
          </InlineReveal>
        </div>
        <div v-if="page || board" class="organization-context"><span class="organization-page-icon"><AtlasIcon :name="board ? 'layout-dashboard' : 'page'" :size="22" /></span><div><strong>{{ (page ?? board)!.title }}</strong><small>{{ sourceName }}</small></div></div>
        <fieldset v-if="page || board" :disabled="mutationsDisabled" class="organization-fields">
          <form class="organization-section" :aria-label="board ? t.org.moveBoard : t.org.movePage" @submit.prevent="move">
            <h3>{{ t.org.locationInSpace }}</h3>
            <PageLocationPicker id="organization-parent" v-model="parentId" :items="pageItems" :excluded="excluded" :root-label="sourceName" :disabled="mutationsDisabled" :label="t.org.underPage" />
            <div class="organization-position">
              <button type="button" class="organization-disclosure" aria-controls="organization-position-reveal" :aria-expanded="positionOpen" @click="positionOpen = !positionOpen">{{ t.org.order(beforeId ? siblings.find(item => item.id === beforeId)?.title ?? '' : undefined) }}</button>
              <InlineReveal id="organization-position-reveal" v-model:open="positionOpen">
                <PageLocationPicker id="organization-before" v-model="beforeId" :items="positionOptions" :disabled="mutationsDisabled" :label="t.org.placeBefore" :root-label="t.org.endOfLevel" />
              </InlineReveal>
            </div>
            <p class="help">{{ board ? t.org.boardMoveHelp : t.org.pageMoveHelp }}</p>
            <Button variant="default" type="submit">{{ board ? t.org.moveBoard : t.org.movePage }}</Button>
          </form>
          <div v-if="page && writableSpaces.length" class="organization-transfer">
          <button type="button" class="organization-disclosure" aria-controls="organization-transfer-reveal" :aria-expanded="transferOpen" @click="transferOpen = !transferOpen">{{ t.org.transferTitle }}</button>
          <InlineReveal id="organization-transfer-reveal" v-model:open="transferOpen">
          <form class="organization-section" :aria-label="t.org.transferTitle" @submit.prevent="moveSpace">
            <p class="help">{{ t.org.transferHelp }}</p>
            <SearchableChoiceList id="organization-target-space" v-model:query="spaceQuery" :model-value="targetSpaceId" :options="spaceOptions" :label="t.org.targetSpace" :hide-label="false" :placeholder="t.org.chooseSpace" :max-height="220" @pick="targetSpaceId = $event" />
            <LoadingNote v-if="targetLoading" :label="t.org.loadingTarget" />
            <div v-if="targetTree" class="organization-target">
              <PageLocationPicker id="organization-target-parent" v-model="targetParentId" :items="targetTree.items.filter(isPageNode)" :disabled="mutationsDisabled || targetLoading" :label="t.org.targetLocation" />
              <p class="help">{{ t.org.transferNote }}</p>
              <Label class="organization-check"><input v-model="targetSubtree" type="checkbox" />{{ t.org.moveSubpages }}</Label>
              <Label class="organization-check"><input id="organization-ack-access" v-model="acknowledgeAccess" type="checkbox" />{{ t.org.ackAccess }}</Label>
              <Button variant="default" type="submit" :disabled="targetLoading || !acknowledgeAccess || hasChildren && !targetSubtree">{{ t.org.moveToSpace }}</Button>
            </div>
          </form>
          </InlineReveal>
          </div>
          <div class="organization-bottom"><Button v-if="page" variant="ghost" type="button" class="organization-trash-trigger" :aria-label="t.org.moveToTrash" @click="confirmTrash = true; includeSubtree = false"><AtlasIcon name="trash" />{{ t.org.moveToTrash }}</Button><Button variant="outline" type="button" @click="close">{{ t.org.done }}</Button></div>
        </fieldset>
        <template v-else>
          <p v-if="trashItems.length || !tree" class="help">{{ t.org.trashHelp }}</p>
          <TrashList v-model:items="trashItems" class="organization-trash-list" :label="t.org.pagesInTrash" :icon="item => item.type === 'board' ? 'layout-dashboard' : 'page'"
            :detail="trashDetail" :restore="selectTrash" :purge="item => objectApi.purge(item.id)" :loaded="!!tree" :loading="loading"
            :empty-title="t.org.trashEmpty" :empty-text="t.org.trashEmptyText" @left="purged">
            <LoadMoreButton v-if="hasMore" class="organization-more" :busy="loading" :disabled="busy" @click="load(true)" />
            <template #purge="{ item }">
              <p v-if="item.type === 'board'">{{ t.org.board }} <strong>{{ item.title }}</strong> {{ t.org.purgeBoard }}</p>
              <p v-else>{{ t.org.page }} <strong>{{ item.title }}</strong> {{ t.org.purgePage }}</p>
            </template>
          </TrashList>
          <form v-if="selectedTrash" class="organization-section organization-restore" @submit.prevent="untrash">
            <h3>{{ t.org.restoreTitle(selectedTrash.title) }}</h3>
            <fieldset class="organization-fields" :disabled="busy || loading || conflict || !tree">
              <PageLocationPicker id="organization-restore-parent" v-model="restoreParent" :items="pageItems" :disabled="mutationsDisabled" :label="t.org.restoreLocation" />
              <Label v-if="selectedTrash.type !== 'board'" class="organization-check"><input v-model="restoreSubtree" type="checkbox" />{{ t.org.restoreSubpages }}</Label>
              <Button variant="default" type="submit">{{ selectedTrash.type === 'board' ? t.org.restoreBoard : t.org.restorePage }}</Button>
            </fieldset>
          </form>
        </template>
      </template>
    </div>
  </AppDialog>
</template>

<style scoped>
.organization-dialog { width: min(760px, calc(100vw - 32px)); max-height: calc(100dvh - 48px); padding: 0; border-radius: var(--radius-lg); box-shadow: var(--shadow-modal); }
/* Only the trash confirmation: the width of every confirmation (ConfirmDialog). */
.organization-dialog::backdrop { background: var(--backdrop); backdrop-filter: blur(4px); }
/* The dialog surface owns the padding; the body only stacks its sections. */
.organization-body { padding: 0; }
.organization-fields { border: 0; padding: 0; margin: 0; min-width: 0; }
/* A section that becomes visible (the other tab, the opened details, the target of a move) fades in; a v-show restarts it. */
.organization-section, .organization-target { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
.organization-section { padding: 22px 0; border-bottom: 1px solid var(--line); }
.organization-section:last-child { border-bottom: 0; padding-bottom: 0; }
.organization-section h3 { margin: 0 0 12px; font-size: 15px; }
.organization-section label { display: block; margin: 10px 0 7px; }
.organization-section .help { font-size: 12px; line-height: 1.6; }
.organization-section .organization-check { display: flex; align-items: flex-start; gap: 9px; font-weight: 400; line-height: 1.5; margin: 14px 0; }
.organization-check input { flex: 0 0 16px; width: 16px; height: 16px; margin: 2px 0 0; accent-color: var(--accent); }
.organization-context { display: flex; align-items: center; gap: 14px; margin: 0 0 22px; }
.organization-context strong { font-size: 17px; overflow-wrap: anywhere; }
.organization-context small { display: block; color: var(--muted); font-size: 12px; margin-top: 4px; }
.organization-page-icon { display: grid; place-items: center; width: 46px; height: 46px; flex-shrink: 0; border-radius: 12px; background: var(--accent-soft); color: var(--accent); }
.organization-position { margin-top: 14px; font-size: 13px; }
.organization-disclosure { display: block; width: 100%; border: 0; background: none; font: inherit; text-align: left; cursor: pointer; color: var(--muted); padding: 10px 0; }
.organization-disclosure[aria-expanded="true"] { color: var(--accent); }
.organization-transfer { padding: 10px 0; border-bottom: 1px solid var(--line); font-size: 13px; }
.organization-transfer .organization-section { padding-top: 0; }
.organization-bottom { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding-top: 20px; }
.organization-trash-trigger { color: var(--error); }
.organization-trash-list { margin: 18px 0; }
</style>
