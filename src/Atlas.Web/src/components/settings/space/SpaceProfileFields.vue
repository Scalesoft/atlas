<script setup lang="ts">
import t from "../../../i18n/settings-space";
import { computed, nextTick, ref, shallowRef, watch, type ComponentPublicInstance } from "vue";
import { Input } from "../../ui/input";
import { Textarea } from "../../ui/textarea";
import type { SpacePinPolicy } from "../../../api";
import { fieldElement } from "../../../lib/templateRef";
import AppearancePicker from "../AppearancePicker.vue";
import OptionPills from "../OptionPills.vue";
import SpaceCoverField from "../../library/SpaceCoverField.vue";
import { bindSegmentedThumb } from "../../library/segmentedThumb";
import type { SpaceCover } from "../../../spaceCover";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import type { ChoiceOption } from "../types";

/**
 * What a space is: name, description, icon, colour, image and pin policy. One set of fields for Nový prostor
 * (CreateSpaceForm) and Nastavení prostoru → Obecné (SpaceGeneralPage), so both look and validate alike; each host
 * keeps its own saving. The image is no part of the draft: the field saves it at once in Obecné, or (`deferred`, Nový prostor) keeps the
 * choice and file until the space exists and reports them with `coverChange`.
 */
const props = defineProps<{
  spaceId: string; pageCount?: number; cover?: SpaceCover | null; deferred?: boolean; disabled?: boolean; nameError?: string;
}>();
const emit = defineEmits<{ coverChange: [cover: SpaceCover, file?: File] }>();
const name = defineModel<string>("name", { required: true });
const description = defineModel<string>("description", { required: true });
const icon = defineModel<string>("icon", { required: true });
const color = defineModel<string>("color", { required: true });
const pinPolicy = defineModel<SpacePinPolicy>("pinPolicy", { required: true });

// Whether the space shows in everyone's sidebar.
const pinPolicyOptions: ChoiceOption[] = [
  { value: "None", label: t.profile.pinNone, icon: "user" },
  { value: "Default", label: t.profile.pinDefault, icon: "pin" },
  { value: "Required", label: t.profile.pinRequired, icon: "lock" },
];
const pinChoice = computed({ get: () => pinPolicy.value, set: (value) => (pinPolicy.value = value as SpacePinPolicy) });
// The raised segment of the pin choice is one thumb that glides to the chosen option, like the other segmented switches
// (segmentedThumb.ts). OptionPills draws the track, so it is looked up once the switch is there.
const pinPills = ref<{ $el?: Element } | null>(null);
const pinTrack = shallowRef<HTMLElement | null>(null);
watch(pinPills, () => void nextTick(() => { pinTrack.value = pinPills.value?.$el?.querySelector?.<HTMLElement>(".option-pills-group") ?? null; }), { flush: "post" });
bindSegmentedThumb(pinTrack, '[data-state="checked"]', () => pinPolicy.value);

const previewSpace = computed(() => ({
  id: props.spaceId, name: name.value.trim() || t.profile.nameFallback, icon: icon.value, color: color.value,
  role: "Admin" as const, pageCount: props.pageCount ?? 0, lastChangedAt: null,
}));
const nameInput = ref<ComponentPublicInstance>();
defineExpose({ focusName: () => fieldElement(nameInput.value)?.focus() });
</script>

<template>
  <SettingsSection :title="t.profile.nameAndDescription" icon="pencil">
    <SettingsRow :label="t.profile.name" :help="t.profile.nameHelp" :error="nameError">
      <template #default="{ id, describedBy, invalid }">
        <Input :id="id" ref="nameInput" v-model="name" name="name" required maxlength="200" autocomplete="off" :placeholder="t.profile.namePlaceholder" :disabled="disabled" :aria-describedby="describedBy" :aria-invalid="invalid" data-space-name />
      </template>
    </SettingsRow>
    <SettingsRow :label="t.profile.description" :help="t.profile.descriptionHelp">
      <template #default="{ id, describedBy }">
        <Textarea :id="id" v-model="description" rows="3" maxlength="2000" :disabled="disabled" :aria-describedby="describedBy" data-space-description />
      </template>
    </SettingsRow>
  </SettingsSection>

  <SettingsSection :label="t.profile.appearance" class="space-appearance">
    <AppearancePicker v-model:icon="icon" v-model:color="color" :title="t.profile.appearance" :name="name" :icons-label="t.profile.icons" :disabled="disabled" />
    <div class="space-appearance-cover">
      <SpaceCoverField :space="previewSpace" :cover="cover" :match-input="{ name, description, icon }" :deferred="deferred" :disabled="disabled" @change="(value, file) => emit('coverChange', value, file)" />
    </div>
  </SettingsSection>

  <SettingsSection :label="t.profile.pin">
    <SettingsRow :label="t.profile.pin" :help="t.profile.pinHelp" :label-for="false">
      <OptionPills ref="pinPills" v-model="pinChoice" class="space-pin-policy" :options="pinPolicyOptions" :label="t.profile.pinLabel" hide-label data-space-pin-policy />
    </SettingsRow>
  </SettingsSection>
</template>

<style scoped>
/* Board 11: the look is one card, the image of the space under a quiet line; the pin choice is a segmented switch. */
.space-appearance :deep(.settings-section-body) { gap: 18px; }
.space-appearance-cover { margin: 0 -18px; padding: 16px 18px 0; border-top: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface)); }
/* Inline-size containment gives the element no width of its own, so it must ask for the whole control column. */
.space-pin-policy { width: 100%; container: space-pin-policy / inline-size; }
/* The thumb is concentric with the track: 10 px radius − 2 px padding. */
.space-pin-policy :deep(.option-pills-group) { --thumb-inset: 2px; --thumb-radius: 8px; --thumb-shadow: 0 1px 2px color-mix(in srgb, var(--text) 8%, transparent); flex: 1; flex-wrap: nowrap; gap: 2px; padding: 2px; border-radius: 10px; background: color-mix(in srgb, var(--line) 50%, var(--surface)); }
.space-pin-policy :deep(.option-pill) { flex: 1; justify-content: center; height: 32px; padding: 0 12px; border: 0; border-radius: 8px; background: transparent; color: var(--muted); font-size: 14px; white-space: nowrap; }
@media (hover: hover) and (pointer: fine) {
  .space-pin-policy :deep(.option-pill:hover:not([data-disabled])) { color: var(--text); }
}
.space-pin-policy :deep(.option-pill > svg) { color: currentColor; }
.space-pin-policy :deep(.option-pill[data-state="checked"]) { background: var(--surface); color: var(--text); font-weight: 600; box-shadow: 0 1px 2px color-mix(in srgb, var(--text) 8%, transparent); }
.space-pin-policy :deep(.option-pill[data-state="checked"] > svg) { color: currentColor; animation: none; }
/* While the thumb is drawn it carries the raised segment; without it (reduced motion) the chosen option fades its own. */
.space-pin-policy :deep(.option-pills-group[data-thumb] > .option-pill[data-state="checked"]) { background: transparent; box-shadow: none; }
/* Three segments need about 400 px; narrower than that (phone, a page squeezed by the dock) they wrap. */
@container space-pin-policy (max-width: 400px) {
  .space-pin-policy :deep(.option-pills-group) { flex-wrap: wrap; }
  /* The thumb glides along one row only: wrapped, each chosen option raises its own segment again. */
  .space-pin-policy :deep(.option-pills-group[data-thumb]::before) { display: none; }
  .space-pin-policy :deep(.option-pills-group[data-thumb] > .option-pill[data-state="checked"]) { background: var(--surface); box-shadow: 0 1px 2px color-mix(in srgb, var(--text) 8%, transparent); }
}
</style>
