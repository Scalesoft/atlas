<script setup lang="ts">
import t from "../../i18n/contacts-ui";
import { computed, nextTick, ref, useId } from "vue";
import { aliasesEdit, contactFieldEdit, maxContactAddress, maxContactLine, type ContactData, type ContactPatch } from "../../contacts/contactData";
import type { ContactFormValues } from "../../contacts/contactForm";
import { inertBlockHost, type BoardBlockHost } from "../../boards/editorBlocks";
import { errorMessage } from "../../api";
import BoardMarkdown from "../boards/text/BoardMarkdown.vue";
import BoardTextEditor from "../boards/text/BoardTextEditor.vue";
import type { TextBinding } from "../boards/text/textBinding";
import AtlasIcon from "../AtlasIcon.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import { plainTitle } from "../../objects/relationsApi";
import { markdownInitials } from "@atlas/markdown/core/userColor";
import { themedUserColor } from "../../live/collaboratorColors";
import { locale } from "../../i18n";
import { downloadCard } from "../../contacts/contactShare";
import { useContactQr } from "../../contacts/useContactQr";
import ContactQr from "./ContactQr.vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { Textarea } from "../ui/textarea";

/**
 * A contact of the directory as a card (objects spec §1a, §2.2): the name, function, organization and department as inline Markdown,
 * the e-mails, phones and website as links, the address as Markdown. `editable` makes each field editable in place: the Markdown
 * fields with the page editor (its inline variant for one line, BoardTextEditor), the identifiers in a plain field; Enter or a
 * click outside saves it, Esc leaves it unchanged; a field being edited is marked `data-md-own-escape`, so the editor's panel holding the card (PageContactEditor)
 * leaves that Esc to the field and closes on the next one. An edit is validated by the editor's rules (contactFieldEdit) and emitted as the change of that one field;
 * what is wrong is said under the card and nothing is sent. `compact` shows the name and the line under it only.
 * The empty fields wait until the card is being edited (progressive disclosure, UX1-5): they appear while the keyboard is inside the
 * card (Tab onto the name, then on into them) or after „Doplnit“, one small button instead of six placeholders on every row.
 * „Zkratky a přezdívky“ (aliases, one per line) are how people search for the person („MMu“, „Mára“); a pick in a people picker
 * after such a search adds one by itself (contactSources.ts `learnAlias`), here they are seen, corrected and removed.
 * The directory lists these, a board's object card shows one; the editor's contacts block draws the same contact in a page (D7: two
 * renderers, one look). A contact linked to an account (`directoryId`) is an internal person: the avatar on that user's colour, as the
 * editor's card and chip draw it.
 */
const props = withDefaults(defineProps<{ contact: ContactData; editable?: boolean; compact?: boolean; host?: BoardBlockHost;
  /** Uploads a chosen logo or photo (contactPhoto.ts) and answers the photo field; without it the card offers no upload. */
  upload?: (file: File) => Promise<string>;
  /** A card of a board, whose box the person sizes (ContactObjectCard): what it shows follows its room (small: the name and what the person does; medium: and the first e-mail and phone;
   *  large: everything), and the vCard and QR code are no icons on it but the board toolbar's (contactCardShare.ts), the code drawn in the card's place while `qr` is on. At rest only:
   *  an edited card shows every field and scrolls. */
  fit?: boolean; qr?: boolean }>(), { editable: false, compact: false, host: () => inertBlockHost, upload: undefined, fit: false, qr: false });
const emit = defineEmits<{ change: [patch: ContactPatch]; closeQr: [] }>();

// The aliases („Zkratky a přezdívky“, one per line) are the directory's own: no field of the editor's contact form.
type Field = keyof ContactFormValues | "aliases";
// The editor's rules, loaded once a contact is edited.
let rules: typeof import("@atlas/markdown/plugins/contacts").normalizeContact | undefined;
const loadRules = async () => (rules ??= (await import("@atlas/markdown/plugins/contacts")).normalizeContact);
const labels: Record<Field, string> = { name: t.card.name, title: t.card.title, organization: t.card.organization, department: t.card.department, emails: t.card.emails, phones: t.card.phones, website: t.card.website, address: t.card.address, aliases: t.card.aliases };
const inlineFields = ["title", "organization", "department"] as const;
const editing = ref<Field | null>(null);
const draft = ref("");
const error = ref("");
const root = ref<HTMLElement>();
const open = ref(false);
const showEmpty = computed(() => props.editable && !props.compact && (open.value || editing.value !== null));

const initials = computed(() => markdownInitials(props.contact.name, locale));
const tint = computed(() => (props.contact.directoryId ? { background: themedUserColor(props.contact.directoryId), color: "#fff" } : undefined));
const lines = computed(() => inlineFields.filter(field => props.contact[field] || showEmpty.value));
const hasLinks = computed(() => !!(props.contact.emails?.length || props.contact.phones?.length || props.contact.website || props.contact.aliases?.length));
const hasEmpty = computed(() => ([...inlineFields, "emails", "phones", "website", "address", "aliases"] as const).some(field => !text(field)));

function text(field: Field): string {
  const value = props.contact[field];
  return Array.isArray(value) ? value.join("\n") : value ?? "";
}
// BoardTextEditor ends on Esc by committing (a board's text keeps it): the commit made during this card's Esc is left out.
let escaping = false;
function escapeCapture(event: KeyboardEvent) {
  if (event.key !== "Escape" || event.isComposing) return;
  escaping = true;
  setTimeout(() => { escaping = false; });
}
function binding(field: Field): TextBinding {
  return { read: () => text(field), commit: markdown => { if (escaping && editing.value === field) stop(); else void save(field, markdown); } };
}
function start(field: Field) {
  if (!props.editable || editing.value) return;
  void loadRules();
  error.value = "";
  draft.value = text(field);
  editing.value = field;
  if (field === "emails" || field === "phones" || field === "website" || field === "aliases") void nextTick(() => root.value?.querySelector<HTMLElement>("[data-contact-input]")?.focus());
}
function stop(blur?: FocusEvent) {
  // Leaving a field with the keyboard (Esc, Enter) keeps the keyboard on that field, not on the page (keyboard pass); the focus
  // going to another control (the panel's role) stays there.
  const element = editing.value ? root.value?.querySelector<HTMLElement>(`[data-contact-field="${editing.value}"]`) : null;
  const active = document.activeElement;
  const returning = !!element && !blur?.relatedTarget && (element.contains(active) || !active || active === document.body);
  editing.value = null;
  if (returning) void nextTick(() => (element!.tabIndex === 0 ? element! : element!.querySelector<HTMLElement>(".contact-edit-link"))?.focus());
}
/** A click on a link follows it; anywhere else on an editable field starts editing it. */
function clicked(field: Field, event: MouseEvent) {
  if ((event.target as HTMLElement | null)?.closest("a")) return;
  start(field);
}
/** Once per editing: Enter and the blur of the field it then removes both end it. */
async function save(field: Field, value: string, blur?: FocusEvent) {
  if (editing.value !== field) return;
  stop(blur);
  // Loaded by start(): the change is emitted at once, also by the blur of a card being unmounted (a closing panel).
  const normalizeContact = rules ?? await loadRules();
  const result = field === "aliases" ? aliasesEdit(props.contact, value) : contactFieldEdit(props.contact, field, value, normalizeContact);
  if (result.patch === undefined) { error.value = result.error; return; }
  if (Object.keys(result.patch).length) emit("change", result.patch);
}
function key(field: Field, event: KeyboardEvent) {
  if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); stop(); return; }
  if (event.key === "Enter" && (field === "website" || event.ctrlKey || event.metaKey)) { event.preventDefault(); void save(field, draft.value); }
}
/** Focus coming into the card opens it; „Doplnit“ opens it itself, so its own focus does not remove it under the click. */
function entered(event: FocusEvent) {
  if (props.editable && !(event.target as HTMLElement).closest("[data-contact-reveal]")) open.value = true;
}
function left() {
  // After the focus has settled: a field swapping its editor for its value returns the focus inside (stop).
  setTimeout(() => { if (!editing.value && !root.value?.contains(document.activeElement)) open.value = false; });
}
async function reveal() {
  open.value = true;
  await nextTick();
  const field = root.value?.querySelector<HTMLElement>(".contact-field.is-empty");
  (field?.tabIndex === 0 ? field : field?.querySelector<HTMLElement>(".contact-edit-link"))?.focus();
}
// The card for a phone, as the editor's card of a person offers it: the .vcf to download, or its QR code unfolded at the bottom
// (ContactQr); a board's card (`fit`) shows the code in its own place instead, risen from the centre as everywhere else.
const qrId = useId();
const qrOpen = ref(false);
const place = useContactQr(() => props.contact, () => props.fit && props.qr, true);
const qrName = computed(() => plainTitle(props.contact.name));
const href = (kind: "mailto" | "tel", value: string) => `${kind}:${kind === "tel" ? value.replace(/[^\d+]/g, "") : value}`;

// The logo or photo (package G): over the initials, which stay when it fails to load.
const failedPhoto = ref("");
const uploading = ref(false);
/** The browser's file chooser, without a file field in the card. */
function choosePhoto() {
  const input = document.createElement("input");
  input.type = "file";
  input.accept = "image/png,image/jpeg,image/webp";
  input.onchange = () => void photoPicked(input.files?.[0]);
  input.click();
}
async function photoPicked(file: File | undefined) {
  if (!file || !props.upload) return;
  error.value = "";
  uploading.value = true;
  // The field the upload set is emitted too, so the card's holder shows it at once as after any edit.
  try { emit("change", { photo: await props.upload(file) }); }
  catch (failure) { error.value = errorMessage(failure); }
  finally { uploading.value = false; }
}
</script>

<template>
  <article ref="root" class="contact-card" :class="{ 'is-compact': compact, 'is-editable': editable, 'is-fit': fit, 'is-qr': fit && qr }" data-contact-card @focusin="entered" @focusout="left" @keydown.capture="escapeCapture">
    <span class="contact-avatar" aria-hidden="true" :style="tint" :data-contact-internal="contact.directoryId ? '' : undefined" :inert="fit && qr ? true : undefined">{{ initials }}<img v-if="contact.photo && contact.photo !== failedPhoto" :src="contact.photo" alt="" loading="lazy" decoding="async"
      class="contact-photo" data-contact-photo @error="failedPhoto = contact.photo ?? ''"></span>
    <div class="contact-body" :inert="fit && qr ? true : undefined">
      <div class="contact-head">
      <div class="contact-field contact-name" :class="{ 'is-editing': editing === 'name' }" :tabindex="editable && editing !== 'name' ? 0 : undefined"
        :role="editable && editing !== 'name' ? 'button' : undefined" :aria-label="editable && editing !== 'name' ? t.card.edit(labels.name) : undefined"
        data-contact-field="name" :data-md-own-escape="editing === 'name' || undefined" @click="clicked('name', $event)" @keydown.enter.self="start('name')" @keydown.space.self.prevent="start('name')">
        <BoardTextEditor v-if="editing === 'name'" :binding="binding('name')" :host="host" :entities="false" inline :max="maxContactLine" :aria-label="labels.name" :placeholder="t.card.name" @cancel="stop" />
        <BoardMarkdown v-else :key="contact.name" class="contact-value" :text="contact.name" inline :aria-label="labels.name" />
      </div>
      <button v-if="editable && !compact && !showEmpty && hasEmpty" type="button" class="contact-edit-link contact-reveal" :title="t.card.fill" data-contact-reveal
        @click="reveal"><AtlasIcon name="plus" :size="13" />{{ t.card.fillShort }}</button>
      <span v-if="!compact && !fit && contact.name" class="contact-share">
        <button type="button" class="contact-share-button" :title="t.card.vcf" :aria-label="t.card.vcf" data-contact-vcf @click="downloadCard(contact)"><AtlasIcon name="download" :size="15" /></button>
        <button type="button" class="contact-share-button" :title="t.card.qr" :aria-label="t.card.qr" :aria-expanded="qrOpen" :aria-controls="qrId" data-contact-qr
          @click="qrOpen = !qrOpen"><AtlasIcon name="qr-code" :size="15" /></button>
      </span>
      </div>
      <div v-if="lines.length" class="contact-lines">
        <div v-for="field in lines" :key="field" class="contact-field" :class="{ 'is-editing': editing === field, 'is-empty': !contact[field] }"
          :tabindex="editable && editing !== field ? 0 : undefined" :role="editable && editing !== field ? 'button' : undefined"
          :aria-label="editable && editing !== field ? t.card.edit(labels[field]) : undefined" :data-contact-field="field" :data-md-own-escape="editing === field || undefined" @click="clicked(field, $event)" @keydown.enter.self="start(field)" @keydown.space.self.prevent="start(field)">
          <BoardTextEditor v-if="editing === field" :binding="binding(field)" :host="host" :entities="false" inline :max="maxContactLine" :aria-label="labels[field]" :placeholder="labels[field]" @cancel="stop" />
          <BoardMarkdown v-else-if="contact[field]" :key="contact[field]" class="contact-value" :text="contact[field]!" inline :aria-label="labels[field]" />
          <span v-else class="contact-placeholder"><AtlasIcon name="plus" :size="13" />{{ labels[field] }}</span>
        </div>
      </div>
      <!-- What the holder shows right under the person's name and role (the page's contact editor: what the person knows), above the
           identifiers and the empty fields an edit reveals, so it stays in view. -->
      <slot name="summary" />
      <template v-if="!compact">
        <ul v-if="hasLinks || showEmpty" class="contact-links">
          <li v-for="field in (['emails', 'phones', 'website', 'aliases'] as const)" :key="field" class="contact-field" :class="{ 'is-editing': editing === field, 'is-empty': !text(field) }"
            :hidden="!showEmpty && !text(field)" :data-contact-field="field" :data-md-own-escape="editing === field || undefined" @click="clicked(field, $event)">
            <AtlasIcon :name="field === 'emails' ? 'mail' : field === 'phones' ? 'phone' : field === 'aliases' ? 'hash' : 'globe'" :size="14" class="contact-link-icon" />
            <template v-if="editing === field">
              <Input v-if="field === 'website'" v-model="draft" class="contact-input" inputmode="url" :aria-label="labels[field]" data-contact-input
                @keydown="key(field, $event)" @blur="save(field, draft, $event)" />
              <Textarea v-else v-model="draft" class="contact-input" rows="2" :aria-label="t.card.listHint(labels[field])" data-contact-input
                @keydown="key(field, $event)" @blur="save(field, draft, $event)" />
            </template>
            <span v-else :key="text(field)" class="contact-link-values">
              <template v-if="field === 'website' && contact.website"><a :href="contact.website" target="_blank" rel="noopener noreferrer">{{ contact.website.replace(/^https?:\/\//, "") }}</a></template>
              <template v-else-if="field === 'aliases'"><span v-for="alias in contact.aliases ?? []" :key="alias" class="contact-alias" :title="labels.aliases">{{ alias }}</span></template>
              <template v-else-if="field !== 'website'"><a v-for="value in contact[field] ?? []" :key="value" :href="href(field === 'emails' ? 'mailto' : 'tel', value)">{{ value }}</a></template>
              <button v-if="editable" type="button" class="contact-edit-link" :aria-label="t.card.edit(labels[field])" @click.stop="start(field)">
                <AtlasIcon :name="text(field) ? 'pencil' : 'plus'" :size="13" /><span v-if="!text(field)">{{ labels[field] }}</span>
              </button>
            </span>
          </li>
          <li v-if="editable && upload && (showEmpty || contact.photo)" class="contact-field" data-contact-field="photo">
            <AtlasIcon name="image" :size="14" class="contact-link-icon" />
            <span class="contact-link-values">
              <button type="button" class="contact-edit-link" :disabled="uploading" data-contact-photo-upload @click.stop="choosePhoto">
                <AtlasIcon :name="contact.photo ? 'pencil' : 'plus'" :size="13" /><span>{{ uploading ? t.card.uploading : contact.photo ? t.card.replacePhoto : t.card.addPhoto }}</span>
              </button>
              <button v-if="contact.photo" type="button" class="contact-edit-link" data-contact-photo-remove @click.stop="emit('change', { photo: null })">
                <AtlasIcon name="close" :size="13" /><span>{{ t.card.remove }}</span>
              </button>            </span>
          </li>
        </ul>
        <div v-if="contact.address || showEmpty" class="contact-field contact-address" :class="{ 'is-editing': editing === 'address', 'is-empty': !contact.address }"
          :tabindex="editable && editing !== 'address' ? 0 : undefined" :role="editable && editing !== 'address' ? 'button' : undefined"
          :aria-label="editable && editing !== 'address' ? t.card.edit(labels.address) : undefined" data-contact-field="address" :data-md-own-escape="editing === 'address' || undefined" @click="clicked('address', $event)" @keydown.enter.self="start('address')" @keydown.space.self.prevent="start('address')">
          <AtlasIcon name="map-pin" :size="14" class="contact-link-icon" />
          <BoardTextEditor v-if="editing === 'address'" :binding="binding('address')" :host="host" :entities="false" :max="maxContactAddress" :aria-label="labels.address" :placeholder="t.card.address" @cancel="stop" />
          <BoardMarkdown v-else-if="contact.address" :key="contact.address" class="contact-value" :text="contact.address" :aria-label="labels.address" />
          <span v-else class="contact-placeholder"><AtlasIcon name="plus" :size="13" />{{ labels.address }}</span>
        </div>
      </template>
      <p v-if="error" class="contact-error" role="alert" data-contact-error>{{ error }}</p>
      <InlineReveal v-if="!compact && !fit" :id="qrId" v-model:open="qrOpen">
        <ContactQr class="contact-qr-inline" :contact="contact" :active="qrOpen" />
      </InlineReveal>
    </div>
    <!-- A board's card shows its QR code in its own place, over its fields, sized by the card's short side (inert and unseen until then). -->
    <div v-if="fit" class="contact-qrview" :class="{ 'is-open': qr }" :inert="qr ? undefined : true" data-contact-qrview>
      <div class="contact-qrview-code"><div v-if="place.svg.value" :key="`qr-${place.run.value}`" class="contact-qr-svg" role="img" :aria-label="t.card.qr" v-html="place.svg.value" /></div>
      <div class="contact-qrview-side">
        <strong class="contact-qrview-name">{{ qrName }}</strong>
        <span class="contact-qrview-hint">{{ place.failed.value ? t.card.qrFailed : t.card.qrHint }}<template v-if="place.omitted.value.length"> {{ t.card.qrOmitted(place.omitted.value.join(", ")) }}</template></span>
        <Button variant="outline" size="xs" type="button" class="contact-qrview-back" :title="t.card.qrBackTitle" data-contact-qr-back @click="emit('closeQr')"><AtlasIcon name="arrow-left" :size="14" />{{ t.card.qrBack }}</Button>
      </div>
    </div>
  </article>
</template>

<style scoped>
.contact-card { display: flex; gap: 12px; min-width: 0; color: var(--text); container-type: inline-size; }
.contact-avatar { display: grid; flex: none; place-items: center; width: 36px; height: 36px; border-radius: 999px; background: var(--accent-soft); color: var(--accent-strong); font-size: 13px; font-weight: 650; letter-spacing: .02em; }
.is-compact .contact-avatar { width: 28px; height: 28px; font-size: 11px; }
.contact-avatar { position: relative; overflow: hidden; }
.contact-photo { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; background: var(--paper, #fff); }
.contact-body { display: grid; flex: 1; gap: 4px; min-width: 0; align-content: start; }
.contact-head { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 8px; min-width: 0; }
.contact-name { min-width: 0; font-size: 15px; font-weight: 650; }
.contact-reveal { animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.contact-lines { display: flex; flex-wrap: wrap; gap: 0 10px; color: var(--muted); font-size: 13px; }
.contact-lines > .contact-field { display: inline-flex; align-items: baseline; }
.contact-lines > .contact-field + .contact-field::before { content: "·"; margin-right: 10px; color: var(--muted); }
.contact-field { min-width: 0; border-radius: 6px; overflow-wrap: anywhere; }
.contact-field.is-editing { flex-basis: 100%; }
/* A changed value fades in where the old one was; nothing moves. */
.contact-value, .contact-link-values { display: block; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.contact-field :deep(.board-text-editor) { animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.is-editable .contact-field:not(.is-editing) { cursor: text; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .is-editable .contact-field:not(.is-editing):hover { background: var(--hover); } }
.contact-field:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.contact-placeholder { display: inline-flex; align-items: center; gap: 4px; color: var(--muted); font-size: 12.5px; }
.contact-links { display: grid; gap: 2px; margin: 4px 0 0; padding: 0; list-style: none; font-size: 13px; }
.contact-links > li { display: flex; align-items: flex-start; gap: 6px; }
.contact-links > li[hidden] { display: none; }
.contact-link-icon { flex: none; margin-top: 3px; color: var(--muted); }
.contact-link-values { display: flex; flex-wrap: wrap; align-items: center; gap: 2px 10px; min-width: 0; }
.contact-alias { padding: 0 6px; border-radius: 999px; background: var(--accent-soft); color: var(--text); font-size: 12px; }
.contact-link-values a { color: var(--accent); text-decoration: none; overflow-wrap: anywhere; }
@media (hover: hover) and (pointer: fine) { .contact-link-values a:hover { text-decoration: underline; } }
.contact-edit-link { display: inline-flex; align-items: center; gap: 4px; padding: 0 4px; border: 0; border-radius: 6px; background: transparent; color: var(--muted); font: inherit; font-size: 12.5px; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .contact-edit-link:hover { background: var(--hover); color: var(--text); } }
.contact-edit-link:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.contact-input { flex: 1; min-width: 0; animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.contact-address { display: flex; gap: 6px; font-size: 13px; }
.contact-share { display: inline-flex; flex: none; gap: 2px; margin-left: auto; align-self: center; }
/* Beats the editor popover's button reset (.md-context-popover-body button), which would pad and frame these icon buttons off-centre. */
.contact-card .contact-share-button { display: inline-grid; place-items: center; width: 26px; height: 26px; padding: 0; border: 0; border-radius: 7px; background: transparent; color: var(--muted); cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .contact-card .contact-share-button:hover { background: var(--hover); color: var(--text); } }
.contact-card .contact-share-button:active { scale: var(--atlas-press); }
.contact-card .contact-share-button[aria-expanded="true"] { background: var(--accent-soft); color: var(--accent); }
.contact-card .contact-share-button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.contact-qr-inline { margin: 10px 0 2px; }
/*
 * A board's card (fit): the card's own room decides what it shows (container queries on the card, so the sidebar or the dock being open
 * changes nothing), the name and surname always the largest thing on it. Small: name and what the person does, one line each. Medium: and
 * the first e-mail and phone. Large: everything. The corner on the right is the card's grip (ContactObjectCard): the name keeps clear of it.
 * Not while the card is edited: every field is there then and the card scrolls.
 */
/* Out of flow, filling its holder (which is positioned and has no padding of its own): a size container whose box the holder decides, never the other way round. */
.contact-card.is-fit { position: absolute; inset: 0; box-sizing: border-box; padding: 12px 14px; container: contact / size; }
.is-fit .contact-avatar { width: clamp(30px, 24cqh, 52px); height: clamp(30px, 24cqh, 52px); font-size: clamp(12px, 8.5cqh, 18px); }
.is-fit .contact-head { flex-wrap: nowrap; padding-right: 24px; }
.is-fit .contact-avatar { box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--accent) 16%, transparent); }
.is-fit .contact-name { flex: 1; font-size: clamp(16px, 11cqh, 26px); font-weight: 650; line-height: 1.22; letter-spacing: -.012em; }
/* What a larger room brings in arrives with a fade (the levels change as the card is resized); nothing already there moves. */
.is-fit:not(.is-editable) :is(.contact-lines, .contact-links, .contact-address) { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.is-fit:not(.is-editable) .contact-lines { overflow: hidden; max-height: 1.45em; }
.is-fit .contact-name { overflow-wrap: anywhere; }
.is-fit:not(.is-editable) :is(.contact-links, .contact-address) { display: none; }
@container contact (max-height: 79px) { .is-fit:not(.is-editable) .contact-lines { display: none; } }
@container contact (min-height: 110px) and ((max-height: 199px) or (max-width: 339px)) {
  .is-fit:not(.is-editable) .contact-links { display: grid; }
  .is-fit:not(.is-editable) .contact-links > li:not([data-contact-field="emails"], [data-contact-field="phones"]) { display: none; }
  .is-fit:not(.is-editable) .contact-link-values > a:nth-of-type(n + 2) { display: none; }
}
@container contact (min-height: 200px) and (min-width: 340px) {
  .is-fit:not(.is-editable) .contact-links { display: grid; }
  .is-fit:not(.is-editable) .contact-address { display: flex; }
  .is-fit .contact-body { gap: 6px; }
  /* What the person does, one thing a row without the separators of the one-line form; the ways to reach them under a hairline. */
  .is-fit:not(.is-editable) .contact-lines { display: grid; gap: 1px; margin-bottom: 4px; }
  .is-fit:not(.is-editable) .contact-lines > .contact-field + .contact-field::before { content: none; }
  .is-fit:not(.is-editable) .contact-links { padding-top: 8px; border-top: 1px solid var(--line); }
  .is-fit:not(.is-editable) .contact-lines { max-height: none; }
}
/* The QR code takes the card's place: the fields fade and settle back, the code comes in over them and rises from its centre (contactQr.css). */
.is-fit > :is(.contact-avatar, .contact-body) { transition: opacity var(--atlas-motion-base) var(--atlas-ease-out), scale var(--atlas-motion-base) var(--atlas-ease-out); }
.is-fit.is-qr > :is(.contact-avatar, .contact-body) { opacity: 0; scale: .98; }
.contact-qrview { position: absolute; inset: 0; display: flex; align-items: center; gap: 14px; opacity: 0; scale: .98; pointer-events: none;
  transition: opacity var(--atlas-motion-base) var(--atlas-ease-out), scale var(--atlas-motion-base) var(--atlas-ease-out); }
.contact-qrview.is-open { opacity: 1; scale: 1; pointer-events: auto; }
.contact-qrview-code { flex: none; width: min(100cqh, 50cqw); aspect-ratio: 1; border-radius: var(--atlas-radius-md); overflow: hidden; background: #fff; box-shadow: 0 0 0 1px var(--line);
  scale: .55; opacity: 0; transition: scale var(--atlas-motion-slow) var(--atlas-ease-out), opacity var(--atlas-motion-base) var(--atlas-ease-out); }
.is-open > .contact-qrview-code { scale: 1; opacity: 1; }
.contact-qrview-side { opacity: 0; translate: 0 var(--atlas-rise); transition: opacity var(--atlas-motion-base) var(--atlas-ease-out), translate var(--atlas-motion-base) var(--atlas-ease-out); }
.is-open > .contact-qrview-side { opacity: 1; translate: 0 0; transition-delay: calc(var(--atlas-motion-fast) * .6); }
.contact-qr-svg, .contact-qr-svg :deep(svg) { display: block; width: 100%; height: 100%; }
.contact-qrview-side { display: grid; gap: 4px; min-width: 0; align-content: center; justify-items: start; }
@media (prefers-reduced-motion: reduce) { .contact-qrview-code { scale: 1; } .contact-qrview-side { translate: none; } }
[data-atlas-reduced-motion] .contact-qrview-code { scale: 1; }
[data-atlas-reduced-motion] .contact-qrview-side { translate: none; }
.contact-qrview-name { max-width: 100%; font-size: 15px; font-weight: 650; line-height: 1.25; overflow-wrap: break-word; }
.contact-qrview-hint { color: var(--muted); font-size: 12px; line-height: 1.35; }
@container contact (max-height: 219px) { .contact-qrview-hint { display: none; } }
/* A card nearer a square than a wide one: the code over its caption. */
@container contact (max-aspect-ratio: 5/4) {
  .contact-qrview { flex-direction: column; justify-content: center; gap: 8px; }
  .contact-qrview-code { width: min(100cqw, calc(100cqh - 52px)); }
  .contact-qrview-side { justify-items: center; padding-right: 0; text-align: center; }
  .contact-qrview-name { font-size: 13px; max-height: 2.5em; overflow: hidden; }
  .contact-qrview-hint, .contact-qrview-back { display: none; }
}
.contact-error { margin: 2px 0 0; color: var(--error); font-size: 12.5px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
</style>
