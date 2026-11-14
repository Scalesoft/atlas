<script setup lang="ts">
import t from "../../i18n/contacts-ui";
import { computed, nextTick, onMounted, ref } from "vue";
import type { ContactData, ContactPatch } from "../../contacts/contactData";
import type { ContactAnswer, ContactDecision } from "../../contacts/contactLinking";
import AtlasIcon from "../AtlasIcon.vue";
import OptionPills from "../settings/OptionPills.vue";
import { Button } from "../ui/button";

/**
 * What a picked person's match asks before the person goes in (spec 2026-10-30 §7), inside the picker that asked (the editor's
 * ContactPicker, the board's BoardContactPicker), so the keyboard never leaves it: a contact the directory has („už je v adresáři a
 * používá se na …“, continue), a similar one („Použít existujícího“ / „Vytvořit nového“), or one whose fields differ (per field which
 * value holds; „nové“ only for who may change the contact). The primary action has the focus, Esc goes back (null).
 * Why not ConfirmDialog: a modal over the editor's popover would close the popover; this is the picker's own next step.
 */
const props = defineProps<{ decision: ContactDecision }>();
const emit = defineEmits<{ answer: [answer: ContactAnswer | null] }>();

const labels: Record<string, string> = { name: t.card.name, title: t.card.title, organization: t.card.organization, department: t.card.department, emails: t.card.emails, phones: t.card.phones, website: t.card.website, address: t.card.address, photo: t.match.photo };
const shown = (value: unknown) => (Array.isArray(value) ? value.join(", ") : String(value ?? ""));
const places = computed(() => (props.decision.kind === "weak" ? "" : props.decision.usedIn.map(place => place.title).join(", ")));
const held = ref<Record<string, string>>(props.decision.kind === "conflict" ? Object.fromEntries(props.decision.conflicts.map(conflict => [conflict.field, "existing"])) : {});
const root = ref<HTMLElement>();

/** What tells candidates apart; their number when none has more to say (so two rows never look the same). */
const detail = (candidate: { contact?: ContactData }, index: number) => candidate.contact?.organization || candidate.contact?.emails?.[0] || candidate.contact?.phones?.[0] || (props.decision.kind === "weak" && props.decision.candidates.length > 1 ? t.match.number(index + 1) : "");
function useExisting(contactId: string) {
  if (props.decision.kind !== "conflict") return emit("answer", { contactId });
  const fields: ContactPatch = Object.fromEntries(props.decision.conflicts.filter(conflict => held.value[conflict.field] === "new").map(conflict => [conflict.field, conflict.new]));
  emit("answer", { contactId, ...(Object.keys(fields).length && { fields }) });
}
function key(event: KeyboardEvent) {
  if (event.key !== "Escape") return;
  event.preventDefault();
  event.stopPropagation();
  emit("answer", null);
}
onMounted(() => nextTick(() => root.value?.querySelector<HTMLElement>("[data-contact-match-primary]")?.focus({ preventScroll: true })));
</script>

<template>
  <div ref="root" class="contact-match" role="group" :aria-label="t.match.title(decision.name)" :data-contact-match="decision.kind" @keydown="key">
    <template v-if="decision.kind === 'exists'">
      <p role="status"><AtlasIcon name="info" :size="15" /><span>{{ t.match.exists(decision.name, places) }}</span></p>
      <div class="contact-match-actions">
        <Button type="button" size="sm" data-contact-match-primary @click="useExisting(decision.contactId)">{{ t.match.continue }}</Button>
        <Button type="button" size="sm" variant="outline" @click="emit('answer', null)">{{ t.match.back }}</Button>
      </div>
    </template>
    <template v-else-if="decision.kind === 'weak'">
      <p role="status"><AtlasIcon name="users" :size="15" /><span>{{ decision.candidates.length === 1 ? t.match.similarOne : t.match.similarMany }}</span></p>
      <ul class="contact-match-candidates">
        <li v-for="(candidate, index) in decision.candidates" :key="candidate.contactId">
          <span>{{ candidate.name }}<small v-if="detail(candidate, index)"> · {{ detail(candidate, index) }}</small></span>
          <Button type="button" size="sm" :data-contact-match-primary="index === 0 || undefined" data-contact-match-use @click="useExisting(candidate.contactId)">{{ t.match.useExisting }}</Button>
        </li>
      </ul>
      <div class="contact-match-actions">
        <Button type="button" size="sm" variant="outline" data-contact-match-new @click="emit('answer', {})">{{ t.match.createNew }}</Button>
        <Button type="button" size="sm" variant="ghost" @click="emit('answer', null)">{{ t.match.back }}</Button>
      </div>
    </template>
    <template v-else>
      <p role="status"><AtlasIcon name="triangle-alert" :size="15" /><span>{{ t.match.conflict(decision.name, places) }}</span></p>
      <OptionPills v-for="conflict in decision.conflicts" :key="conflict.field" v-model="held[conflict.field]" :label="labels[conflict.field] ?? conflict.field" :data-contact-match-field="conflict.field"
        :options="[{ value: 'existing', label: t.match.existing(shown(conflict.existing)) }, { value: 'new', label: t.match.new(shown(conflict.new)), disabled: !decision.canEdit }]" />
      <p v-if="!decision.canEdit" class="contact-match-note">{{ t.match.readOnly }}</p>
      <div class="contact-match-actions">
        <Button type="button" size="sm" data-contact-match-primary @click="useExisting(decision.contactId)">{{ t.match.insertLink }}</Button>
        <Button type="button" size="sm" variant="outline" @click="emit('answer', null)">{{ t.match.back }}</Button>
      </div>
    </template>
  </div>
</template>

<style scoped>
.contact-match { display: grid; gap: 10px; padding: 10px 12px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface); font-size: 13.5px; animation: st-rise-in var(--atlas-motion-base) var(--atlas-ease-out); }
.contact-match p { display: flex; gap: 8px; align-items: flex-start; margin: 0; }
.contact-match p > :first-child { flex: none; margin-top: 2px; }
.contact-match-candidates { display: grid; gap: 6px; margin: 0; padding: 0; list-style: none; }
.contact-match-candidates li { display: flex; align-items: center; justify-content: space-between; gap: 10px; }
.contact-match-candidates small, .contact-match-note { color: var(--muted); }
.contact-match-actions { display: flex; flex-wrap: wrap; gap: 8px; }
</style>
