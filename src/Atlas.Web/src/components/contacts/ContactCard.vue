<script setup lang="ts">
import t from "../../i18n/contacts-ui";
import { computed, nextTick, ref, useId, watch } from "vue";
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
import { drawContactQr } from "../../contacts/contactQr";
import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
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
 * The directory lists these, a board's object card shows one; the editor's own contacts block (a copy inside a page or on a board)
 * stays the editor's component: this one is the live directory contact.
 */
const props = withDefaults(defineProps<{ contact: ContactData; editable?: boolean; compact?: boolean; host?: BoardBlockHost;
  /** Uploads a chosen logo or photo (contactPhoto.ts) and answers the photo field; without it the card offers no upload. */
  upload?: (file: File) => Promise<string> }>(), { editable: false, compact: false, host: () => inertBlockHost, upload: undefined });
const emit = defineEmits<{ change: [patch: ContactPatch] }>();

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

const initials = computed(() => props.contact.name.replace(/[*_`~[\]()#>\\]/g, "").trim().split(/\s+/).slice(0, 2).map(word => word[0] ?? "").join("").toUpperCase());
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
// (the contacts plugin's contactQr: the whole card inside the code, no link to the wiki, so it works in a closed network).
const qrId = useId();
const qrOpen = ref(false);
const qr = ref<{ svg: string; omitted: string[] } | null>(null);
/** The card as a phone shows it: every field here is inline Markdown, a vCard carries its plain text. */
function plainContact(): MarkdownContact {
  const { name, title, organization, department, emails, phones, website, address } = props.contact;
  const plain = (value?: string) => plainTitle(value) || undefined;
  return { name: plainTitle(name), title: plain(title), organization: plain(organization), department: plain(department), emails, phones, website,
    address: address?.split("\n").map(line => plainTitle(line)).filter(Boolean).join("\n") || undefined };
}
async function downloadCard() {
  const { downloadContacts } = await import("../../contacts/pageContacts");
  downloadContacts([plainContact()]);
}
watch([qrOpen, () => props.contact], async () => {
  if (!qrOpen.value) return;
  try {
    const { contactQr } = await import("@atlas/markdown/plugins/contacts");
    const drawn = await contactQr(plainContact(), drawContactQr);
    qr.value = { svg: drawn.svg, omitted: drawn.omitted.map(field => labels[field === "website" ? "website" : "address"]) };
  } catch { qr.value = { svg: "", omitted: [] }; }
});
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
  <article ref="root" class="contact-card" :class="{ 'is-compact': compact, 'is-editable': editable }" data-contact-card @focusin="entered" @focusout="left" @keydown.capture="escapeCapture">
    <span class="contact-avatar" aria-hidden="true">{{ initials }}<img v-if="contact.photo && contact.photo !== failedPhoto" :src="contact.photo" alt="" loading="lazy" decoding="async"
      class="contact-photo" data-contact-photo @error="failedPhoto = contact.photo ?? ''"></span>
    <div class="contact-body">
      <div class="contact-head">
      <div class="contact-field contact-name" :class="{ 'is-editing': editing === 'name' }" :tabindex="editable && editing !== 'name' ? 0 : undefined"
        :role="editable && editing !== 'name' ? 'button' : undefined" :aria-label="editable && editing !== 'name' ? t.card.edit(labels.name) : undefined"
        data-contact-field="name" :data-md-own-escape="editing === 'name' || undefined" @click="clicked('name', $event)" @keydown.enter.self="start('name')" @keydown.space.self.prevent="start('name')">
        <BoardTextEditor v-if="editing === 'name'" :binding="binding('name')" :host="host" :entities="false" inline :max="maxContactLine" :aria-label="labels.name" :placeholder="t.card.name" @cancel="stop" />
        <BoardMarkdown v-else :key="contact.name" class="contact-value" :text="contact.name" inline :aria-label="labels.name" />
      </div>
      <button v-if="editable && !compact && !showEmpty && hasEmpty" type="button" class="contact-edit-link contact-reveal" :title="t.card.fill" data-contact-reveal
        @click="reveal"><AtlasIcon name="plus" :size="13" />{{ t.card.fillShort }}</button>
      <span v-if="!compact && contact.name" class="contact-share">
        <button type="button" class="contact-share-button" :title="t.card.vcf" :aria-label="t.card.vcf" data-contact-vcf @click="downloadCard"><AtlasIcon name="download" :size="15" /></button>
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
      <InlineReveal v-if="!compact" :id="qrId" v-model:open="qrOpen">
        <figure class="contact-qr" data-contact-qr-panel>
          <!-- uqr's drawing of the code (rects and a path, no text of the contact in it). -->
          <div v-if="qr?.svg" class="contact-qr-code" role="img" :aria-label="t.card.qr" v-html="qr.svg" />
          <div v-else class="contact-qr-code" aria-hidden="true" />
          <figcaption class="contact-qr-hint">{{ qr && !qr.svg ? t.card.qrFailed : t.card.qrHint }}<template v-if="qr?.omitted.length"> {{ t.card.qrOmitted(qr.omitted.join(", ")) }}</template></figcaption>
        </figure>
      </InlineReveal>
    </div>
  </article>
</template>

<style scoped>
.contact-card { display: flex; gap: 12px; min-width: 0; color: var(--text); container-type: inline-size; }
.contact-avatar { display: grid; flex: none; place-items: center; width: 36px; height: 36px; border-radius: 999px; background: var(--accent-soft); color: var(--accent); font-size: 13px; font-weight: 700; }
.is-compact .contact-avatar { width: 28px; height: 28px; font-size: 11px; }
.contact-avatar { position: relative; overflow: hidden; }
.contact-photo { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; background: var(--paper, #fff); }
.contact-body { display: grid; flex: 1; gap: 4px; min-width: 0; align-content: start; }
.contact-head { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 8px; min-width: 0; }
.contact-name { min-width: 0; font-size: 15px; font-weight: 600; }
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
.contact-share-button { display: inline-grid; place-items: center; width: 26px; height: 26px; padding: 0; border: 0; border-radius: 7px; background: transparent; color: var(--muted); cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .contact-share-button:hover { background: var(--hover); color: var(--text); } }
.contact-share-button:active { scale: var(--atlas-press); }
.contact-share-button[aria-expanded="true"] { background: var(--accent-soft); color: var(--accent); }
.contact-share-button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
/* A phone reads black modules on white only: the code keeps its own white, quiet zone included, in the dark theme too. */
.contact-qr { display: grid; justify-items: start; gap: 8px; margin: 10px 0 2px; }
.contact-qr-code { width: 216px; height: 216px; max-width: 100%; border-radius: var(--atlas-radius-md); overflow: hidden; background: #fff; box-shadow: 0 0 0 1px var(--line); }
.contact-qr-code :deep(svg) { width: 100%; height: 100%; }
.contact-qr-hint { max-width: 340px; color: var(--muted); font-size: 12.5px; line-height: 1.45; }
.contact-error { margin: 2px 0 0; color: var(--error); font-size: 12.5px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
</style>
