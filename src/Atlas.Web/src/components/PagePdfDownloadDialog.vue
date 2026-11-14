<script setup lang="ts">
import t from "../i18n/components-b";
import type { PdfCoverDetail } from "../contentExport";
import ConfirmDialog from "./settings/ConfirmDialog.vue";
import PdfCoverDetailsField from "./PdfCoverDetailsField.vue";

/**
 * Before a manual PDF export: what its cover states about the revision, with the cover in miniature. The download itself
 * and remembering the choice are the caller's (App.vue); PagePdfExportDialog offers the same field for the automatic export.
 */
const details = defineModel<PdfCoverDetail[]>({ required: true });
defineProps<{ open: boolean; title?: string; space?: string; saveFirst?: boolean }>();
const emit = defineEmits<{ confirm: []; cancel: [] }>();
</script>

<template>
  <ConfirmDialog :open="open" :title="t.pdfdl.title" :confirm-label="saveFirst ? t.pdfdl.saveAndExport : t.pdfdl.export" confirm-icon="download" :destructive="false"
    @confirm="emit('confirm')" @cancel="emit('cancel')">
    <div class="pdf-download">
      <p class="st-note">{{ t.pdfdl.note }}</p>
      <PdfCoverDetailsField v-model="details" :title="title" :space="space" />
    </div>
  </ConfirmDialog>
</template>

<style scoped>
/* The dialog sizes to its content: wide enough for the choices beside the paper, never wider than the screen (the
   dialog's own max-width is 100% - 34px, its padding --app-dialog-pad, its border 1px). */
.pdf-download { display: grid; gap: 16px; width: min(600px, calc(100vw - 34px - 2 * var(--app-dialog-pad, 24px) - 2px)); }
</style>
