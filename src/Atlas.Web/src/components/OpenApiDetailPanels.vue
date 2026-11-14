<script setup lang="ts">
import { computed } from "vue";
import { lightOpenApiSummary } from "@atlas/markdown/plugins/openapi";
import { openApiDetailMessages, type OpenApiDetail, type OpenApiDetailView } from "../openApiDetail";
import AppDialog from "./AppDialog.vue";
import { Button } from "./ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "./ui/tabs";
import { formatDateTime } from "../formatDate";
import t from "../i18n/components-search";

// The OpenAPI detail of the page: a full-height panel (AppDialog size="side", the whole width of a phone) over the page,
// with ONE scrolling area, the Scalar frame or the source editor; the page behind stays where it was and does not scroll.
// Dokumentace (Scalar, Try It off) for everyone, YAML (Monaco) for editors: „Použít“ writes the draft into the block
// through the editor's transaction. All state lives in the openApiDetail composable owned by DocumentEditor.
// `change`: who saved the page's current revision and when (DocumentEditor reads it from the page history).
const props = defineProps<{ detail: OpenApiDetail; change?: { at: string; by: string } }>();
const detail = props.detail;
const applyNote = t.openApi.applyNote;
const heading = computed(() => {
  const data = detail.data.value;
  return data ? lightOpenApiSummary(data).title : detail.title.value;
});
const shown = computed(() => detail.tabs.value.map(tab => tab.id));
const unapplicable = computed(() => !!detail.confirmation.value?.unapplicable || !!detail.blocked.value);
const retainedMessage = computed(() => detail.retained.value ? openApiDetailMessages[detail.retained.value] : "");
function select(view: string | number) { detail.activate(view as OpenApiDetailView); }
function intent(view: string) { if (view === "yaml") detail.prefetchSource(); }
// Tab indents in the source editor, so Esc (when no editor widget took it) leaves it for the tabs, from where Esc closes.
function leaveSource(event: KeyboardEvent) {
  const host = event.currentTarget as HTMLElement;
  // A suggestion, find or parameter widget of the editor takes its own Esc.
  if (event.defaultPrevented || host.querySelector(".suggest-widget.visible, .find-widget.visible, .parameter-hints-widget.visible, .monaco-hover:not(.hidden), .rename-box.visible")) return;
  event.preventDefault();
  event.stopPropagation();
  const dialog = host.closest("dialog");
  (dialog?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]') ?? dialog?.querySelector<HTMLElement>(".app-dialog-header button"))?.focus();
}
function setSourceHost(element: unknown) { detail.sourceHost.value = (element as HTMLElement | null) ?? undefined; }
function setReferenceHost(element: unknown) { detail.referenceHost.value = (element as HTMLElement | null) ?? undefined; }
</script>

<template>
  <AppDialog class="openapi-detail-dialog" size="side" :title="heading" :open="detail.open.value" :close-label="t.openApi.closeDetail"
    @close="detail.requestClose()" @closed="detail.closed()">
    <!-- The panels stay mounted while the detail is open: Scalar keeps its place and the YAML draft its editor. -->
    <Tabs :model-value="detail.view.value" :unmount-on-hide="false" class="openapi-detail" @update:model-value="select">
      <div class="openapi-detail-bar">
        <TabsList v-if="shown.length > 1" :aria-label="t.openApi.views">
          <TabsTrigger v-for="tab in detail.tabs.value" :key="tab.id" :value="tab.id" @pointerenter="intent(tab.id)" @focus="intent(tab.id)">{{ tab.label }}</TabsTrigger>
        </TabsList>
        <span class="openapi-detail-meta" data-openapi-detail-change>{{ detail.data.value?.filename }} · {{ detail.dirty.value ? t.openApi.draft : t.openApi.saved }}<Transition name="openapi-detail-change"><span v-if="change"> · <time :datetime="change.at">{{ t.openApi.changedAt(formatDateTime(change.at)) }}</time> · {{ change.by }}</span></Transition></span>
      </div>
      <TabsContent v-if="shown.includes('api')" value="api" class="openapi-detail-panel" data-openapi-detail="api">
        <p v-if="detail.detached.value" class="openapi-detail-note" role="status">{{ openApiDetailMessages.detached }}</p>
        <p v-else-if="detail.dirty.value" class="openapi-detail-note">{{ t.openApi.draftKept }}</p>
        <div :ref="setReferenceHost" class="openapi-detail-reference" data-testid="openapi-detail-reference" />
      </TabsContent>
      <TabsContent v-if="shown.includes('yaml')" value="yaml" class="openapi-detail-panel" data-openapi-detail="yaml">
        <div v-if="detail.retained.value" class="openapi-detail-retained notice" role="alert">
          <p>{{ retainedMessage }}</p>
          <div class="openapi-detail-actions">
            <Button variant="outline" type="button" @click="detail.downloadDraft()">{{ t.openApi.downloadDraft }}</Button>
            <Button variant="destructive" type="button" @click="detail.discardDraft()">{{ t.openApi.discardDraft }}</Button>
          </div>
        </div>
        <div v-else :ref="setSourceHost" class="openapi-detail-source" data-testid="openapi-detail-source" @keydown.esc.capture="leaveSource" />
        <p v-if="detail.error.value" class="openapi-detail-error" role="alert">{{ detail.error.value }}</p>
        <footer v-if="!detail.retained.value" class="openapi-detail-footer">
          <span>{{ applyNote }}</span>
          <Button variant="outline" type="button" :disabled="detail.loading.value || !detail.dirty.value" @click="detail.discard()">{{ t.openApi.discardChanges }}</Button>
          <Button variant="default" type="button" :disabled="detail.loading.value || !detail.dirty.value || !!detail.blocked.value" @click="detail.apply()">{{ t.openApi.apply }}</Button>
        </footer>
      </TabsContent>
    </Tabs>
  </AppDialog>

  <AppDialog :title="t.openApi.confirmTitle" :open="!!detail.confirmation.value" @close="detail.choose('continue')">
    <template v-if="unapplicable">
      <p>{{ detail.blocked.value === 'readonly' ? openApiDetailMessages.readonly : openApiDetailMessages.lost }}</p>
      <p>{{ t.openApi.cannotApply }}</p>
    </template>
    <template v-else>
      <p>{{ t.openApi.unapplied }}</p>
      <p class="openapi-detail-hint">{{ applyNote }}</p>
    </template>
    <template #footer>
      <Button variant="outline" type="button" @click="detail.choose('continue')">{{ t.openApi.keepEditing }}</Button>
      <Button v-if="unapplicable" variant="outline" type="button" @click="detail.downloadDraft()">{{ t.openApi.downloadDraft }}</Button>
      <Button variant="destructive" type="button" @click="detail.choose('discard')">{{ t.openApi.discard }}</Button>
      <Button v-if="!unapplicable" variant="default" type="button" @click="detail.choose('apply')">{{ t.openApi.apply }}</Button>
    </template>
  </AppDialog>
</template>

<style scoped>
/* Nearly the whole window (Scalar's sidebar and its two columns need the width); the strip on the left shows the page is still
   there. The panel itself never scrolls and keeps a wheel or swipe at its end from scrolling the page behind it. */
.openapi-detail-dialog { --app-dialog-side-width: calc(100vw - 48px); overscroll-behavior: contain; }
.openapi-detail { flex: 1; min-height: 0; gap: 0; color: var(--text); }
.openapi-detail-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 16px; padding: 8px 22px; border-bottom: 1px solid var(--line); }
.openapi-detail-meta { min-width: 0; font-size: 12px; color: var(--muted); overflow-wrap: anywhere; }
.openapi-detail-change-enter-active { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
.openapi-detail-panel { display: flex; flex-direction: column; min-height: 0; }
.openapi-detail-panel[hidden] { display: none; }
.openapi-detail-note { margin: 0; padding: 10px 22px; font-size: 12px; color: var(--muted); background: var(--surface-muted, color-mix(in srgb, var(--accent) 6%, transparent)); }
.openapi-detail-reference, .openapi-detail-source { flex: 1; min-height: 0; }
.openapi-detail-reference { display: flex; flex-direction: column; }
.openapi-detail-reference :deep(iframe) { flex: 1; min-height: 0; width: 100%; height: auto; border: 0; display: block; }
.openapi-detail-retained { margin: 16px 22px; display: grid; gap: 12px; }
.openapi-detail-retained p { margin: 0; overflow-wrap: anywhere; }
.openapi-detail-actions { display: flex; flex-wrap: wrap; gap: 8px; }
.openapi-detail-error { margin: 0; padding: 8px 22px; color: var(--error); overflow-wrap: anywhere; }
.openapi-detail-footer { display: flex; gap: 8px; align-items: center; padding: 12px 22px; border-top: 1px solid var(--line); }
.openapi-detail-footer > span { flex: 1; font-size: 12px; color: var(--muted); }
.openapi-detail-hint { color: var(--muted); font-size: 13px; }
@media (max-width: 600px) {
  .openapi-detail-bar { padding: 8px 12px; }
  .openapi-detail-footer { flex-wrap: wrap; padding: 10px 12px; }
  .openapi-detail-footer > span { flex-basis: 100%; }
}
</style>
