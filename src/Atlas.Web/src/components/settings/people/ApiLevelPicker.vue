<script setup lang="ts">
import t from "../../../i18n/settings-people";
import { computed } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { apiLevel, apiLevelInfo, apiLevelOrder, levelRank, type ApiLevel } from "../../../apiTokenRights";
import OptionPills from "../OptionPills.vue";
import "./people.css";

/**
 * The ceiling of an account's personal API (Bez API · Čtení · Zápis) with what the chosen level means and what a
 * change does to the tokens already issued: lowering trims their scopes at once and keeps them valid, Bez API closes them for
 * good. `saved` is the stored level the draft is compared with. Store-free: the caller saves (SaveBar) and confirms Bez API.
 */
const props = defineProps<{ saved: ApiLevel; disabled?: boolean }>();
const model = defineModel<ApiLevel>({ required: true });
const options = apiLevelOrder.map((level) => ({ value: level, label: apiLevelInfo[level].label, icon: apiLevelInfo[level].icon }));
const revoking = computed(() => model.value === "none" && props.saved !== "none");
const lowering = computed(() => !revoking.value && levelRank(model.value) < levelRank(props.saved));
</script>

<template>
  <div class="api-level">
    <OptionPills :model-value="model" :options="options" :label="t.api.level" data-api-access @update:model-value="!disabled && (model = apiLevel($event))" />
    <p class="st-note" data-api-level-help>
      <AtlasIcon :name="apiLevelInfo[model].icon" :size="15" />{{ apiLevelInfo[model].description }}{{ model === "none" ? "" : t.api.userManages }}
    </p>
    <p v-if="revoking" class="people-warning" role="note" data-api-revoke-warning>
      <AtlasIcon name="triangle-alert" :size="15" />{{ t.api.revokeWarning }}
    </p>
    <p v-else-if="lowering" class="people-warning" role="note" data-api-lower-warning>
      <AtlasIcon name="triangle-alert" :size="15" />{{ t.api.lowerWarning }}
    </p>
  </div>
</template>

<style scoped>
.api-level { display: grid; gap: 10px; min-width: 0; }
</style>
