<script setup lang="ts">
import t from "../../../i18n/settings-people";
import { computed, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { disclosureHooks } from "../disclosure";
import { Button } from "../../ui/button";
import type { Group } from "../../../administration";
import SettingsRow from "../SettingsRow.vue";
import ProgressiveFlow from "../ProgressiveFlow.vue";
import SummaryList from "../SummaryList.vue";
import type { FlowStep, SummaryItem } from "../types";

/**
 * Nová skupina: 1 Název · 2 Souhrn · 3 Hotovo. Members are added in the group's detail, so the last
 * step offers to go there (`open`) or back to the list (`done`). Store-free: `submit` creates the
 * group and resolves with it, or rejects with the reason shown on the summary; `takenNames` catches
 * a duplicate name early.
 */
const props = withDefaults(defineProps<{ submit: (name: string) => Promise<Group>; takenNames?: string[] }>(), { takenNames: () => [] });
const emit = defineEmits<{ done: [group: Group]; open: [group: Group]; cancel: []; dirty: [value: boolean] }>();

const name = ref("");
const step = ref("name");
const busy = ref(false);
const failure = ref("");
const created = ref<Group>();

watch(name, (value) => {
  emit("dirty", !created.value && !!value.trim());
});
watch(step, () => (failure.value = ""));
const taken = computed(() => new Set(props.takenNames.map((item) => item.trim().toLocaleLowerCase("cs"))));
const nameError = computed(() => taken.value.has(name.value.trim().toLocaleLowerCase("cs")) ? t.createGroup.nameTaken : "");

const steps = computed<FlowStep[]>(() => [
  {
    id: "name",
    label: t.createGroup.nameStep,
    icon: "users",
    title: t.createGroup.nameTitle,
    hint: t.createGroup.nameHint,
    summary: name.value.trim() || undefined,
    ready: !!name.value.trim() && !nameError.value,
  },
  { id: "summary", label: t.common.summary, icon: "circle-check", title: t.createGroup.summaryTitle, hint: t.createGroup.summaryHint, manual: true, nextLabel: t.createGroup.create, nextIcon: "check" },
  { id: "done", label: t.createGroup.doneStep, icon: "check", title: t.createGroup.doneTitle, hint: t.createGroup.doneHint, final: true },
]);
const summary = computed<SummaryItem[]>(() => [
  { key: "name", icon: "users", label: t.common.name, value: name.value.trim() },
  { key: "source", icon: "home", label: t.createGroup.kind, value: t.common.localGroup },
  { key: "members", icon: "account", label: t.createGroup.members, value: t.createGroup.addInDetail },
]);

async function onNext(current: FlowStep) {
  if (current.id !== "summary" || busy.value) return;
  busy.value = true;
  failure.value = "";
  try {
    created.value = await props.submit(name.value.trim());
    emit("dirty", false);
    step.value = "done";
  } catch (reason) {
    failure.value = reason instanceof Error ? reason.message : t.createGroup.failed;
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <ProgressiveFlow autofocus v-model="step" :steps="steps" :label="t.createGroup.label" :context="t.createGroup.context" :busy="busy" @next="onNext" @cancel="emit('cancel')">
    <template #name>
      <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.createGroup.nameLabel" :error="nameError || undefined">
        <input :id="id" v-model="name" class="st-input" maxlength="100" autocomplete="off" :aria-describedby="describedBy" :aria-invalid="invalid" data-step-focus data-group-name />
      </SettingsRow>
    </template>
    <template #summary>
      <SummaryList :items="summary" :label="t.createGroup.summaryLabel" />
      <Transition :css="false" v-bind="disclosureHooks"><p v-if="busy" class="group-status" role="status"><AtlasIcon name="loader-circle" :size="16" class="atlas-spinner" />{{ t.createGroup.creating }}</p></Transition>
      <Transition :css="false" v-bind="disclosureHooks">
        <p v-if="failure" class="group-failure" role="alert" data-group-error><AtlasIcon name="triangle-alert" :size="15" />{{ failure }}</p>
      </Transition>
    </template>
    <template #done>
      <p class="group-status" role="status" data-group-created><AtlasIcon name="circle-check" :size="16" />{{ t.createGroup.createdLead }} <strong>{{ created?.name }}</strong> {{ t.createGroup.createdTail }}</p>
      <div class="st-actions">
        <Button type="button" data-group-open @click="created && emit('open', created)"><AtlasIcon name="plus" :size="16" />{{ t.createGroup.addMembers }}</Button>
        <Button variant="outline" type="button" data-group-done @click="created && emit('done', created)"><AtlasIcon name="back" :size="16" />{{ t.createGroup.back }}</Button>
      </div>
    </template>
  </ProgressiveFlow>
</template>

<style scoped>
.group-status { display: flex; align-items: center; gap: 8px; margin: 0; color: var(--muted); font-size: 13px; }
.group-status strong { color: var(--text); }
.group-failure { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
</style>
