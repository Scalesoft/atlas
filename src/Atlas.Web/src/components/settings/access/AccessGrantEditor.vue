<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import AccessLevelPicker from "../AccessLevelPicker.vue";
import GrantValidityFields from "../GrantValidityFields.vue";
import { localDateTime, utcDateTime, validityError } from "../grantValidity";
import t from "../../../i18n/settings-access";
import { levelOptions } from "./accessKinds";
import type { AccessChangeDraft, AccessLevel, AccessSource } from "./accessModel";

/**
 * Changes one grant in place: the level on the kind's staircase (AccessLevelPicker) and its validity
 * (GrantValidityFields), then an explicit Uložit. It starts at the stored grant; unchanged bounds are sent back exactly as
 * the server gave them (utcDateTime). Store-free: `save` carries the change, the owner calls the API.
 */
const props = withDefaults(defineProps<{ kind: string; grant: AccessSource; name: string; busy?: boolean }>(), { busy: false });
const emit = defineEmits<{ save: [draft: AccessChangeDraft]; cancel: [] }>();

const levels = computed(() => levelOptions(props.kind));
const level = ref<string | null>(props.grant.level);
const startAt = ref(localDateTime(props.grant.startAt));
const expiresAt = ref(localDateTime(props.grant.expiresAt));
// Checked while typing; stored lapsed bounds left as they are pass, so a level change still saves.
const error = computed(() => validityError(startAt.value, expiresAt.value, props.grant));
const draft = computed<AccessChangeDraft>(() => ({
  level: (level.value ?? props.grant.level) as AccessLevel,
  startAt: utcDateTime(startAt.value, props.grant.startAt),
  expiresAt: utcDateTime(expiresAt.value, props.grant.expiresAt),
}));
const boundsChanged = computed(() => draft.value.startAt !== (props.grant.startAt ?? null) || draft.value.expiresAt !== (props.grant.expiresAt ?? null));
const changed = computed(() => draft.value.level !== props.grant.level || boundsChanged.value);
// Změnit opens the editor on the level chosen now; Esc is its Zrušit (UX cycle 5, AD5-9).
const root = ref<HTMLElement>();
onMounted(() => (root.value?.querySelector<HTMLElement>('[role="radio"][aria-checked="true"]') ?? root.value?.querySelector<HTMLElement>('[role="radio"]'))?.focus({ preventScroll: true }));
function escape(event: KeyboardEvent) {
  if (event.defaultPrevented || event.isComposing || props.busy) return;
  event.preventDefault();
  emit("cancel");
}
function save() {
  if (!props.busy && changed.value && !error.value) emit("save", draft.value);
}
</script>

<template>
  <div ref="root" class="access-grant-editor" data-access-editor @keydown.esc="escape">
    <AccessLevelPicker v-model="level" :options="levels" :label="t.editor.levelFor(name)" :disabled="busy" />
    <GrantValidityFields v-model:start-at="startAt" v-model:expires-at="expiresAt" :error="error" :disabled="busy" />
    <p v-if="kind === 'space' && grant.level === 'manage' && boundsChanged" class="st-note"><AtlasIcon name="triangle-alert" :size="15" />{{ t.editor.adminValidity }}</p>
    <div class="st-actions">
      <Button size="sm" type="button" :disabled="busy || !changed || !!error" data-save-access @click="save"><AtlasIcon :name="busy ? 'loader-circle' : 'check'" :size="15" :class="{ 'atlas-spinner': busy }" />{{ t.editor.save }}</Button>
      <Button variant="ghost" size="sm" type="button" :disabled="busy" @click="emit('cancel')">{{ t.editor.cancel }}</Button>
    </div>
  </div>
</template>

<style scoped>
.access-grant-editor { display: grid; gap: 14px; padding: 2px 0 14px; }
</style>
