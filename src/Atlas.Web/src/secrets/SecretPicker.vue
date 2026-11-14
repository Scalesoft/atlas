<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import "../components/settings/settings.css";
import OptionPills from "../components/settings/OptionPills.vue";
import SearchableChoiceList from "../components/settings/SearchableChoiceList.vue";
import type { ChoiceOption } from "../components/settings/types";
import { errorMessage } from "../api";
import { vaultAccessLevelLabels } from "../security";
import t from "../i18n/secrets-picker";
import { secretsApi, type SecretReferenceView, type SecretsApi } from "./secretsApi";

/**
 * Host picker of the secrets plugin (the plugin has none), the content of an editor popover (openVuePopover, as the other in-editor
 * pickers: Esc and focus return belong to the popover; the list preselects the first result and Enter picks it): searches only the secrets the reader may Discover
 * (GET /api/secrets/search, the page's space) and inserts the chosen one as a card or inside the sentence. A reference
 * grants nothing: who may reveal it is decided on the server at release time.
 *
 * `mode="replace"` is „Přesunout do trezoru“ (plan §12 H.5, decision O2): a password typed into the page is replaced by
 * an inline reference. The author stores it in KeePass first and picks it here; the dialog never receives the value
 * (`initialQuery` holds the words around it) and always inserts inline, in place of the text.
 *
 * `mode="swap"` is „Vyměnit heslo“ on a reference already in the page: `currentId` is the secret it points to (marked in
 * the list) and `initialQuery` its KeePass group or title; the reference keeps its layout and display settings.
 */
// `close` is the popover's own (openVuePopover passes it to every component); the host closes it once the answer is in.
const props = withDefaults(defineProps<{ open: boolean; spaceId: string; api?: SecretsApi; mode?: "pick" | "replace" | "swap"; initialQuery?: string; currentId?: string; close?: () => void }>(),
  { api: () => secretsApi, mode: "pick", initialQuery: "", close: undefined });
const emit = defineEmits<{ insert: [reference: { secretId: string; inline: boolean; vaultName?: string }] }>();
const replacing = computed(() => props.mode === "replace");
const query = ref("");
const layout = ref("block");
const results = ref<SecretReferenceView[]>([]);
const loading = ref(false);
const failure = ref("");
let search: AbortController | undefined;
let timer: ReturnType<typeof setTimeout> | undefined;

const layouts = computed<ChoiceOption[]>(() => [
  { value: "block", label: t.picker.block, icon: "key-round" },
  { value: "inline", label: t.picker.inline, icon: "type" },
]);
const options = computed<ChoiceOption[]>(() => results.value.map(secret => ({
  value: secret.id,
  label: secret.title || t.picker.untitled,
  description: [secret.vaultName, secret.path].filter(Boolean).join(" · "),
  icon: "key-round",
  badge: vaultAccessLevelLabels[secret.level],
})));

let searched: string | undefined;
function run(text: string) {
  searched = text.trim();
  search?.abort();
  const controller = (search = new AbortController());
  loading.value = true;
  failure.value = "";
  props.api.search(text.trim(), props.spaceId, controller.signal).then(found => {
    if (search === controller) results.value = found;
  }, error => {
    if (search === controller && !controller.signal.aborted) failure.value = errorMessage(error);
  }).finally(() => {
    if (search === controller) loading.value = false;
  });
}
watch(query, text => {
  clearTimeout(timer);
  // Opening the dialog already searched for its initial text.
  if (text.trim() === searched) return;
  timer = setTimeout(() => run(text), 200);
});
watch(() => props.open, open => {
  if (open) {
    const initial = props.initialQuery.trim();
    query.value = initial;
    results.value = [];
    run(initial);
  }
  else { clearTimeout(timer); search?.abort(); }
}, { immediate: true });
onBeforeUnmount(() => { clearTimeout(timer); search?.abort(); });

function pick(secretId: string) {
  const vaultName = results.value.find(secret => secret.id === secretId)?.vaultName;
  emit("insert", { secretId, inline: replacing.value || layout.value === "inline", ...(vaultName ? { vaultName } : {}) });
}
</script>

<template>
  <div class="secret-picker settings-ui" :data-secret-picker-mode="mode">
      <p v-if="replacing" class="secret-picker-hint">{{ t.picker.hintReplace }}</p>
      <p v-else-if="mode === 'swap'" class="secret-picker-hint">{{ t.picker.hintSwap }}</p>
      <p v-else class="secret-picker-hint">{{ t.picker.hintPick }}</p>
      <OptionPills v-if="mode === 'pick'" v-model="layout" :label="t.picker.layout" :options="layouts" />
      <SearchableChoiceList
        v-model:query="query"
        remote
        :model-value="currentId ?? null"
        :options="options"
        :label="t.picker.list"
        :placeholder="t.picker.search"
        :empty-text="loading ? t.picker.searching : t.picker.none"
        @pick="pick"
      />
      <p v-if="failure" class="notice error" role="alert">{{ failure }}</p>
  </div>
</template>

<style scoped>
.secret-picker { display: grid; gap: 14px; min-width: 0; }
.secret-picker-hint { margin: 0; color: var(--muted); font-size: 13px; line-height: 1.5; }
</style>
