<script setup lang="ts">
import t from "../../i18n/contacts-ui";
import { ref, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import FormField from "../FormField.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { Textarea } from "../ui/textarea";
import type { ContactFormErrors, ContactFormValues } from "../../contacts/contactForm";

/**
 * The fields of a new contact: the ones of the editor's contact component that a board stores (spec §4). Validation is
 * `validateContactForm`, the editor's own rules; this only shows its answer next to the field. Website and address are the
 * „Další údaje“, revealed on request and by themselves when one of them is wrong. The board's contact picker and the directory
 * (ContactDirectory) use it; `submitLabel` names what submitting does there.
 */
const values = defineModel<ContactFormValues>({ required: true });
const props = withDefaults(defineProps<{ errors: ContactFormErrors; busy?: boolean; submitLabel?: string }>(), { busy: false, submitLabel: t.formView.submit });
const emit = defineEmits<{ submit: [] }>();

const more = ref(false);
watch(() => props.errors, errors => { if (errors.website || errors.address) more.value = true; });
function update(key: keyof ContactFormValues, value: string | number) { values.value = { ...values.value, [key]: String(value) }; }
</script>

<template>
  <form class="contact-form" novalidate data-contact-form @submit.prevent="emit('submit')">
    <FormField class="wide" :label="t.formView.name" :error="errors.name" v-slot="{ describedBy }">
      <Input :model-value="values.name" autocomplete="off" required :disabled="busy" :aria-invalid="!!errors.name" :aria-describedby="describedBy" data-contact-name @update:model-value="update('name', $event)" />
    </FormField>
    <FormField :label="t.formView.title" :error="errors.title" v-slot="{ describedBy }">
      <Input :model-value="values.title" autocomplete="off" :disabled="busy" :aria-invalid="!!errors.title" :aria-describedby="describedBy" @update:model-value="update('title', $event)" />
    </FormField>
    <FormField :label="t.formView.organization" :error="errors.organization" v-slot="{ describedBy }">
      <Input :model-value="values.organization" autocomplete="off" :disabled="busy" :aria-invalid="!!errors.organization" :aria-describedby="describedBy" @update:model-value="update('organization', $event)" />
    </FormField>
    <FormField :label="t.formView.department" :error="errors.department" v-slot="{ describedBy }">
      <Input :model-value="values.department" autocomplete="off" :disabled="busy" :aria-invalid="!!errors.department" :aria-describedby="describedBy" @update:model-value="update('department', $event)" />
    </FormField>
    <FormField class="wide" :label="t.formView.emails" :error="errors.emails" v-slot="{ describedBy }">
      <Textarea :model-value="values.emails" rows="2" :disabled="busy" :aria-invalid="!!errors.emails" :aria-describedby="describedBy" data-contact-emails @update:model-value="update('emails', $event)" />
    </FormField>
    <FormField class="wide" :label="t.formView.phones" :error="errors.phones" v-slot="{ describedBy }">
      <Textarea :model-value="values.phones" rows="2" :disabled="busy" :aria-invalid="!!errors.phones" :aria-describedby="describedBy" data-contact-phones @update:model-value="update('phones', $event)" />
    </FormField>
    <div class="wide contact-form-more">
      <Button variant="ghost" size="sm" type="button" aria-controls="contact-form-more" :aria-expanded="more" data-contact-more @click="more = !more">
        <AtlasIcon :name="more ? 'chevron-up' : 'chevron-down'" :size="15" />{{ t.formView.more }}
      </Button>
      <InlineReveal id="contact-form-more" v-model:open="more">
        <div class="contact-form-grid">
          <FormField class="wide" :label="t.formView.website" :error="errors.website" v-slot="{ describedBy }">
            <Input :model-value="values.website" autocomplete="off" inputmode="url" :disabled="busy" :aria-invalid="!!errors.website" :aria-describedby="describedBy" data-contact-website @update:model-value="update('website', $event)" />
          </FormField>
          <FormField class="wide" :label="t.formView.address" :error="errors.address" v-slot="{ describedBy }">
            <Textarea :model-value="values.address" rows="2" :disabled="busy" :aria-invalid="!!errors.address" :aria-describedby="describedBy" @update:model-value="update('address', $event)" />
          </FormField>
        </div>
      </InlineReveal>
    </div>
    <div class="wide contact-form-actions">
      <Button type="submit" size="sm" :disabled="busy" data-contact-add><AtlasIcon name="plus" :size="15" />{{ submitLabel }}</Button>
    </div>
  </form>
</template>

<style scoped>
.contact-form, .contact-form-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 0 12px; min-width: 0; }
.contact-form :deep(label) { margin: 0 0 14px; }
.wide { grid-column: 1 / -1; }
.contact-form-more { margin: -4px 0 8px; }
.contact-form-more :deep(.inline-reveal-inner) { padding-top: 6px; }
.contact-form-actions { display: flex; justify-content: flex-end; }
</style>
