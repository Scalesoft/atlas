<script setup lang="ts">
import t from "../i18n/components-b";
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { errorMessage } from "../api";
import { channelText } from "../channel";
import tv from "../i18n/verification";
import { pageVerificationApi, verificationIntervals, type PageVerification, type PageVerificationApi, type VerificationChange } from "../verification/pageVerification";
import { verificationShown } from "../verification/verificationMark";
import { formatDate, formatDateTime } from "../formatDate";
import { historyApi, type PageRevisionSummary } from "../history";
import { animateAtlas, motionMs, riseIn, RISE_IN_ROWS } from "../motion/aiMotion";
import { pagePublicationApi, publicationLabels, type PagePublication, type PagePublicationApi, type PublicationMode } from "../publishing/pagePublication";
import { pageConnectionsApi, type PageConnectionsApi } from "../pageConnections";
import { sharingApi, type PageAccess } from "../sharing";
import { peopleCount } from "./sharing/pageAccessText";
import type { BoardBlockHost } from "../boards/editorBlocks";
import type { CommentApi } from "../comments/commentApi";
import type { ViewSessions } from "../live/viewSession";
import { lazyComponent } from "../lazyComponent";
import { skeletonOf } from "./loadingPlaceholders";
import AtlasIcon from "./AtlasIcon.vue";
import PageConnections from "./PageConnections.vue";
import PageOutline from "./PageOutline.vue";
import PageSearch from "./PageSearch.vue";
import { pageSearchQuery } from "../editor/pageSearchState";
import UsagePanel from "./objects/UsagePanel.vue";
import type { UsageApi } from "../objects/relationsApi";
import LoadingNote from "./settings/LoadingNote.vue";
import OptionPills from "./settings/OptionPills.vue";
import { Button } from "./ui/button";

/**
 * The page tools of the dock (refresh 2.5, board 03): the content of the tabs Obsah, Hledat (3.5 J, PageSearch), Historie and
 * Přístup beside the article, and Diskuse: the page's comments, the same CommentThread as a board's Diskuse in its dock (the editor comes with
 * the tab, not with the dock). Asistent is the assistant's own content. Each tab is a summary with one way on to the full surface, so
 * nothing moved here stops existing elsewhere: Historie opens the page history (restore, compare, PDF of a version),
 * Přístup the page's access view (grants, links, opening history).
 */
export type PageSideTab = "outline" | "search" | "history" | "access" | "comments";
const CommentThread = lazyComponent(() => import("./comments/CommentThread.vue"), { loading: skeletonOf({ rows: 1, rowHeight: "41px" }) });
const props = withDefaults(defineProps<{
  tab: PageSideTab;
  pageId: string;
  spaceId: string;
  pageTitle: string;
  /** The page's document section; the outline reads the headings rendered inside it. */
  documentRoot: HTMLElement | null | undefined;
  document?: MarkdownRichValue;
  version: number;
  isPublished: boolean;
  publishedVersion: number | null;
  updatedAt?: string | null;
  relatedEnabled?: boolean;
  refreshKey?: number;
  /** Data sources (the gallery passes local ones). */
  history?: Pick<typeof historyApi, "list">;
  sharing?: Pick<typeof sharingApi, "pageAccess">;
  connections?: PageConnectionsApi;
  usage?: UsageApi;
  publicationApi?: PagePublicationApi;
  /** Ověřený obsah (5.2): the page's verification and the 90-day reader count (both the bundle's), whether the reader edits it, and the source of its changes. */
  verification?: PageVerification | null;
  canEdit?: boolean;
  verificationApi?: PageVerificationApi;
  readers?: number;
  /** The editor services of the comments (`[[`, YouTrack, `@` people) and their data sources. */
  host?: BoardBlockHost;
  commentApi?: CommentApi;
  views?: Pick<ViewSessions, "open">;
}>(), { host: undefined, commentApi: undefined, views: undefined, usage: undefined, publicationApi: () => pagePublicationApi, verification: null, canEdit: false, verificationApi: () => pageVerificationApi, readers: undefined,
  document: undefined, updatedAt: null, relatedEnabled: true, refreshKey: 0, history: () => historyApi, sharing: () => sharingApi, connections: () => pageConnectionsApi });
/** `publication`: the page's publication as read or set here (the header's chip and Publikovat follow its mode). */
const emit = defineEmits<{ openHistory: [revision?: PageRevisionSummary]; openAccess: []; openPlace: [id: string, typeId: string]; publication: [value: PagePublication]; verification: [value: PageVerification]; openReadership: []; openVerification: []; comments: [count: number]; anchor: [anchor: string] }>();

// The draft (koncept) in the top bar's words: what readers read meanwhile.
const state = computed(() => !props.isPublished ? t.side.draftUnpublished : props.version > (props.publishedVersion ?? 0) ? t.side.draftReaders(props.publishedVersion) : t.side.published);

// Historie: the latest revisions; the full history stays one click away.
const revisions = shallowRef<PageRevisionSummary[]>();
const more = shallowRef(false);
const historyError = shallowRef("");
const revisionList = ref<HTMLElement>();
// Přístup: the same state the access view loads.
const access = shallowRef<PageAccess>();
const accessError = shallowRef("");
const root = ref<HTMLElement>();
/** The loading line and the result that replaces it share one box per tab. */
const result = ref<HTMLElement>();
let abort: AbortController | undefined;
let generation = 0;

/**
 * An answer replaces the loading line in place: the box eases from the line's height to the result's, so the way on
 * under it („Otevřít historii“, „Spravovat přístup“) is moved there instead of jumping.
 */
async function arrive(apply: () => void) {
  const box = result.value, before = box?.offsetHeight ?? 0;
  apply();
  await nextTick();
  if (box?.isConnected && before && box.offsetHeight !== before) {
    animateAtlas(box, [{ height: `${before}px`, overflow: "clip" }, { height: `${box.offsetHeight}px`, overflow: "clip" }], { duration: motionMs(box, "--atlas-motion-slow"), fill: "none" });
  }
}

async function load() {
  abort?.abort();
  const current = ++generation;
  if (props.tab === "history" && !revisions.value) {
    const controller = abort = new AbortController();
    historyError.value = "";
    try {
      const page = await props.history.list(props.pageId, null, controller.signal);
      if (current !== generation) return;
      await arrive(() => {
        revisions.value = page.items.slice(0, 6);
        more.value = page.items.length > 6 || page.nextBeforeVersion !== null;
      });
      if (revisionList.value) riseIn(revisionList.value.children, RISE_IN_ROWS);
    } catch (failure) {
      if (current === generation && !controller.signal.aborted) historyError.value = errorMessage(failure);
    }
  } else if (props.tab === "access" && !access.value) {
    accessError.value = "";
    try {
      const state = await props.sharing.pageAccess(props.pageId);
      if (current === generation) await arrive(() => { access.value = state; });
    } catch (failure) {
      if (current === generation) accessError.value = errorMessage(failure);
    }
  }
}
// Publikace: loaded when the panel shows Obsah; a new version refreshes it in place (no skeleton again).
const publication = shallowRef<PagePublication>();
const publicationError = shallowRef("");
/** Živě: readers read the latest save, so the current version is what they see and no older one counts as published. */
const live = computed(() => publication.value?.effective === "Live");
const publicationSaving = shallowRef(false);
/** The page the shown publication is of: another page keeps the last one on screen (same rows, no jump) until its own arrives, unchangeable. */
const publicationOf = shallowRef<string>();
let publicationGeneration = 0;
async function loadPublication() {
  const current = ++publicationGeneration;
  publicationError.value = "";
  try {
    const value = await props.publicationApi.get(props.pageId);
    if (current === publicationGeneration) { publication.value = value; publicationOf.value = props.pageId; emit("publication", value); }
  } catch (failure) {
    if (current === publicationGeneration) publicationError.value = errorMessage(failure);
  }
}
async function setPublication(choice: string) {
  const mode = choice === "inherit" ? null : choice as PublicationMode;
  if (publicationSaving.value || mode === (publication.value?.mode ?? null)) return;
  publicationSaving.value = true;
  publicationError.value = "";
  const current = ++publicationGeneration;
  try {
    const value = await props.publicationApi.set(props.pageId, mode);
    if (current === publicationGeneration) { publication.value = value; publicationOf.value = props.pageId; emit("publication", value); }
  } catch (failure) {
    publicationError.value = errorMessage(failure);
  } finally {
    publicationSaving.value = false;
  }
}
const publicationChoices = computed(() => [
  { value: "inherit", label: t.side.bySpace(publicationLabels[publication.value?.spaceMode ?? "Manual"]) },
  ...(Object.keys(publicationLabels) as PublicationMode[]).map((value) => ({ value, label: publicationLabels[value] })),
]);
const publicationLast = computed(() => {
  const p = publication.value;
  return !p?.publishedAt ? t.side.notPublishedYet : t.side.publishedAt(formatDateTime(p.publishedAt), p.publishedAutomatically ? true : p.publishedBy);
});

// A publication moves only the published version (Naposledy, the history's „publikovaná“): it refreshes them too.
watch(() => [props.pageId, props.version, props.publishedVersion], () => {
  revisions.value = undefined; access.value = undefined; void load();
  if (props.tab === "outline") void loadPublication();
});
// Another page: the whole column is new content and fades in with the article, as a changed tab does.
// The page search starts empty on another page: the old query would answer „Nic nenalezeno“ there.
watch(() => props.pageId, () => { pageSearchQuery.value = ""; if (root.value) animateAtlas(root.value, [{ opacity: 0 }, { opacity: 1 }], { duration: motionMs(root.value, "--atlas-motion-slow") }); }, { flush: "post" });
watch(() => props.tab, () => { void load(); if (props.tab === "outline" && publicationOf.value !== props.pageId) void loadPublication(); }, { immediate: true });
onBeforeUnmount(() => { abort?.abort(); generation++; publicationGeneration++; });

// Ověřování (5.2 §3.2, §5.1): the facts and the choice show while the space or the page has verification on; the 90-day reader count loads after the shell.
const verifying = computed(() => !!props.verification && (props.verification.state !== "off" || props.verification.spaceIntervalMonths !== null));
const verificationSaving = shallowRef(false);
const verificationError = shallowRef("");
const intervalChoices = computed(() => [
  { value: "inherit", label: tv.bySpace(props.verification?.spaceIntervalMonths ?? null) },
  ...verificationIntervals.map((months) => ({ value: String(months), label: tv.months(months) })),
  { value: "0", label: tv.never },
]);
async function setIntervalChoice(choice: string) {
  const intervalMonths = choice === "inherit" ? null : Number(choice) as NonNullable<VerificationChange["intervalMonths"]>;
  if (verificationSaving.value || intervalMonths === (props.verification?.intervalMonths ?? null)) return;
  verificationSaving.value = true;
  verificationError.value = "";
  try {
    emit("verification", await props.verificationApi.change(props.pageId, intervalMonths === null ? { inheritInterval: true } : { intervalMonths }));
  } catch (failure) {
    verificationError.value = errorMessage(failure);
  } finally {
    verificationSaving.value = false;
  }
}

const viewers = computed(() => { const counts = access.value?.counts; return counts ? counts.manage + counts.edit + counts.read : 0; });
const ownAccess = computed(() => {
  const me = access.value?.myAccess;
  return !me ? "" : me.canEdit ? t.side.canEdit : me.canReadDraft ? t.side.canReadDraft : t.side.canReadPublished;
});
</script>

<template>
  <div ref="root" class="page-side" :data-page-side="tab">
    <template v-if="tab === 'outline'">
      <PageOutline :root="documentRoot" variant="list" :page-id="pageId" :document="document" />
      <PageConnections layout="list" :page-id="pageId" :space-id="spaceId" :page-title="pageTitle" :document="document" :refresh-key="refreshKey" :related-enabled="relatedEnabled" :api="connections" />
      <dl class="page-side-facts" :aria-label="t.side.aboutPage">
        <dt>{{ t.side.version }}</dt><dd>{{ version }} · {{ state }}</dd>
        <template v-if="updatedAt"><dt>{{ t.side.edited }}</dt><dd><time :datetime="updatedAt">{{ formatDate(updatedAt) }}</time></dd></template>
        <template v-if="verificationShown(verification)">
          <dt>{{ tv.steward }}</dt>
          <dd data-verification-steward>
            <Button v-if="canEdit" variant="link" size="xs" type="button" class="h-auto p-0" data-verification-steward-open @click="emit('openVerification')">{{ verification.stewardName ?? tv.setForEditor }}</Button>
            <template v-else>{{ verification.stewardName ?? tv.noSteward }}</template>
          </dd>
            <dt>{{ tv.verifiedRow }}</dt>
            <dd data-verification-row>{{ tv.verifiedRowText(verification.verifiedAt ? formatDate(verification.verifiedAt) : null, verification.verifiedUntil ? formatDate(verification.verifiedUntil) : null) }}<template v-if="verification.changedSinceVerification"><br><span class="page-side-warn">{{ tv.changedVersions(verification.verifiedVersion, publishedVersion ?? version) }}</span></template></dd>
          <template v-if="readers !== undefined">
            <dt>{{ tv.readership }}</dt>
            <dd><Button variant="link" size="xs" type="button" class="h-auto p-0" :aria-label="tv.readershipOpen" data-verification-readers @click="emit('openReadership')">{{ tv.readershipText(readers) }}</Button></dd>
          </template>
        </template>
      </dl>
      <!-- „Použito na“: the count at once, the places on request (they read the blocks that use the page). -->
      <UsagePanel :key="pageId" collapsible :object-id="pageId" :api="usage" @open="(id, typeId) => emit('openPlace', id, typeId)" />
      <!-- Last in the column: its answer arriving grows the column at its end and moves nothing above (F2, dock CLS). The mode is said
           once: by the choice for who may change it, by the fact for who may not; „Verze“ above already says unpublished changes. -->
      <section class="page-side-publication" :aria-label="t.side.publicationOfPage" data-page-publication>
        <p v-if="publicationError" class="notice error" role="alert">{{ publicationError }}</p>
        <div v-if="!publication && !publicationError" class="page-side-publication-skeleton"><LoadingNote :label="t.side.loadingPublication" /></div>
        <template v-else-if="publication">
          <dl class="page-side-facts">
            <template v-if="!publication.canChange"><dt>{{ t.side.publication }}</dt><dd data-publication-effective>{{ publicationLabels[publication.effective] }}<template v-if="publication.mode === null">{{ " " + t.side.bySpaceNote }}</template></dd></template>
            <dt>{{ t.side.lastPublished }}</dt><dd data-publication-last>{{ publicationLast }}</dd>
          </dl>
          <OptionPills v-if="publication.canChange" :model-value="publication.mode ?? 'inherit'" :label="t.side.publicationOfPage" :options="publicationChoices" :disabled="publicationSaving || publicationOf !== pageId" data-publication-choice @update:model-value="setPublication" />
        </template>
      </section>
      <!-- Ověřování under Publikace, the same pills: the page's own interval, by the space, or never. Editors only; the others read the facts above. -->
      <section v-if="verifying && canEdit && verification" class="page-side-publication" :aria-label="tv.interval" data-page-verification-interval>
        <OptionPills :model-value="verification.intervalMonths === null ? 'inherit' : String(verification.intervalMonths)" :label="tv.interval" :options="intervalChoices" :disabled="verificationSaving" @update:model-value="setIntervalChoice" />
        <p v-if="verificationError" class="notice error" role="alert">{{ verificationError }}</p>
      </section>
    </template>

    <PageSearch v-else-if="tab === 'search'" :root="documentRoot" />

    <section v-else-if="tab === 'history'" class="page-side-section" :aria-label="t.side.lastVersions">
      <div ref="result" class="page-side-result">
      <p v-if="historyError" class="notice error" role="alert">{{ historyError }}</p>
      <LoadingNote v-else-if="!revisions" :label="t.side.loadingHistory" />
      <ol v-else ref="revisionList" class="page-side-revisions">
        <li v-for="item in revisions" :key="item.id">
          <button type="button" class="page-side-revision" @click="emit('openHistory', item)">
            <span class="page-side-revision-head">
              <strong>{{ t.side.versionN(item.version) }}</strong>
              <span v-if="item.isCurrent" class="page-side-chip">{{ live ? t.side.currentReaders : t.side.current }}</span>
              <span v-else-if="item.isPublished && !live" class="page-side-chip" data-tone="success">{{ t.side.published }}</span>
            </span>
            <small><time :datetime="item.activeFrom">{{ formatDateTime(item.activeFrom) }}</time> · {{ item.authorName }}<template v-if="item.channel && item.channel !== 'web' && channelText(item.channel)"> · {{ channelText(item.channel) }}</template></small>
            <span v-if="item.changeSummary" class="page-side-summary">{{ item.changeSummary }}</span>
          </button>
        </li>
      </ol>
      </div>
      <Button variant="outline" size="sm" class="page-side-action" data-open-history @click="emit('openHistory')"><AtlasIcon name="history" :size="15" />{{ more ? t.side.fullHistory : t.side.openHistory }}</Button>
    </section>

    <!-- The same thread as the page's section under its content and a board's dock: one object, one view session; a selection of the page is quoted (Alt C). -->
    <CommentThread v-else-if="tab === 'comments'" :key="pageId" :object-id="pageId" :view-key="`page:${pageId}`" :host="host" :api="commentApi" :views="views" quotes :lazy="false"
      @count="emit('comments', $event)" @anchor="emit('anchor', $event)" />

    <section v-else class="page-side-section" :aria-label="t.side.accessToPage">
      <div ref="result" class="page-side-result">
      <p v-if="accessError" class="notice error" role="alert">{{ accessError }}</p>
      <LoadingNote v-else-if="!access" :label="t.side.loadingAccess" />
      <dl v-else-if="access.canManage" class="page-side-facts">
        <dt>{{ t.side.mode }}</dt><dd>{{ access.mode === 'custom' ? t.side.customAccess : t.side.wholeSpace(access.spaceName) }}</dd>
        <template v-if="access.counts"><dt>{{ t.side.canSee }}</dt><dd>{{ peopleCount(viewers) }}</dd>
          <dt>{{ t.side.role }}</dt><dd>{{ t.side.roles(access.counts.manage, access.counts.edit, access.counts.read) }}</dd></template>
        <template v-if="access.groups?.length"><dt>{{ t.side.groups }}</dt><dd>{{ access.groups.map(group => group.name).join(', ') }}</dd></template>
        <dt>{{ t.side.links }}</dt><dd>{{ access.shareLinks?.active ? t.side.activeLinks(access.shareLinks.active) : t.side.noAnonymousLinks }}</dd>
      </dl>
      <p v-else class="page-side-note"><AtlasIcon name="circle-check" :size="15" />{{ ownAccess }} {{ t.side.othersNote }}</p>
      </div>
      <Button variant="outline" size="sm" class="page-side-action" data-open-access @click="emit('openAccess')"><AtlasIcon name="users" :size="15" />{{ access?.canManage ? t.side.manageAccess : t.side.showMyAccess }}</Button>
    </section>
  </div>
</template>

<style scoped>
.page-side { display: flex; flex-direction: column; gap: 20px; padding: 16px; min-width: 0; }
.page-side-section { display: flex; flex-direction: column; gap: 12px; min-width: 0; }
.page-side-result { min-width: 0; }
.page-side-facts { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 6px 12px; margin: 0; padding: 12px; border: 1px solid color-mix(in srgb, var(--line) 80%, transparent); border-radius: 10px; background: var(--surface); font-size: 13px; }
.page-side-facts dt { color: var(--muted); }
.page-side-facts dd { margin: 0; min-width: 0; overflow-wrap: anywhere; }
.page-side-note { display: flex; align-items: flex-start; gap: 8px; margin: 0; color: var(--muted); font-size: 13px; line-height: 1.5; }
.page-side-note > svg { flex: none; margin-top: 2px; color: var(--success, var(--accent)); }
.page-side-revisions { display: flex; flex-direction: column; gap: 6px; margin: 0; padding: 0; list-style: none; }
.page-side-revision { display: flex; flex-direction: column; gap: 2px; width: 100%; padding: 9px 12px; border: 1px solid color-mix(in srgb, var(--line) 80%, transparent); border-radius: 10px; background: var(--surface); color: var(--text); font: inherit; text-align: left; cursor: pointer; transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .page-side-revision:hover { border-color: var(--line); background: color-mix(in srgb, var(--hover) 50%, var(--surface)); } }
.page-side-revision:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.page-side-revision-head { display: flex; align-items: center; gap: 8px; }
.page-side-revision small { color: var(--muted); font-size: 12px; }
.page-side-summary { color: var(--text); font-size: 13px; overflow-wrap: anywhere; }
.page-side-chip { padding: 0 6px; border-radius: 6px; background: var(--hover); color: var(--muted); font-size: 11.5px; font-weight: 600; }
.page-side-chip[data-tone="success"] { background: var(--success-bg, var(--hover)); color: var(--success, var(--muted)); }
.page-side-action { align-self: flex-start; }
.page-side-publication { display: flex; flex-direction: column; gap: 10px; min-width: 0; }
.page-side-warn { color: var(--warning); }
.page-side-publication-skeleton { min-height: 84px; }
</style>
