<script setup lang="ts">
import t from "../i18n/components-b";
import { locale } from "../i18n";
import { computed, ref } from "vue";
import { ALL_PDF_COVER_DETAILS, PDF_COVER_DETAILS, type PdfCoverDetail } from "../contentExport";
import { Checkbox } from "./ui/checkbox";
import OptionPills from "./settings/OptionPills.vue";
import { DisclosureGroup, disclosureHooks } from "./settings/disclosure";
// Also opened from a page, outside the settings shell (PagePdfExportDialog.vue explains why the stylesheets come along).
import "./settings/settings.css";
import "./settings/settings-tailwind.css";

/**
 * What a PDF's cover states about the exported revision, for the manual export (Export do PDF) and the automatic one
 * (Titulní strana) alike. Three presets for the usual cases, the details to tick one by one, and next to them the cover
 * itself in miniature with sample values: what is ticked unfolds into it, what is unticked folds away, and the row under
 * the pointer lights up its line on the paper. Nothing ticked is a cover with only the space and the title.
 */
const model = defineModel<PdfCoverDetail[]>({ required: true });
const props = withDefaults(defineProps<{ title?: string; space?: string; disabled?: boolean }>(), { title: "", space: "", disabled: false });

const FORMAL: PdfCoverDetail[] = ["revision", "revisionDate"];
const presets = [
  { value: "all", label: t.pdfcover.allDetails, details: ALL_PDF_COVER_DETAILS },
  { value: "formal", label: t.pdfcover.formal, details: FORMAL },
  { value: "none", label: t.pdfcover.noDetails, details: [] as PdfCoverDetail[] },
];
const preset = computed({
  get: () => presets.find(item => item.details.join() === model.value.join())?.value ?? "custom",
  set: value => { const chosen = presets.find(item => item.value === value); if (chosen) model.value = [...chosen.details]; },
});
const has = (detail: PdfCoverDetail) => model.value.includes(detail);
function toggle(detail: PdfCoverDetail, on: boolean) {
  model.value = ALL_PDF_COVER_DETAILS.filter(item => item === detail ? on : has(item));
}

// The sample cover: the document's own title and space where the dialog knows them, illustrative values for the rest.
const day = (offset: number) => new Intl.DateTimeFormat(locale, { dateStyle: "long" }).format(new Date(Date.now() - offset * 86_400_000));
const sample = { revision: "12", state: t.pdfcover.publishedState, revisionDate: day(3), exportedAt: day(0), pageId: "8c41f2a0-5d7e-4b19-a3c6-2f90d1e74b58" };
const rows = computed(() => [
  { detail: "revision" as const, label: t.pdfcover.rowRevision, value: sample.revision },
  { detail: "state" as const, label: t.pdfcover.rowState, value: sample.state },
  { detail: "revisionDate" as const, label: t.pdfcover.rowRevisionDate, value: sample.revisionDate },
  { detail: "exportedAt" as const, label: t.pdfcover.rowExported, value: sample.exportedAt },
].filter(row => has(row.detail)));
const pointed = ref<PdfCoverDetail>();
const documentTitle = computed(() => props.title.trim() || t.pdfcover.documentTitle);
</script>

<template>
  <div class="pdf-cover" :data-disabled="disabled || undefined">
    <div class="pdf-cover-layout">
    <div class="pdf-cover-choices">
      <OptionPills v-model="preset" :label="t.pdfcover.choicesLabel" :options="presets" :disabled="disabled" data-pdf-cover-preset />
      <ul class="pdf-cover-list" :aria-label="t.pdfcover.individual" data-pdf-cover-details>
        <li v-for="detail in PDF_COVER_DETAILS" :key="detail.value" @pointerenter="pointed = detail.value" @pointerleave="pointed = undefined">
          <label class="pdf-cover-item" :data-checked="has(detail.value) || undefined">
            <Checkbox :model-value="has(detail.value)" :disabled="disabled" :data-pdf-cover-detail="detail.value"
              @update:model-value="toggle(detail.value, $event === true)" @focus="pointed = detail.value" @blur="pointed = undefined" />
            <span class="pdf-cover-item-text"><span>{{ t.pdfcover.detailLabel(detail.value) }}</span><small>{{ t.pdfcover.detailHint(detail.value) }}</small></span>
          </label>
        </li>
      </ul>
    </div>

    <figure class="pdf-cover-preview" :aria-label="t.pdfcover.previewLabel">
      <div class="pdf-paper" aria-hidden="true">
        <div class="pdf-paper-header">
          <span class="pdf-paper-header-title">{{ documentTitle }}</span>
          <span :data-active="pointed === 'revision' || undefined"><Transition name="pdf-cover-fade"><span v-if="has('revision')">{{ t.pdfcover.revisionN(sample.revision) }}</span></Transition></span>
        </div>
        <p class="pdf-paper-kicker">{{ space || t.pdfcover.space }} · {{ t.pdfcover.documentation }}</p>
        <p class="pdf-paper-title">{{ documentTitle }}</p>
        <dl class="pdf-paper-metadata">
          <DisclosureGroup>
            <div v-for="row in rows" :key="row.detail" class="pdf-paper-row" :data-active="pointed === row.detail || undefined">
              <dt>{{ row.label }}</dt><dd>{{ row.value }}</dd>
            </div>
          </DisclosureGroup>
        </dl>
        <Transition :css="false" v-bind="disclosureHooks">
          <p v-if="has('pageId')" class="pdf-paper-identity" :data-active="pointed === 'pageId' || undefined">{{ t.pdfcover.pageId }} · {{ sample.pageId }}</p>
        </Transition>
      </div>
      <figcaption>{{ t.pdfcover.preview }}</figcaption>
    </figure>
    </div>
  </div>
</template>

<style scoped>
.pdf-cover { container-type: inline-size; min-width: 0; }
.pdf-cover-layout { display: grid; gap: 18px; grid-template-columns: minmax(0, 1fr); }
.pdf-cover-choices { display: grid; gap: 12px; align-content: start; min-width: 0; }
.pdf-cover-list { list-style: none; margin: 0; padding: 0; display: grid; gap: 2px; }
.pdf-cover-item {
  display: flex; align-items: center; gap: 10px; padding: 7px 10px; border-radius: 8px; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out);
}
.pdf-cover-item:hover { background: var(--surface-muted, color-mix(in srgb, currentColor 6%, transparent)); }
[data-disabled] .pdf-cover-item { cursor: default; }
.pdf-cover-item-text { display: grid; min-width: 0; line-height: 1.25; }
.pdf-cover-item-text > span { font-size: 13.5px; font-weight: 550; transition: color var(--atlas-motion-fast) var(--atlas-ease-out); }
.pdf-cover-item:not([data-checked]) .pdf-cover-item-text > span { color: var(--muted); }
.pdf-cover-item-text small { font-size: 12px; color: var(--muted); }

/* The cover in miniature: paper is white in either theme, as the PDF is. Proportions follow print.css on an A4 page. */
.pdf-cover-preview { margin: 0; display: grid; gap: 6px; justify-items: center; align-content: start; }
.pdf-cover-preview figcaption { font-size: 11.5px; color: var(--muted); }
.pdf-paper {
  width: 100%; max-width: 210px; aspect-ratio: 210 / 297; display: flex; flex-direction: column; overflow: hidden;
  padding: 7% 10.5% 8%; background: white; color: #172a36; border-radius: 3px;
  box-shadow: 0 1px 2px rgb(15 30 40 / .12), 0 8px 24px -8px rgb(15 30 40 / .28); font-size: 7.5px; line-height: 1.45;
}
.pdf-paper-header { display: flex; justify-content: space-between; gap: 8px; font-size: 5.5px; color: #526b7b; border-bottom: .5px solid #d7e0e5; padding-bottom: 3px; margin-bottom: 9%; }
.pdf-paper-header-title { overflow: hidden; white-space: nowrap; text-overflow: ellipsis; min-width: 0; }
.pdf-paper-header > span:last-child { white-space: nowrap; border-radius: 2px; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.pdf-paper .pdf-paper-kicker { margin: 14% 0 3%; color: #526b7b; font-size: 6.5px; }
.pdf-paper .pdf-paper-title { margin: 0; font-size: 16px; line-height: 1.15; font-weight: 700; letter-spacing: -.02em; overflow-wrap: anywhere;
  display: -webkit-box; -webkit-line-clamp: 4; -webkit-box-orient: vertical; overflow: hidden; }
.pdf-paper .pdf-paper-metadata { margin: auto 0 6%; display: flex; flex-direction: column; }
.pdf-paper-row { display: grid; grid-template-columns: 44% 1fr; border-bottom: .5px solid #d7e0e5; padding: 2.5px 3px; border-radius: 2px; }
.pdf-paper-row dt { color: #526b7b; }
.pdf-paper-row dd { margin: 0; font-weight: 600; }
.pdf-paper .pdf-paper-identity { margin: 0; font-size: 5.5px; color: #637888; overflow-wrap: anywhere; padding: 1px 3px; border-radius: 2px; }
/* The line the pointer or focus is on in the list lights up on the paper. */
.pdf-paper [data-active] { background: #e3f0f7; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.pdf-paper-row, .pdf-paper-identity { transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }

.pdf-cover-fade-enter-active, .pdf-cover-fade-leave-active { transition: opacity var(--atlas-motion-base) var(--atlas-ease-out); }
.pdf-cover-fade-enter-from, .pdf-cover-fade-leave-to { opacity: 0; }
@media (prefers-reduced-motion: reduce) {
  .pdf-cover-item, .pdf-paper [data-active], .pdf-paper-row, .pdf-paper-identity, .pdf-cover-item-text > span { transition: none; }
}

/* Side by side where the dialog is wide enough; stacked below, the paper under the choices. */
@container (min-width: 500px) {
  .pdf-cover-layout { grid-template-columns: minmax(0, 1fr) minmax(170px, 210px); align-items: start; }
}
</style>
