<script setup lang="ts">
import { computed, nextTick, ref, useId, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import type { ActionMenuItem } from "../../ActionMenu.vue";
import RowActionsMenu from "../../RowActionsMenu.vue";
import { Button } from "../../ui/button";
import AccessLevelBadge from "../AccessLevelBadge.vue";
import InlineReveal from "../InlineReveal.vue";
import SettingsListRow from "../SettingsListRow.vue";
import StatusBadge from "../StatusBadge.vue";
import SubjectAvatar from "../SubjectAvatar.vue";
import { formatDateTime, formatShortDate } from "../../../formatDate";
import t from "../../../i18n/settings-access";
import AccessGrantEditor from "./AccessGrantEditor.vue";
import { accessKind, allLevelOptions } from "./accessKinds";
import type { AccessChangeDraft, AccessSource } from "./accessModel";
import { accessEnd, directSource, effectiveSources, expiringSoon, grantEnd, groupSource, sourceText, sourceTiming, sourcesSummary, type AccessRow } from "./accessView";

/**
 * One access as one sentence, the same in every list: who or what · where it comes from · until when · the level on the
 * kind's staircase. A person's card shows the resource in the row; a resource's panel shows the person or group. The level
 * badge comes last with a fixed width, so the levels of a list form one coloured column along the right edge, and the
 * resource icon is tinted with the level colour. Actions fade in over the row's right end on hover or keyboard focus (and
 * stay visible below the row on touch devices): Změnit opens the inline editor and Odebrat asks the owner to confirm. A
 * right through a group is changed on the group, so the row offers the group instead; automatic rows are locked. Ctrl+mezerník,
 * Alt+Enter, Ctrl+., Shift+F10 and the right button open the same two actions as „Co s tím jde dělat“ (RowActionsMenu, P2).
 */
const props = withDefaults(
  defineProps<{
    row: AccessRow;
    readonly?: boolean;
    editing?: boolean;
    busy?: boolean;
    /** Where the group of a right through a group opens; without it the row names the group only in its sentence. */
    groupHref?: (groupId: string) => string | undefined;
  }>(),
  { readonly: false, editing: false, busy: false, groupHref: undefined },
);
const emit = defineEmits<{
  change: [row: AccessRow];
  revoke: [row: AccessRow, grant: AccessSource];
  save: [row: AccessRow, grant: AccessSource, draft: AccessChangeDraft];
  cancel: [row: AccessRow];
}>();

const kind = computed(() => accessKind(props.row.kind));
const options = allLevelOptions();
const description = computed(() => [props.row.detail, props.row.locked ? undefined : sourcesSummary(props.row.sources)].filter(Boolean).join(" · "));
const grant = computed(() => (props.row.locked ? undefined : directSource(props.row.sources)));
const viaGroup = computed(() => (grant.value ? undefined : groupSource(props.row.sources)));
const viaGroupHref = computed(() => (viaGroup.value?.groupId ? props.groupHref?.(viaGroup.value.groupId) : undefined));
// Nothing gives access now: the strongest path is waiting for its start, or has ended.
const timing = computed(() => (props.row.sources.some((source) => source.effective) ? "active" : props.row.sources[0] ? sourceTiming(props.row.sources[0]) : "active"));
const end = computed(() => accessEnd(props.row.sources) ?? (timing.value === "active" ? undefined : effectiveSources(props.row.sources)[0]?.expiresAt ?? undefined));
const start = computed(() => (timing.value === "pending" ? effectiveSources(props.row.sources)[0]?.startAt ?? undefined : undefined));
const soon = computed(() => expiringSoon(end.value));
const sourcesOpen = ref(false);
const sourcesId = `access-sources-${useId()}`;
const hubLease = computed(() => props.row.sources.some(source => source.verificationLimited));
// A right through a group offers its group only where the group can be opened (the security centre, space settings).
const hasActions = computed(() => !props.readonly && (!!grant.value || !!viaGroupHref.value));
/** „Co s tím jde dělat“ of the row (P2): what Změnit and Odebrat do; a locked row or a right through a group has none here. */
const rowActions = computed<ActionMenuItem[]>(() => (hasActions.value && grant.value ? [
  { id: "change", label: t.row.change, icon: "pencil", shortcut: "F2", disabled: props.busy },
  { id: "revoke", label: t.row.remove, icon: "trash", shortcut: "Delete", destructive: true, group: "revoke", disabled: props.busy },
] : []));
// The editor closed (Zrušit, Esc, saved) and took the focus with it: it goes back to Změnit (AD5-9).
const entry = ref<{ $el: HTMLElement }>();
watch(() => props.editing, (now, before) => {
  if (before && !now) void nextTick(() => { if (!document.activeElement || document.activeElement === document.body) entry.value?.$el.querySelector<HTMLElement>("[data-change-access]")?.focus(); });
});
</script>

<template>
  <SettingsListRow ref="entry" :title="row.title" :description="description" class="access-entry" :data-access-entry="row.key" :data-editing="editing || undefined">
    <template #leading>
      <SubjectAvatar v-if="row.subject" :kind="row.subject.kind" :name="row.title" />
      <span v-else class="st-icon-tile access-entry-icon" :data-level-color="row.level ?? undefined" :data-tone="row.level ? undefined : 'neutral'"><AtlasIcon :name="kind.icon" :size="17" /></span>
    </template>
    <template v-if="row.href" #title><a class="access-entry-link" :href="row.href">{{ row.title }}</a></template>
    <template #meta>
      <StatusBadge v-if="kind.plugin" tone="neutral" icon="blocks" :label="kind.plugin" size="sm" />
      <StatusBadge v-if="row.locked" tone="neutral" icon="lock" :label="t.row.automatic" size="sm" />
      <StatusBadge v-if="row.subject?.isActive === false" tone="neutral" icon="ban" :label="t.row.deactivated" size="sm" />
      <StatusBadge v-if="timing === 'pending'" tone="warning" icon="hourglass" :label="start ? t.row.since(formatShortDate(start)) : t.row.notValidYet" :title="start ? t.row.startsAt(formatDateTime(start)) : undefined" size="sm" data-access-pending />
      <StatusBadge v-else-if="timing === 'expired'" tone="warning" icon="hourglass" :label="hubLease ? t.row.hubExpired : t.row.expired" size="sm" data-access-expired />
      <StatusBadge v-else-if="end" :tone="soon ? 'warning' : 'info'" :icon="soon ? 'hourglass' : 'calendar'" :label="t.row.until(formatDateTime(end))" :title="t.row.validUntil(formatDateTime(end))" size="sm" data-access-end />
      <StatusBadge v-if="hubLease && timing === 'active'" tone="neutral" icon="refresh-cw" :label="t.row.hubVerified" size="sm" data-hub-verification />
      <!-- The level always comes last and has a fixed width, so the levels of a list form one coloured column. -->
      <AccessLevelBadge v-if="row.level" :options="options" :value="row.level" tone="level" class="access-entry-level" />
      <StatusBadge v-else tone="neutral" icon="eye-off" :label="t.row.none" size="sm" class="access-entry-level" />
    </template>
    <!-- Actions show on hover or keyboard focus of the row; they keep their room, so nothing moves. -->
    <template v-if="hasActions" #actions>
      <template v-if="grant">
        <Button variant="ghost" size="sm" type="button" :disabled="busy" :aria-expanded="editing" :aria-label="t.row.changeFor(row.title)" data-change-access @click="emit('change', row)"><AtlasIcon name="pencil" :size="15" />{{ t.row.change }}</Button>
        <Button variant="ghost" size="sm" type="button" :disabled="busy" :aria-label="t.row.removeFor(row.title)" data-revoke-access @click="emit('revoke', row, grant)"><AtlasIcon name="trash" :size="15" />{{ t.row.remove }}</Button>
      </template>
      <Button v-else-if="viaGroup && viaGroupHref" variant="ghost" size="sm" as-child><a :href="viaGroupHref" data-open-group><AtlasIcon name="users" :size="15" />{{ t.row.group(viaGroup.label) }}</a></Button>
    </template>
    <RowActionsMenu :items="rowActions" :label="t.row.actionsFor(row.title)" @select="id => (id === 'change' ? emit('change', row) : grant && emit('revoke', row, grant))" />
    <template v-if="row.sources.length && !row.locked">
      <Button variant="ghost" size="sm" class="access-sources-toggle" :aria-expanded="sourcesOpen" :aria-controls="sourcesId" @click="sourcesOpen = !sourcesOpen"><AtlasIcon :name="sourcesOpen ? 'chevron-up' : 'chevron-down'" :size="14" />{{ t.row.why }}</Button>
      <InlineReveal :id="sourcesId" :open="sourcesOpen">
        <ul class="access-sources">
          <li v-for="(source, index) in row.sources" :key="index">
            <div class="access-source-line"><a v-if="source.groupId && groupHref?.(source.groupId)" :href="groupHref(source.groupId)">{{ sourceText(source) }}</a><span v-else>{{ sourceText(source) }}</span><AccessLevelBadge :options="options" :value="source.level" tone="level" /></div>
            <p v-if="!source.effective" class="st-note">{{ sourceTiming(source) === 'pending' ? t.row.notValidYet : t.row.expired }}</p>
            <p v-if="grantEnd(source)" class="st-note">{{ t.row.until(formatDateTime(grantEnd(source)!)) }}</p>
            <p v-if="source.verificationLimited" class="st-note">{{ t.row.hubHelp }} {{ source.expiresAt ? t.row.verifiedUntil(formatDateTime(source.expiresAt)) : '' }}</p>
            <p v-if="source.via === 'group'" class="st-note">{{ source.groupSource === 'hub' ? t.row.hubMembership : t.row.groupMembership }}</p>
          </li>
        </ul>
      </InlineReveal>
    </template>
    <InlineReveal v-if="grant && !readonly" :open="editing">
      <AccessGrantEditor v-if="editing" :kind="row.kind" :grant="grant" :name="row.title" :busy="busy" @save="emit('save', row, grant, $event)" @cancel="emit('cancel', row)" />
    </InlineReveal>
  </SettingsListRow>
</template>

<style scoped>
.access-sources-toggle { margin-bottom: 8px; }
.access-sources { list-style: none; margin: 0; padding: 0 0 12px; display: grid; gap: 12px; }
.access-source-line { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; }
.access-source-line a { color: var(--accent); text-decoration: underline; overflow-wrap: anywhere; }
.access-source-line a:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
/* The hover band is inset 10 px from the content on both sides. A list that bleeds to the edges of its card (--st-row-pad-x,
   the inset of the card header) keeps its labels on that inset, so the band stops 8 px short of the card instead of
   running 10 px past it; in a list without the bleed the band reaches 10 px past the content as before. */
.access-entry { --row-hover: color-mix(in srgb, var(--hover) 55%, var(--surface)); margin-inline: calc(var(--st-row-pad-x, 0px) - 10px); padding-inline: 10px; border-radius: var(--st-radius-sm); transition: background-color var(--atlas-motion-fast); }
/* The divider above a row is a line of its own along the content: drawn as the row's border it followed the rounded band and
   hooked upwards at both ends. The border stays (transparent) so the row keeps its height. */
.access-entry { position: relative; border-top-color: transparent; }
.access-entry:not(:first-child)::before { content: ""; position: absolute; inset: 0 10px auto; height: 1px; background: var(--st-line-soft, color-mix(in srgb, var(--line) 75%, transparent)); }
.access-entry:focus-within { background: var(--row-hover); }
@media (hover: hover) and (pointer: fine) {
  .access-entry:hover { background: var(--row-hover); }
}
.access-entry-icon { width: 36px; height: 36px; border-radius: var(--st-radius-sm); }
.access-entry-icon[data-level-color] { color: var(--level); background: var(--level-bg); }
.access-entry-level { min-width: 104px; justify-content: flex-start; }
.access-entry-link { color: inherit; text-decoration: none; border-radius: 4px; }
@media (hover: hover) and (pointer: fine) {
  .access-entry-link:hover { color: var(--accent); text-decoration: underline; text-underline-offset: 3px; }
}
.access-entry-link:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
/* With a mouse the levels run down the right edge as one column; the actions of the hovered (or focused) row fade in
   over its right end and cover the badges there, so nothing moves. The fade needs no movement, so it looks the same
   with reduced motion. */
/* Two columns, the actions never take one: an empty third column still cost its 14 px gap and pushed the levels that far
   in from the right inset (33 px from the card's edge against 18 px on the left). */
.access-entry :deep(.settings-list-row-line) { grid-template-columns: minmax(0, 1fr) auto; }
@media (hover: hover) {
  .access-entry :deep(.settings-list-row-line) { position: relative; }
  .access-entry :deep(.settings-list-row-actions) {
    grid-column: 1 / -1;
    position: absolute;
    inset-block: 0;
    right: 0;
    align-items: center;
    padding-left: 40px;
    background: linear-gradient(to right, transparent, var(--row-hover) 40px);
    opacity: 0;
    pointer-events: none;
    transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out);
  }
  .access-entry:hover :deep(.settings-list-row-actions),
  .access-entry:focus-within :deep(.settings-list-row-actions),
  .access-entry[data-editing] :deep(.settings-list-row-actions) { opacity: 1; pointer-events: auto; }
}
/* Touch: nothing to hover, so the actions stay visible on their own line below the row. */
@media (hover: none) {
  .access-entry :deep(.settings-list-row-actions) { grid-column: 1 / -1; grid-row: 3; justify-content: flex-start; padding-left: 40px; }
}
/* A phone: the level and its badges get the whole line. */
@container (max-width: 460px) {
  .access-entry :deep(.settings-list-row-meta) { grid-column: 1 / -1; }
  /* A narrow row has no room to cover: the actions go below it, with a mouse as well as on touch. */
  .access-entry :deep(.settings-list-row-actions) {
    position: static;
    grid-column: 1 / -1;
    grid-row: 3;
    justify-content: flex-start;
    padding-left: 40px;
    background: none;
    opacity: 1;
    pointer-events: auto;
  }
}
</style>
