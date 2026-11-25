<script setup lang="ts">
import { computed, inject, nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from "vue";
import { ApiError } from "../../api";
import {
  CONFLICT_STATE, decideProposal, PROPOSAL_DECIDE, PROPOSAL_FINDINGS, decisionErrorText, getProposal, itemFailureText, summaryText,
  type Proposal, type ProposalItem,
} from "../../assistant/proposals";
import t from "../../i18n/assistant-panel";
import type { PageRevision } from "../../history";
import { findingsFor } from "../../mcpPublish";
import type { PublicationCheck } from "../../publishing/publication";
import { linkFindings } from "../../publishing/publicationChecks";
import { tagKey, tagTone } from "../../pageTags";
import AtlasIcon from "../AtlasIcon.vue";
import HistoryChanges from "../HistoryChanges.vue";
import PageIcon from "../PageIcon.vue";
import PublishFindings from "../publishing/PublishFindings.vue";
import ImpactPreview from "../settings/ImpactPreview.vue";
import ErrorState from "../settings/ErrorState.vue";
import ResultBanner from "../settings/ResultBanner.vue";
import { Button } from "../ui/button";
import { Checkbox } from "../ui/checkbox";
import "../../page-tags.css";

/**
 * The card of a write the agent proposes in the conversation (spec §9.2, D4): every proposed change of a page as one compact row
 * (icon and tags before → after; for a draft write (WP-31) the changed sections as the history's word diff, a rename old → new,
 * a new draft its place and first lines, and „Otevřít v editoru“ once it is applied; a move (WP-40) its path before → after with the
 * subpages that move along; a publication (WP-41) the versions, changed sections and change summary with the check before publication
 * as the rows of PublishFindings), a checkbox per row, „Vybrat vše / Zrušit výběr“ with the count, and Použít / Zahodit (P11) under the
 * list, which scrolls inside the card so the actions never leave the screen (up to 100 rows). It is a composition of ImpactPreview,
 * PageIcon, ui/checkbox and ResultBanner, not a dialog: the decision belongs to the flow of the conversation and may wait.
 *
 * It owns nothing but the selection: the proposal (a thread item) arrives from the stream or, for a reopened conversation, is
 * loaded by `proposalId`; every change of it goes up (`update`). The client sends only the proposal id and the indexes of the kept
 * rows. Focus starts on „Zahodit“ (the safe answer) when the card arrives live; Escape only returns the focus to the question field.
 */
const props = defineProps<{ proposal?: Proposal | null; proposalId?: string | null }>();
const emit = defineEmits<{ update: [proposal: Proposal]; "open-editor": [pageId: string] }>();
const decideRequest = inject(PROPOSAL_DECIDE, decideProposal);
const findingsRequest = inject(PROPOSAL_FINDINGS, findingsFor);

const current = ref<Proposal | null>(props.proposal ?? null);
const selected = ref<Set<number>>(new Set());
const busy = ref(false);
const error = ref("");
const loadFailed = ref(false);
const root = ref<HTMLElement>();
const hintId = `proposal-${useId()}`;
const bannerOpen = ref(true);
let loading: AbortController | undefined;

const state = computed(() => current.value?.state ?? "pending");
const items = computed(() => current.value?.items ?? []);
const open = computed(() => state.value === "pending" && !busy.value);
const choosing = computed(() => state.value === "pending" || busy.value);
const all = computed(() => selected.value.size === items.value.length && items.value.length > 0);

function adopt(proposal: Proposal) {
  current.value = proposal;
  if (proposal.state === "pending") selected.value = new Set(proposal.items.map((item) => item.index));
}
// A new message supersedes the card: the thread marks it, the card follows without a reload.
watch(() => props.proposal, (proposal) => { if (proposal && proposal !== current.value) adopt(proposal); });
if (props.proposal) adopt(props.proposal);

async function load() {
  if (current.value || !props.proposalId) return;
  loading = new AbortController();
  loadFailed.value = false;
  try {
    const proposal = await getProposal(props.proposalId, loading.signal);
    adopt(proposal);
    emit("update", proposal);
  } catch (failure) {
    if ((failure as Error).name !== "AbortError") loadFailed.value = true;
  }
}
const live = !!props.proposal;
onMounted(() => {
  void load();
  // A card that arrives with the answer takes the focus on the safe answer; a reopened conversation does not steal it.
  if (live && state.value === "pending") void nextTick(() => root.value?.querySelector<HTMLElement>("[data-proposal-cancel]")?.focus());
});
onBeforeUnmount(() => loading?.abort());

function toggle(index: number, on: boolean | "indeterminate") {
  const next = new Set(selected.value);
  if (on === true) next.add(index); else next.delete(index);
  selected.value = next;
}
const toggleAll = () => { selected.value = all.value ? new Set() : new Set(items.value.map((item) => item.index)); };

async function decide(approve: boolean) {
  const proposal = current.value;
  if (!proposal || busy.value) return;
  const indexes = approve ? proposal.items.map((item) => item.index).filter((index) => selected.value.has(index)) : [];
  if (approve && !indexes.length) return;
  busy.value = true;
  error.value = "";
  try {
    const result = await decideRequest(proposal.id, approve, indexes);
    current.value = result;
    emit("update", result);
    bannerOpen.value = true;
  } catch (failure) {
    error.value = decisionErrorText(failure);
    const code = failure instanceof ApiError && failure.status === 409 ? failure.code : undefined;
    const next = code ? CONFLICT_STATE[code] : undefined;
    if (next) { current.value = { ...proposal, state: next }; emit("update", current.value); }
    // Someone decided it already (another tab): show what really happened instead of a card that still offers the choice.
    else if (code === "proposal_decided") { try { const fresh = await getProposal(proposal.id); current.value = fresh; emit("update", fresh); } catch { /* the message stays */ } }
  } finally { busy.value = false; }
}
function onKeydown(event: KeyboardEvent) {
  if (event.key !== "Escape" || event.isComposing || !open.value) return;
  // Esc closes things by habit; declining is Zrušit's own decision, so Esc only returns to the question field (none: the panel's own Esc).
  const field = root.value?.closest("[data-assistant-panel]")?.querySelector<HTMLElement>("textarea");
  if (!field) return;
  event.preventDefault();
  event.stopPropagation();
  field.focus();
}

// --- What a row shows -------------------------------------------------------------------------------------------------
const NO_LABELS = { icon: null, color: null, tags: [] as string[] };
const before = (item: ProposalItem) => item.before ?? NO_LABELS, after = (item: ProposalItem) => item.after ?? NO_LABELS;
const iconChanged = (item: ProposalItem) => before(item).icon !== after(item).icon || before(item).color !== after(item).color;
function tagDiff(item: ProposalItem) {
  const old = new Set(before(item).tags.map(tagKey)), next = new Set(after(item).tags.map(tagKey));
  return [
    ...after(item).tags.map((name) => ({ name, kind: old.has(tagKey(name)) ? "kept" : "added" })),
    ...before(item).tags.filter((name) => !next.has(tagKey(name))).map((name) => ({ name, kind: "removed" })),
  ] as { name: string; kind: "kept" | "added" | "removed" }[];
}
// A content change or a new draft is shown by HistoryChanges over Markdown only (no stored revision yet, so no rendered view).
const revision = (item: ProposalItem, markdown: string, version: number): PageRevision => ({
  id: `${current.value?.id}-${item.index}-${version}`, pageId: item.pageId, version, title: item.title, authorId: "", authorName: "", activeFrom: "", activeTo: null,
  isCurrent: false, isPublished: false, lastPublishedAt: null, contentJson: "", markdown,
});
const diffs = computed(() => new Map(items.value.filter((item) => item.text && (item.kind === "content" || item.kind === "draft")).map((item) => [item.index, {
  previous: item.kind === "draft" ? null : revision(item, item.text!.before, 0),
  next: revision(item, item.text!.after, 1),
  label: item.kind === "draft" ? t.proposal.draftStart : t.proposal.changedSections(item.text!.sections.join(", ")),
}])));
const canOpen = (item: ProposalItem) => ["content", "rename", "draft"].includes(item.kind) && item.status === "applied" && !!item.pageId;
const moveFacts = (item: ProposalItem) => {
  const move = item.move!;
  const subpages = move.subpages ? t.proposal.subpages(move.subpages) : t.proposal.noSubpages;
  return move.otherSpace ? t.proposal.otherSpace(subpages) : subpages;
};
const publishFacts = (item: ProposalItem) => {
  const publish = item.publish!;
  const versions = t.proposal.version(publish.version, publish.publishedVersion ?? null);
  const sections = t.proposal.sections(publish.changedSections);
  return [versions, sections, publish.changeSummary && t.proposal.changeSummary(publish.changeSummary)].filter(Boolean).join(" · ");
};
// The check before publication: the server's link findings at once, then findingsFor adds the
// browser's rules over the saved version when it is still the one to publish.
const checks = ref(new Map<number, PublicationCheck[]>());
watch(() => current.value?.id, () => {
  const proposal = current.value;
  const next = new Map<number, PublicationCheck[]>();
  for (const item of proposal?.items ?? []) {
    if (!item.publish) continue;
    const request = { pageId: item.pageId, version: item.publish.version, findings: item.publish.findings };
    next.set(item.index, linkFindings(request.findings.links).map(({ target: _target, ...check }) => check));
    void findingsRequest(request).then((found) => { if (current.value?.id === proposal!.id) checks.value = new Map(checks.value).set(item.index, found); });
  }
  checks.value = next;
}, { immediate: true });
const kindText = (kind: "kept" | "added" | "removed") => kind === "added" ? t.proposal.addTag : kind === "removed" ? t.proposal.removeTag : "";
function statusText(item: ProposalItem) {
  switch (item.status) {
    case "applied": return t.proposal.applied;
    case "failed": return itemFailureText(item.code);
    case "skipped": return t.proposal.skipped;
    case "rolled_back": return t.proposal.rolledBack;
    default: return "";
  }
}
const explanation = computed(() => ({ expired: t.proposal.expired, superseded: t.proposal.superseded, declined: t.proposal.declined } as Record<string, string>)[state.value] ?? "");
</script>

<template>
  <section v-if="current" ref="root" class="asp-card" role="group" :aria-label="t.proposal.group" :data-state="state" data-assistant-proposal @keydown="onKeydown">
    <ImpactPreview :title="t.proposal.title" :items="[]">
      <div v-if="choosing" class="asp-bar" data-proposal-bar>
        <span class="asp-count" aria-live="polite" data-proposal-count>{{ t.proposal.count(selected.size, items.length) }}</span>
        <Button type="button" variant="ghost" size="sm" :disabled="busy" data-proposal-toggle-all @click="toggleAll">{{ all ? t.proposal.selectNone : t.proposal.selectAll }}</Button>
      </div>
      <p v-else-if="explanation" class="asp-note" data-proposal-note>{{ explanation }}</p>

      <ul class="asp-list" :aria-label="t.proposal.list(items.length)" data-proposal-rows>
        <li v-for="item in items" :key="item.index" class="asp-row" :data-status="item.status ?? undefined" :data-selected="choosing ? selected.has(item.index) : undefined">
          <Checkbox
            v-if="choosing"
            :id="`${hintId}-${item.index}`"
            class="asp-check"
            :model-value="selected.has(item.index)"
            :disabled="busy"
            :aria-label="`${item.title}, ${item.path}`"
            @update:model-value="(on) => toggle(item.index, on)"
          />
          <span v-else class="asp-mark" :data-status="item.status ?? undefined" aria-hidden="true">
            <AtlasIcon :name="item.status === 'applied' ? 'check' : item.status === 'failed' ? 'triangle-alert' : 'minus'" :size="14" />
          </span>
          <div class="asp-main">
            <label class="asp-body" :for="choosing ? `${hintId}-${item.index}` : undefined">
              <span v-if="item.kind === 'rename'" class="asp-title asp-rename" data-proposal-rename>
                <del class="asp-name">{{ item.text?.before }}</del><span class="sr-only">{{ t.proposal.renameTo }}</span><AtlasIcon name="arrow-right" :size="12" /><ins class="asp-name">{{ item.text?.after }}</ins>
              </span>
              <span v-else-if="item.kind === 'move' || item.kind === 'publish'" class="asp-title">
                <AtlasIcon :name="item.kind === 'move' ? 'folder-input' : 'published'" :size="14" />
                <span class="asp-name">{{ item.kind === "move" ? t.proposal.moveTitle(item.title) : t.proposal.publishTitle(item.title) }}</span>
              </span>
              <span v-else-if="item.kind !== 'labels'" class="asp-title">
                <AtlasIcon :name="item.kind === 'draft' ? 'file-plus' : 'pencil'" :size="14" />
                <span class="asp-name">{{ item.kind === "draft" ? t.proposal.newDraft(item.title) : item.title }}</span>
              </span>
              <span v-else class="asp-title">
                <span class="asp-icons">
                  <PageIcon v-if="iconChanged(item) && item.before?.icon" :decoration="{ revisionId: '', icon: item.before.icon, color: item.before.color }" :size="16" />
                  <AtlasIcon v-if="iconChanged(item) && item.before?.icon" name="arrow-right" :size="12" />
                  <PageIcon v-if="item.after?.icon" :decoration="{ revisionId: '', icon: item.after.icon, color: item.after.color }" :size="16" />
                  <span v-else class="sr-only">{{ t.proposal.noIcon }}</span>
                </span>
                <span class="asp-name">{{ item.title }}</span>
              </span>
              <small v-if="item.move" class="asp-path asp-move" data-proposal-move>
                <span class="asp-from"><span class="sr-only">{{ t.proposal.from }}</span>{{ item.move.before }}</span>
                <span class="asp-to"><AtlasIcon name="arrow-right" :size="12" /><span class="sr-only">{{ t.proposal.to }}</span>{{ item.move.after }}</span>
              </small>
              <small v-else class="asp-path">{{ item.path }}</small>
              <small v-if="item.move" class="asp-facts" data-proposal-move-facts>{{ moveFacts(item) }}</small>
              <small v-if="item.publish" class="asp-facts" data-proposal-publish>{{ publishFacts(item) }}</small>
              <span v-if="tagDiff(item).length" class="asp-tags tags-ui">
                <span v-for="tag in tagDiff(item)" :key="tag.kind + tag.name" class="tag-chip asp-tag" :data-tone="tagTone(tag.name)" :data-diff="tag.kind">
                  <span v-if="tag.kind !== 'kept'" class="sr-only">{{ kindText(tag.kind) }}</span>
                  <span v-if="tag.kind !== 'kept'" aria-hidden="true">{{ tag.kind === "added" ? "+" : "−" }}</span>{{ tag.name }}
                </span>
              </span>
              <span v-if="item.status" class="asp-status" :data-status="item.status" data-proposal-status>{{ statusText(item) }}</span>
            </label>
            <div v-if="item.publish" class="asp-findings" data-proposal-findings>
              <PublishFindings v-if="checks.get(item.index)?.length" :checks="checks.get(item.index)!" />
              <p v-else class="asp-note" data-no-findings>{{ t.proposal.noFindings }}</p>
            </div>
            <HistoryChanges v-if="diffs.get(item.index)" class="asp-diff" :previous="diffs.get(item.index)!.previous" :revision="diffs.get(item.index)!.next" :label="diffs.get(item.index)!.label" data-proposal-diff />
            <Button v-if="canOpen(item)" type="button" variant="outline" size="sm" class="asp-open" data-proposal-open-editor @click="emit('open-editor', item.pageId)"><AtlasIcon name="pencil" :size="14" />{{ t.proposal.openEditor }}</Button>
          </div>
        </li>
      </ul>

      <p v-if="error" class="asp-error" role="alert" data-proposal-error><AtlasIcon name="triangle-alert" :size="14" />{{ error }}</p>

      <ResultBanner v-if="(state === 'applied' || state === 'failed') && bannerOpen" :message="summaryText(items)" :icon="state === 'failed' ? 'triangle-alert' : 'circle-check'" @dismiss="bannerOpen = false" />

      <div v-if="choosing" class="asp-actions" data-proposal-actions>
        <Button type="button" variant="outline" size="sm" :disabled="busy" data-proposal-cancel @click="decide(false)">{{ t.proposal.discard }}</Button>
        <Button type="button" size="sm" :disabled="busy || selected.size === 0" data-proposal-confirm @click="decide(true)">{{ busy ? t.proposal.working : t.proposal.apply(selected.size) }}</Button>
      </div>
    </ImpactPreview>
  </section>
  <ErrorState v-else-if="loadFailed" :title="t.proposal.loadFailed" :retry-label="t.proposal.retry" data-proposal-error @retry="load" />
</template>

<style scoped>
.asp-card { margin-top: 8px; }
.asp-card :deep(.impact-preview) { padding: 10px 12px; gap: 8px; }
.asp-card[data-state="expired"], .asp-card[data-state="superseded"] { opacity: .8; }
.asp-bar { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.asp-count { font-size: 12.5px; font-weight: 600; color: var(--text); font-variant-numeric: tabular-nums; }
.asp-note { margin: 0; font-size: 12.5px; color: var(--muted); }
/* The list scrolls inside the card, so the bar above it and the actions below it stay in view for 100 rows. */
.asp-list { display: grid; gap: 2px; margin: 0; padding: 0; list-style: none; max-height: min(50vh, 340px); overflow-y: auto; overscroll-behavior: contain; }
.asp-row { display: flex; align-items: flex-start; gap: 8px; padding: 6px 4px; border-radius: 8px; transition: opacity var(--atlas-motion-fast); }
.asp-row[data-selected="false"] { opacity: .6; }
.asp-check { margin-top: 3px; }
.asp-mark { display: grid; place-items: center; flex: none; width: 16px; height: 16px; margin-top: 3px; border-radius: 50%; color: var(--muted); }
.asp-mark[data-status="applied"] { color: var(--success); }
.asp-mark[data-status="failed"] { color: var(--error); }
.asp-main { display: grid; gap: 6px; min-width: 0; flex: 1; }
.asp-rename del { text-decoration-color: var(--error); color: var(--muted); font-weight: 500; }
.asp-rename ins { text-decoration: none; }
/* The word diff of the history, compact inside the card: the list scrolls, so its toolbar does not stick. */
.asp-diff { border: 1px solid var(--line); border-radius: 8px; padding: 0 10px; }
.asp-diff :deep(.changes-toolbar) { position: static; min-height: 32px; gap: 8px; }
.asp-diff :deep(.text-diff) { padding: 8px 0 10px; font-size: 12.5px; line-height: 1.6; }
.asp-open { justify-self: start; }
.asp-body { display: grid; grid-template-columns: minmax(0, 1fr); gap: 2px; min-width: 0; flex: 1; cursor: pointer; font-size: 13px; }
.asp-title { display: flex; align-items: center; gap: 6px; min-width: 0; }
.asp-icons { display: inline-flex; align-items: center; gap: 3px; flex: none; color: var(--muted); }
.asp-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 600; }
.asp-path { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--muted); font-size: 11.5px; }
/* A move: the old path struck through over the new one, each on its own line so neither is cut on 390 px. */
.asp-move { display: grid; gap: 1px; white-space: normal; }
.asp-from { text-decoration: line-through; text-decoration-color: var(--error); }
.asp-to { display: inline-flex; align-items: center; gap: 4px; color: var(--text); }
.asp-facts { color: var(--muted); font-size: 11.5px; }
.asp-findings { border: 1px solid var(--line); border-radius: 8px; padding: 4px 8px; }
.asp-tags { display: flex; flex-wrap: wrap; gap: 4px; }
.asp-card .asp-tag { min-height: 20px; padding: 0 6px; font-size: 11.5px; gap: 2px; }
.asp-tag[data-diff="added"] { font-weight: 700; }
.asp-tag[data-diff="removed"] { text-decoration: line-through; opacity: .75; border-style: dashed; background: transparent; }
.asp-status { font-size: 11.5px; font-weight: 600; color: var(--muted); }
.asp-status[data-status="applied"] { color: var(--success); }
.asp-status[data-status="failed"] { color: var(--error); }
.asp-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 8px; }
.asp-error { display: flex; align-items: center; gap: 6px; margin: 0; font-size: 12.5px; color: var(--error); }
.asp-card :deep(.result-banner) { margin: 0; }
@media (prefers-reduced-motion: reduce) { .asp-row { transition: none; } }
</style>
