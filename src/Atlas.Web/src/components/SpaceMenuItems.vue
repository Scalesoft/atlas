<script setup lang="ts">
import AtlasIcon from "./AtlasIcon.vue";
import ActionDisclosureItem from "./ActionDisclosureItem.vue";
import t from "../i18n/components-c";

/**
 * The one list of a space's actions: the rows of the breadcrumb's „Akce prostoru“ and of the sidebar's „Další akce prostoru“, so the same
 * space offers the same actions wherever its menu opens. It renders only rows (inside an ActionDisclosure); each caller adds what is
 * its own through the slots (the sidebar: Najít ve stromu, Sbalit vše, Odepnout). The shell answers the events for the space the menu is of.
 */
defineProps<{
  /** The account may create pages here. */
  canWrite?: boolean;
  /** The account administers the space: settings, templates, access, trash, export. */
  canManage?: boolean;
  /** Module Boards is on. */
  boards?: boolean;
  /** The space has no page yet: there is nothing to export. */
  empty?: boolean;
}>();
const emit = defineEmits<{ createPage: []; createBoard: []; settings: []; templates: []; access: []; pinPolicy: []; trash: []; exportSpace: [] }>();
</script>

<template>
  <slot name="before" />
  <ActionDisclosureItem v-if="canWrite" data-space-menu="new-page" @click="emit('createPage')"><AtlasIcon name="file-plus" />{{ t.spaceMenu.newPage }}</ActionDisclosureItem>
  <ActionDisclosureItem v-if="canWrite && boards" data-space-menu="new-board" @click="emit('createBoard')"><AtlasIcon name="layout-dashboard" />{{ t.spaceMenu.newBoard }}</ActionDisclosureItem>
  <template v-if="canManage">
    <ActionDisclosureItem data-space-menu="settings" @click="emit('settings')"><AtlasIcon name="settings" />{{ t.spaceMenu.settings }}</ActionDisclosureItem>
    <ActionDisclosureItem data-space-menu="templates" @click="emit('templates')"><AtlasIcon name="layout-template" />{{ t.spaceMenu.templates }}</ActionDisclosureItem>
    <ActionDisclosureItem data-space-menu="access" @click="emit('access')"><AtlasIcon name="users" />{{ t.spaceMenu.access }}</ActionDisclosureItem>
    <ActionDisclosureItem data-space-menu="pin-policy" @click="emit('pinPolicy')"><AtlasIcon name="pin" />{{ t.spaceMenu.pinPolicy }}</ActionDisclosureItem>
    <ActionDisclosureItem data-space-menu="trash" @click="emit('trash')"><AtlasIcon name="trash" />{{ t.spaceMenu.trash }}</ActionDisclosureItem>
    <!-- A native `title`, not a tooltip: a tooltip popper inside a menu popper competes with the menu's own hover and keys. -->
    <ActionDisclosureItem data-space-menu="export" :disabled="empty"
      :title="empty ? t.spaceMenu.exportEmpty : t.spaceMenu.exportHint"
      @click="emit('exportSpace')"><AtlasIcon name="download" />{{ t.spaceMenu.export }}</ActionDisclosureItem>
  </template>
</template>
