<script setup lang="ts">
import type { Editor } from "@tiptap/core";
import { computed, onMounted, toRaw } from "vue";
import t from "../i18n/projector";
import { openVuePopover } from "./openVuePopover";
import { useEditorOverview } from "./requirementAutoOverview";
import { catalogNames, catalogOf, isCatalogPage } from "./requirementCatalog";
import type { ProjectorHost } from "./requirementsPlugin";
import type { OverviewCardAction } from "../overview/overviewKinds";
import { removeOverview } from "../overview/overviewNode";
import RequirementSummaryCard from "./RequirementSummaryCard.vue";
import RequirementSummaryMenu from "./RequirementSummaryMenu.vue";

/**
 * The card of the requirements overview in the editor (the `overview` block of kind `requirements`, OverviewView.vue): the card of
 * the page's live index and outline, with its menu (a new deployment from the page, marking the page as a catalog, removing the
 * overview) while the editor is editable.
 */
const props = defineProps<{ editor: Editor; host?: ProjectorHost; actions?: readonly OverviewCardAction[] }>();
// The raw editor: a transaction built from a proxied state is refused.
const editor = toRaw(props.editor);
const host = computed(() => props.host);
const { runtime, outline, show, detail, leave } = useEditorOverview(editor, props.host);

function openMenu(anchor: HTMLElement) {
  if (!host.value || !editor.isEditable) return;
  openVuePopover({ editor, anchor, title: t.summaryMenu.options, variant: "menu", component: RequirementSummaryMenu,
    props: { host: host.value, editor, remove: () => removeOverview(editor, anchor) } });
}
// A catalog page's summary counts its definitions instead; the page says what it is, a host that does not is told by the catalog.
const catalog = computed(() => host.value && isCatalogPage(editor, host.value));
onMounted(() => { if (host.value?.pageId() && host.value.isCatalog?.() === undefined) catalogOf(editor, host.value).catch(() => {}); });
</script>

<template>
  <RequirementSummaryCard :items="runtime.index.value.items" :outline="outline" :catalog="catalog" :show="show" :detail="detail" :leave="leave"
    :catalog-names="host ? () => catalogNames(editor, host!) : undefined"
    :menu="host && runtime.editable.value ? openMenu : undefined" :menu-place="!!host?.writable?.()"
    :view-page="host?.pageId()" :actions="actions" />
</template>
