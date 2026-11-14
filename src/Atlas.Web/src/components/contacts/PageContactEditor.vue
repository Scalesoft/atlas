<script lang="ts">
import t from "../../i18n/contacts-ui";
// Per contact, which panel stored the page's snapshot last: a panel's later second save never overwrites a newer panel's.
const generations = new Map<string, number>();
</script>

<script setup lang="ts">
import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { computed, nextTick, onBeforeUnmount, onMounted, onUnmounted, ref, shallowRef, useId } from "vue";
import { errorMessage } from "../../api";
import { contactApi, linkedSnapshot } from "../../contacts/contactApi";
import type { ContactData, ContactPatch } from "../../contacts/contactData";
import { liveObjects, type LiveObjects } from "../../objects/liveObjects";
import { usageApi, type UsageApi, type UsageCount } from "../../objects/relationsApi";
import FormField from "../FormField.vue";
import { Input } from "../ui/input";
import { Label } from "../ui/label";
import { RadioGroup, RadioGroupItem } from "../ui/radio-group";
import ContactCard from "./ContactCard.vue";
import { uploadContactPhoto } from "../../contacts/contactPhoto";

/**
 * A directory contact edited where a page shows it (the contacts plugin's `editLinked`): the live contact as ContactCard, each field
 * edited in place and saved to the contact at once (liveObjects, as the directory and a board's card do), so every page that holds
 * it shows the change; under it what is the page's own, its role, the person's login and how it reads in the text. The page's
 * snapshot is stored when the editor closes (storing repaints the card it is anchored to), at once, from the contact as shown; a
 * refusal the directory answers later stores it again, unless a newer panel of the contact stored it since. Without the right to
 * change the contact its fields only read and the page's own still edit.
 */
const props = withDefaults(defineProps<{
  snapshot: MarkdownContact; save: (contact: MarkdownContact) => void;
  store?: Pick<LiveObjects, "get" | "load" | "edit">; usage?: Pick<UsageApi, "count">; forget?: (id: string) => void;
}>(), { store: () => liveObjects, usage: () => usageApi, forget: (id: string) => void contactApi.forget(id) });

const id = props.snapshot.objectId!;
const kinds = [{ value: "auto", label: t.pageEditor.auto }, { value: "internal", label: t.pageEditor.internal }, { value: "external", label: t.pageEditor.external }] as const;
const uid = useId();
const object = computed(() => props.store.get(id));
const role = ref(props.snapshot.role ?? "");
const username = ref(props.snapshot.username ?? "");
const kind = ref<string>(props.snapshot.kind ?? "auto");
const failure = ref("");
const used = shallowRef<UsageCount | null>(null);
const root = ref<HTMLElement>();
let changed = false;
// The directory's answers to this panel's edits: a refusal among them stores the page's snapshot again, without the refused value.
const edits: Promise<unknown>[] = [];
let refused = false;

onMounted(async () => {
  void props.usage.count(id).then(count => { used.value = count; }, () => { /* The note is a courtesy. */ });
  await props.store.load(id);
  await nextTick();
  // The keyboard starts on the name when the contact may change, else on the page's role.
  root.value?.querySelector<HTMLElement>(object.value?.canEdit ? "[data-contact-field='name']" : "[data-page-contact-role]")?.focus();
});

async function change(patch: ContactPatch) {
  failure.value = "";
  // Shown at once (liveObjects): a panel closing meanwhile stores it too.
  changed = true;
  const edit = props.store.edit(id, patch);
  edits.push(edit.catch(() => {}));
  try { await edit; props.forget(id); }
  catch (error) { refused = true; failure.value = errorMessage(error); }
}

// A click outside closes the panel (the popover's document listener, after this window one): the open field is left first, so its
// blur saves it while the card is still here, as in the directory. Esc leaves the draft.
const outside = (event: Event) => {
  const active = document.activeElement;
  if (active instanceof HTMLElement && root.value?.contains(active) && !root.value.contains(event.target as Node)) active.blur();
};
onMounted(() => window.addEventListener("pointerdown", outside, true));
onBeforeUnmount(() => window.removeEventListener("pointerdown", outside, true));
// After the card: a Markdown field still open commits as its editor unmounts.
onUnmounted(() => {
  const own = { role: role.value.trim() || undefined, username: username.value.trim() || undefined, kind: kind.value === "auto" && !props.snapshot.kind ? undefined : kind.value as MarkdownContact["kind"] };
  if (!changed && own.role === props.snapshot.role && own.username === props.snapshot.username && own.kind === props.snapshot.kind) return;
  const { snapshot, store, save } = props;
  const storeSnapshot = () => {
    // The store's object (read again after a refusal); the panel's computed is gone with it.
    const now = store.get(id), data = now?.available ? now.data : undefined;
    // From the directory's fields when it has them, so a field cleared there leaves the page's snapshot too.
    const { name, internalUser, directoryId } = snapshot;
    save(linkedSnapshot({ ...(data ? { name, ...(internalUser && { internalUser }), ...(directoryId && { directoryId }) } : snapshot), ...own }, id, data));
  };
  // At once, while the page may still be edited (leaving edit mode right after a change destroys the editor).
  const generation = (generations.get(id) ?? 0) + 1;
  generations.set(id, generation);
  storeSnapshot();
  void Promise.all(edits).then(() => { if (refused && generations.get(id) === generation) storeSnapshot(); });
});

const elsewhere = computed(() => (used.value && used.value.total > 1 ? used.value.total - 1 : 0));
</script>

<template>
  <div ref="root" class="page-contact-editor" data-page-contact-editor>
    <p v-if="object && !object.available" class="page-contact-note">{{ t.pageEditor.gone }}</p>
    <template v-else-if="object?.data">
      <ContactCard :contact="object.data as unknown as ContactData" :editable="object.canEdit" :upload="file => uploadContactPhoto(id, file)" @change="change" />
      <p v-if="failure" class="page-contact-error" role="alert">{{ failure }}</p>
      <p class="page-contact-note">
        <template v-if="!object.canEdit">{{ t.pageEditor.readOnly }}</template>
        <template v-else-if="elsewhere">{{ t.pageEditor.everywhere(elsewhere) }}</template>
        <template v-else>{{ t.pageEditor.saved }}</template>
      </p>
    </template>
    <div v-else class="page-contact-loading" aria-busy="true" />
    <fieldset class="page-contact-own">
      <legend>{{ t.pageEditor.onPage }}</legend>
      <FormField :label="t.pageEditor.role"><Input v-model="role" maxlength="400" :placeholder="t.pageEditor.rolePlaceholder" data-page-contact-role /></FormField>
      <FormField :label="t.pageEditor.username"><Input v-model="username" maxlength="256" autocomplete="off" spellcheck="false" /></FormField>
      <RadioGroup v-model="kind" class="page-contact-kinds" :aria-label="t.pageEditor.display">
        <div v-for="option in kinds" :key="option.value" class="page-contact-kind">
          <RadioGroupItem :id="`${uid}-${option.value}`" :value="option.value" /><Label :for="`${uid}-${option.value}`">{{ option.label }}</Label>
        </div>
      </RadioGroup>
    </fieldset>
  </div>
</template>

<style scoped>
/* The panel (the editor's popover) sets the width; a width of its own overran the panel's padding and cut the fields. */
.page-contact-editor { display: grid; gap: 10px; min-width: 0; }
.page-contact-note { margin: 0; color: var(--muted); font-size: 12px; line-height: 1.5; }
.page-contact-error { margin: 0; color: var(--error); font-size: 12.5px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
/* The card's place while the contact loads, so nothing jumps. */
.page-contact-loading { height: 64px; border-radius: var(--atlas-radius-sm); background: var(--hover); }
.page-contact-own { display: grid; gap: 0; margin: 0; padding: 10px 0 0; border: 0; border-top: 1px solid var(--line); min-width: 0; }
.page-contact-own legend { padding: 0; margin-bottom: 8px; color: var(--muted); font-size: 12px; font-weight: 600; }
.page-contact-kinds { display: flex; flex-wrap: wrap; gap: 6px 14px; }
.page-contact-kind { display: inline-flex; align-items: center; gap: 6px; font-size: 13px; }
</style>
