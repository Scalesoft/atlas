<script setup lang="ts">
import t from "../../i18n/components-library";
import { locale } from "../../i18n";
import { computed, onBeforeUnmount, ref, shallowRef, useId, watch } from "vue";
import { ApiError, errorMessage, type Space } from "../../api";
import { useSpaceCoverMatch } from "../../composables/useSpaceCoverMatch";
import { spaceCoverApi, spaceCoverMaxBytes, spaceCoverTypes, type SpaceCover, type SpaceCoverChoice, type SpaceCoverMatchInput } from "../../spaceCover";
import { spaceCoverName } from "../../spaceCoverCatalog";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import { Switch } from "../ui/switch";
import { disclosureHooks } from "../settings/disclosure";
import InlineReveal from "../settings/InlineReveal.vue";
import SpaceCard from "./SpaceCard.vue";
import SpaceCoverGallery from "./SpaceCoverGallery.vue";

/**
 * The block Obrázek prostoru of Nastavení prostoru → Obecné, next to the live card the image will make: a line that
 * says how the image was chosen (automatically from the name, description and icon, from the catalog, uploaded, or
 * none), Vybrat jiný, which opens the gallery of built-in drawings (SpaceCoverGallery), Nahrát vlastní (button or
 * drop, PNG, JPEG or WebP up to 5 MB, checked here before the server checks the bytes) and, for an upload, Sladit do
 * barvy prostoru. Unlike the rest of Obecné it is saved at once on its own (spaceCover.ts), so it says so and reports
 * its errors in place: a choice shows at once and a refused one returns to the previous image. Only a choice by
 * keyboard over an uploaded image waits for Enter, because saving it deletes the file.
 * A server without the endpoints (404/405) gets a plain explanation instead of a failure.
 */
type PreviewSpace = Pick<Space, "id" | "name" | "icon" | "color" | "role" | "pageCount" | "lastChangedAt">;
type CoverApi = Pick<typeof spaceCoverApi, "upload" | "setTinted" | "choose" | "match">;
// `matchInput` is the form's unsaved name, description and icon: the automatic image follows them before they are saved.
// `api` and `coverSrc` let the gallery run the whole interaction on local data (no requests, a local drawing); `deferred` is never combined with `api`.
// `deferred`: the space does not exist yet (Nový prostor). Without an `api` the field keeps the choice (and an uploaded file) itself and
// reports it with `change`; the form saves it with the space.
const props = defineProps<{ space: PreviewSpace; cover?: SpaceCover | null; matchInput?: SpaceCoverMatchInput; disabled?: boolean; api?: CoverApi; coverSrc?: string; deferred?: boolean }>();
const emit = defineEmits<{ change: [cover: SpaceCover, file?: File] }>();

/** Arrow keys select as they move through the gallery, so a choice by keyboard is saved when it has rested this long. */
const keyboardRest = 400;
const automatic: SpaceCover = { source: "Auto", catalogKey: null, version: null, tinted: true };
const choiceStatus = { Catalog: t.cover.stored, Auto: t.cover.autoAgain, None: t.cover.none };

const uid = useId();
const input = ref<HTMLInputElement>();
const busy = ref<"upload" | "tint" | null>(null);
const error = ref("");
const status = ref("");
const unsupported = ref(false);
/** The version of the uploaded image a tint request found gone (404): its tint cannot be set any more. */
const gone = ref<string | null>();
const dragging = ref(false);
const open = ref(false);
/** The chosen file while it uploads, so the card shows it at once. */
const local = ref<string>();
/** A choice from the gallery the server has not confirmed yet: shown at once, dropped when it is refused. */
const pending = shallowRef<SpaceCoverChoice>();
/**
 * A choice by keyboard while an uploaded image is stored: only shown. Saving it makes the server delete the file, and
 * arrow keys select as they move, so it waits for Enter or Space on that option; Escape or leaving the gallery gives it up.
 */
const held = shallowRef<SpaceCoverChoice>();
const sending = ref(false);
let rest: ReturnType<typeof setTimeout> | undefined;
let generation = 0;

const accept = spaceCoverTypes.join(",");
const maxMegabytes = Math.round(spaceCoverMaxBytes / 1024 / 1024);
const own = computed(() => !!props.deferred && !props.api);
const kept = shallowRef<{ cover: SpaceCover; file?: File; src?: string }>({ cover: automatic });
function keep(cover: SpaceCover, file?: File) {
  if (kept.value.src) URL.revokeObjectURL(kept.value.src);
  kept.value = { cover, file, src: file && URL.createObjectURL(file) };
  return cover;
}
/** The calls over the draft: nothing is sent, the choice is only remembered. */
const draftApi: CoverApi = {
  async upload(_id, file) { return keep({ source: "Upload", catalogKey: null, version: "draft", tinted: kept.value.cover.tinted }, file); },
  async setTinted(_id, tinted) { return keep({ ...kept.value.cover, tinted }, kept.value.file); },
  async choose(_id, choice) { return keep({ ...automatic, source: choice.source, catalogKey: choice.catalogKey ?? null }); },
  match: spaceCoverApi.match,
};
const client = computed(() => (own.value ? draftApi : props.api ?? spaceCoverApi));
/** The new image goes up; in the deferred mode with the file it came from. */
const announce = (cover: SpaceCover) => (own.value ? emit("change", cover, kept.value.file) : emit("change", cover));
const imageSrc = computed(() => (own.value ? kept.value.src : props.coverSrc));
const stored = computed(() => (own.value ? kept.value.cover : props.cover ?? automatic));
// Asked only while the automatic image shows: as the stored image, or as the gallery's first choice.
const matched = useSpaceCoverMatch(
  computed(() => (stored.value.source === "Auto" || open.value ? props.matchInput : undefined)),
  (value, signal) => client.value.match(value, signal),
);
/** The automatic pick: for the unsaved values once the server has answered, the stored one until then and when the match fails. */
const autoKey = computed(() => (matched.value !== undefined ? matched.value : stored.value.source === "Auto" ? stored.value.catalogKey : null));
/** What the card, the line and the gallery show: the held or the pending choice, or the stored image. */
const shown = computed<SpaceCover>(() => {
  const choice = held.value ?? pending.value;
  if (choice) return { ...automatic, source: choice.source, catalogKey: choice.source === "Auto" ? autoKey.value : choice.catalogKey ?? null };
  return stored.value.source === "Auto" ? { ...stored.value, catalogKey: autoKey.value } : stored.value;
});
const shownName = computed(() => spaceCoverName(shown.value.catalogKey));
const line = computed(() => {
  const { source, catalogKey } = shown.value;
  if (source === "Upload") return t.cover.own;
  if (source === "None") return t.cover.noImage;
  if (source === "Catalog") return t.cover.fromCatalog(shownName.value ?? String(catalogKey));
  return shownName.value ? t.cover.autoChosen(shownName.value) : t.cover.autoNoMatch;
});
/** Nothing can be changed: the form around is saving, or the server has no images. */
const off = computed(() => props.disabled || unsupported.value);
/** An upload and the tint wait for what is on its way, so the server gets one change at a time. */
const locked = computed(() => off.value || !!busy.value || sending.value);

function dropLocal() {
  if (local.value) URL.revokeObjectURL(local.value);
  local.value = undefined;
}
function dropChoice() {
  clearTimeout(rest);
  rest = undefined;
  pending.value = undefined;
  held.value = undefined;
}
watch(() => props.space.id, () => { generation++; busy.value = null; sending.value = false; error.value = ""; status.value = ""; unsupported.value = false; gone.value = undefined; dropChoice(); dropLocal(); });
// A choice the keyboard had not rested on yet is dropped with the block.
onBeforeUnmount(() => { generation++; clearTimeout(rest); dropLocal(); if (kept.value.src) URL.revokeObjectURL(kept.value.src); });

/** The server explains its refusals in Czech (`detail`); only a server without the endpoint needs our words. */
function failure(reason: unknown) {
  if (reason instanceof ApiError && (reason.status === 404 || reason.status === 405)) {
    unsupported.value = true;
    return t.cover.unsupported;
  }
  return errorMessage(reason);
}

/** From the gallery. `settle`: made by keyboard, so it waits until the keyboard rests. */
function chosen(choice: SpaceCoverChoice, settle: boolean) {
  if (off.value || busy.value) return;
  const hold = settle && stored.value.source === "Upload";
  // A click is replacing the uploaded image right now: a key pressed meanwhile adds nothing to it.
  if (hold && sending.value) return;
  error.value = ""; status.value = "";
  clearTimeout(rest);
  rest = undefined;
  held.value = hold ? choice : undefined;
  if (hold) return;
  pending.value = choice;
  if (settle) rest = setTimeout(send, keyboardRest);
  else void send();
}
/**
 * Enter or Space on an option. On the held one: now it may replace the uploaded image. Focus also rests on options
 * that are not the held one (Home, End and often an arrow move it without selecting), and a key there must not
 * confirm an image the person is not on: over an uploaded image it makes that option the held one instead.
 */
function confirmed(choice: SpaceCoverChoice) {
  if (off.value || busy.value) return;
  if (held.value?.source !== choice.source || held.value.catalogKey !== choice.catalogKey) {
    if (stored.value.source === "Upload") chosen(choice, true);
    return;
  }
  pending.value = held.value;
  held.value = undefined;
  void send();
}
/** Escape gives up the choice that waits, and goes no further (as in a search field that had text). */
function escaped(event: KeyboardEvent) {
  if (!held.value) return;
  held.value = undefined;
  event.stopPropagation();
}
/** The keyboard left the gallery or the gallery was closed: what it rested on is the choice, one that waited is given up. */
function settled() {
  held.value = undefined;
  if (rest !== undefined) void send();
}
async function send() {
  clearTimeout(rest);
  rest = undefined;
  const choice = pending.value;
  // One request at a time, so the server gets the choices in their order; a newer one goes when this one returns.
  if (!choice || sending.value) return;
  const current = generation;
  sending.value = true;
  try {
    const cover = await client.value.choose(props.space.id, choice);
    if (current !== generation) return;
    announce(cover);
    if (pending.value === choice) {
      pending.value = undefined;
      status.value = choiceStatus[choice.source];
    }
  } catch (reason) {
    if (current !== generation) return;
    // The failure is this choice's: a newer one that waits is still sent. Without one the previous image stays.
    if (pending.value === choice) {
      dropChoice();
      error.value = failure(reason);
    }
  } finally {
    if (current === generation) {
      sending.value = false;
      if (pending.value && rest === undefined) void send();
    }
  }
}
function toggleGallery() {
  open.value = !open.value;
  if (!open.value) settled();
}

/** The same rule as the server's, so a wrong file fails here without a request. */
function check(file: File) {
  if (!(spaceCoverTypes as readonly string[]).includes(file.type)) return t.cover.wrongType;
  // Rounded up, so a file just over the limit never reads as exactly 5 MB.
  const size = Math.ceil(file.size / 1024 / 1024 * 10) / 10;
  if (file.size > spaceCoverMaxBytes) return t.cover.tooBig(size.toLocaleString(locale, { minimumFractionDigits: 1 }), maxMegabytes);
  return "";
}
async function upload(file: File) {
  if (locked.value) return;
  status.value = "";
  error.value = check(file);
  if (error.value) return;
  // The upload replaces a choice the keyboard was still resting on.
  dropChoice();
  const current = ++generation;
  dropLocal();
  local.value = URL.createObjectURL(file);
  busy.value = "upload";
  try {
    const cover = await client.value.upload(props.space.id, file);
    if (current !== generation) return;
    announce(cover);
    status.value = t.cover.stored;
  } catch (reason) {
    if (current === generation) error.value = failure(reason);
  } finally {
    if (current === generation) { busy.value = null; dropLocal(); }
  }
}
function picked(event: Event) {
  const target = event.target as HTMLInputElement;
  const file = target.files?.[0];
  target.value = "";
  if (file) void upload(file);
}
function dropped(event: DragEvent) {
  dragging.value = false;
  const file = event.dataTransfer?.files?.[0];
  if (file) void upload(file);
}
function dragOver(event: DragEvent) {
  if (locked.value || !event.dataTransfer?.types.includes("Files")) return;
  event.preventDefault();
  event.dataTransfer.dropEffect = "copy";
  dragging.value = true;
}
async function setTinted(value: boolean) {
  if (locked.value || stored.value.source !== "Upload") return;
  const current = ++generation;
  busy.value = "tint"; error.value = ""; status.value = "";
  try {
    const cover = await client.value.setTinted(props.space.id, value);
    if (current !== generation) return;
    announce(cover);
    status.value = value ? t.cover.tinted : t.cover.untinted;
  } catch (reason) {
    if (current !== generation) return;
    // Without an uploaded image the server answers 404. That proves only that this upload is gone: someone may have
    // chosen a drawing or none since, and the block cannot read the settings again, so it guesses no new state.
    if (reason instanceof ApiError && reason.status === 404) {
      gone.value = stored.value.version;
      error.value = t.cover.gone;
    } else error.value = failure(reason);
  } finally {
    if (current === generation) busy.value = null;
  }
}
</script>

<template>
  <div
    class="space-cover-field"
    :class="{ 'is-dragging': dragging }"
    :aria-busy="!!busy || sending || undefined"
    data-space-cover
    @dragenter="dragOver"
    @dragover="dragOver"
    @dragleave.self="dragging = false"
    @drop.prevent="dropped"
  >
    <div class="space-cover-main">
      <div class="space-cover-preview">
        <SpaceCard :space="{ ...space, cover: shown }" :cover-src="busy === 'upload' ? local : shown.source === 'Upload' ? imageSrc : undefined" preview aria-hidden="true" />
        <span v-if="dragging" class="space-cover-drop" aria-hidden="true"><AtlasIcon name="upload" :size="18" />{{ t.cover.drop }}</span>
      </div>
      <div class="space-cover-controls">
        <h4 :id="`${uid}-title`">{{ t.cover.title }}</h4>
        <p class="space-cover-help">{{ t.cover.help }} {{ deferred ? t.cover.helpDeferred : t.cover.helpSaved }}</p>
        <p class="space-cover-line" data-space-cover-line>{{ line }}<small v-if="shown.source === 'Auto' && shownName">{{ t.cover.byName }}</small></p>
        <div class="space-cover-actions">
          <Button variant="outline" size="sm" type="button" :disabled="off && !open" :aria-expanded="open" :aria-controls="`${uid}-gallery`" data-space-cover-pick @click="toggleGallery">
            <AtlasIcon :name="open ? 'chevron-up' : 'image'" :size="14" />{{ open ? t.cover.closePick : t.cover.pick }}
          </Button>
          <input :id="`${uid}-file`" ref="input" class="sr-only" type="file" :accept="accept" :disabled="locked" tabindex="-1" aria-hidden="true" data-space-cover-input @change="picked" />
          <Button variant="outline" size="sm" type="button" :disabled="locked" :aria-busy="busy === 'upload' || undefined" data-space-cover-upload @click="input?.click()">
            <AtlasIcon :name="busy === 'upload' ? 'loader-circle' : 'upload'" :size="14" :class="{ 'atlas-spinner': busy === 'upload' }" />{{ busy === "upload" ? t.cover.uploading : t.cover.upload }}
          </Button>
          <span v-if="shown.source !== 'Upload'" class="space-cover-hint">{{ t.cover.hint(maxMegabytes) }}</span>
        </div>
        <Transition name="space-cover-reveal">
          <label v-if="shown.source === 'Upload'" class="space-cover-switch">
            <Switch :model-value="stored.tinted" :disabled="locked || gone === stored.version" data-space-cover-tint @update:model-value="setTinted(!!$event)" />
            <span><span class="space-cover-switch-label">{{ t.cover.tint }}</span><small>{{ t.cover.tintHelp }}</small></span>
          </label>
        </Transition>
        <!-- One live region that is always there and only gets its text later: a status that arrives together with its
             text is not announced by every screen reader. -->
        <div class="space-cover-notes" role="status">
          <Transition :css="false" v-bind="disclosureHooks">
            <p v-if="held" class="space-cover-held"><AtlasIcon name="info" :size="14" />{{ t.cover.held }}</p>
          </Transition>
          <Transition :css="false" v-bind="disclosureHooks">
            <p v-if="!error && status && !deferred" class="space-cover-status"><AtlasIcon name="check" :size="14" />{{ status }}</p>
          </Transition>
        </div>
        <Transition :css="false" v-bind="disclosureHooks">
          <p v-if="error" class="space-cover-error" role="alert" data-space-cover-error><AtlasIcon name="triangle-alert" :size="14" />{{ error }}</p>
        </Transition>
      </div>
    </div>
    <!-- Mounted only while open, so no thumbnail is asked for before. -->
    <InlineReveal :id="`${uid}-gallery`" :open="open">
      <SpaceCoverGallery class="space-cover-choices" :space="space" :cover="shown" :auto-key="autoKey" :disabled="off || !!busy" @choose="chosen" @confirm="confirmed" @settled="settled" @keydown.esc="escaped" />
    </InlineReveal>
  </div>
</template>

<style scoped>
/* The block follows its own room (the settings page squeezed by the dock, a phone), not the screen. */
.space-cover-field { min-width: 0; container: space-cover / inline-size; }
.space-cover-main { display: flex; flex-wrap: wrap; gap: 20px; min-width: 0; }
.space-cover-preview { position: relative; flex: none; width: 280px; max-width: 100%; border-radius: 16px; }
.space-cover-preview::after { content: ""; position: absolute; inset: -4px; border: 2px dashed transparent; border-radius: 18px; pointer-events: none; transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.is-dragging .space-cover-preview::after { border-color: var(--accent); }
.space-cover-drop {
  position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; gap: 8px;
  border-radius: 14px; background: color-mix(in srgb, var(--surface) 82%, transparent); color: var(--accent); font-weight: 600;
  animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out);
}
.space-cover-controls { display: flex; flex: 1; flex-direction: column; gap: 10px; min-width: 260px; }
.space-cover-controls h4 { margin: 0; font-size: 14px; font-weight: 600; }
.space-cover-help { margin: 0; color: var(--muted); font-size: 13px; line-height: 1.45; }
.space-cover-line { margin: 0; font-size: 13.5px; font-weight: 500; line-height: 1.45; overflow-wrap: anywhere; }
.space-cover-line small { display: block; color: var(--muted); font-size: 12px; font-weight: 400; }
.space-cover-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.space-cover-hint { color: var(--muted); font-size: 12.5px; }
.space-cover-switch { display: flex; align-items: center; gap: 10px; cursor: pointer; }
.space-cover-switch > span { display: flex; flex-direction: column; line-height: 1.3; }
.space-cover-switch-label { font-weight: 500; }
.space-cover-switch small { color: var(--muted); font-size: 12px; }
.space-cover-error, .space-cover-status, .space-cover-held { display: flex; align-items: flex-start; gap: 6px; margin: 0; font-size: 13px; line-height: 1.45; }
.space-cover-error { color: var(--error); }
/* Unfolds and folds like the note above it (disclosureHooks) instead of fading in and vanishing in one frame. */
.space-cover-status { color: var(--success); }
.space-cover-error > svg, .space-cover-status > svg, .space-cover-held > svg { flex: none; margin-top: 2px; }
/* Always in the column, so it takes no room while it is empty: it gives the column's gap back and each of its lines
   brings that distance as its own padding, which unfolds with the line. */
.space-cover-notes { margin-top: -10px; }
.space-cover-notes > p { padding-top: 10px; }
.space-cover-reveal-enter-active { animation: st-rise-in var(--atlas-motion-base) var(--atlas-ease-out); }
.space-cover-reveal-leave-active { animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-out); }
/* A margin, not a gap: the closed disclosure takes no room. */
.space-cover-choices { margin-top: 16px; }
@container space-cover (max-width: 560px) {
  .space-cover-preview { width: 100%; }
  .space-cover-controls { min-width: 0; }
}
</style>
