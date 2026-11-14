<script setup lang="ts">
import { locale } from "../../../i18n";
import t from "../../../i18n/settings-security";
import { computed, onMounted, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { reauthApi, type FreshAuthProvider, type FreshAuthState, type ReauthStatus } from "../../../protection";
import { formatDateTime } from "../../../formatDate";
import { disclosureHooks } from "../disclosure";
import StatusBadge from "../StatusBadge.vue";
import type { StatusTone } from "../types";
import { runFreshAuthSelfTest } from "./apiReauth";

/**
 * "Otestovat nové přihlášení" (wiki administrator): signs in again at the company provider (Hub) in a popup, releases nothing
 * and shows the verdict: whether the provider proved a fresh login (auth_time), per configured provider. A local Atlas password
 * always proves it. `run` is injectable for the gallery; the page passes the V4 API.
 */
const props = withDefaults(
  defineProps<{
    run?: (providerId?: string) => ReturnType<typeof runFreshAuthSelfTest>;
    providers?: () => Promise<FreshAuthProvider[]>;
    /** Whether the caller's own sign-in can prove freshness (security/me). */
    freshAuth?: FreshAuthState | null;
  }>(),
  { run: (providerId?: string) => runFreshAuthSelfTest(providerId), providers: () => reauthApi.selfTests(), freshAuth: null },
);
const list = ref<FreshAuthProvider[]>([]);
const running = ref(false);
const status = ref<ReauthStatus | null>(null);
const error = ref("");
const tested = ref(false);
onMounted(async () => {
  list.value = await props.providers().catch(() => []);
});

const verdicts: Record<FreshAuthState, { label: string; tone: StatusTone; icon: string }> = {
  supported: { label: t.selfTest.supported, tone: "success", icon: "circle-check" },
  unknown: { label: t.selfTest.unknown, tone: "info", icon: "hourglass" },
  unsupported: { label: t.selfTest.unsupported, tone: "danger", icon: "shield-alert" },
};
const verdict = computed(() => {
  if (!tested.value) return undefined;
  if (error.value) return { tone: "warning" as StatusTone, icon: "triangle-alert", text: error.value };
  if (status.value?.state === "verified") return { tone: "success" as StatusTone, icon: "circle-check", text: t.selfTest.verified };
  if (status.value?.failure === "auth_time_missing") return { tone: "danger" as StatusTone, icon: "shield-alert", text: t.selfTest.missing };
  if (status.value?.failure === "auth_time_stale") return { tone: "danger" as StatusTone, icon: "shield-alert", text: t.selfTest.stale };
  return { tone: "warning" as StatusTone, icon: "triangle-alert", text: t.selfTest.notFinished };
});
async function test() {
  running.value = true;
  error.value = "";
  status.value = null;
  try {
    const result = await props.run();
    status.value = result.status;
    error.value = result.error;
    if (result.providers.length) list.value = result.providers;
  } finally {
    running.value = false;
    tested.value = true;
  }
}
</script>

<template>
  <article class="st-card fresh-auth-test" data-fresh-auth-test>
    <div class="fresh-auth-head">
      <span class="st-icon-tile"><AtlasIcon name="fingerprint" :size="20" /></span>
      <div>
        <strong>{{ t.selfTest.title }}</strong>
        <small>{{ t.selfTest.description }}</small>
      </div>
      <StatusBadge v-if="freshAuth" v-bind="verdicts[freshAuth]" :label="t.selfTest.yourAccount(verdicts[freshAuth].label.toLocaleLowerCase(locale))" size="sm" />
    </div>
    <ul v-if="list.length" class="fresh-auth-providers" :aria-label="t.selfTest.providers">
      <li v-for="provider in list" :key="provider.providerId" :data-provider-verdict="provider.providerId">
        <AtlasIcon name="shield-check" :size="15" /><span>{{ provider.displayName }}</span>
        <StatusBadge v-bind="verdicts[provider.verdict]" size="sm" />
        <small v-if="provider.checkedAt">{{ provider.selfTest ? t.selfTest.testWord : t.selfTest.signInWord }} {{ formatDateTime(provider.checkedAt) }}</small>
      </li>
    </ul>
    <p v-else class="st-note"><AtlasIcon name="info" :size="15" />{{ t.selfTest.noProvider }}</p>
    <Transition :css="false" v-bind="disclosureHooks">
      <p v-if="verdict" class="fresh-auth-verdict" :data-tone="verdict.tone" role="status" data-fresh-auth-verdict><AtlasIcon :name="verdict.icon" :size="16" />{{ verdict.text }}</p>
    </Transition>
    <div class="st-actions">
      <Button variant="outline" size="sm" type="button" :disabled="running || !list.length" data-fresh-auth-run @click="test">
        <AtlasIcon :name="running ? 'loader-circle' : 'external'" :size="15" :class="{ 'atlas-spinner': running }" />{{ running ? t.selfTest.waiting : t.selfTest.title }}
      </Button>
    </div>
  </article>
</template>

<style scoped>
/* A column and not a grid: the verdict unfolds (disclosureHooks) and takes its gap with it, a grid track cannot. */
.fresh-auth-test { display: flex; flex-direction: column; gap: 12px; padding: 16px; min-width: 0; }
.fresh-auth-test > * { min-width: 0; }
.fresh-auth-head { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 10px 12px; }
.fresh-auth-head > div { display: grid; gap: 3px; flex: 1 1 220px; min-width: 0; }
.fresh-auth-head strong { font-size: 14.5px; }
.fresh-auth-head small { color: var(--muted); font-size: 12.5px; line-height: 1.5; }
.fresh-auth-providers { display: grid; gap: 6px; margin: 0; padding: 0; list-style: none; font-size: 13px; }
.fresh-auth-providers li { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 8px; }
.fresh-auth-providers small { color: var(--muted); font-size: 12px; }
.fresh-auth-verdict { display: flex; align-items: flex-start; gap: 8px; margin: 0; padding: 10px 12px; border-radius: var(--st-radius-sm); font-size: 13px; font-weight: 550; line-height: 1.5; background: var(--hover); }
.fresh-auth-verdict > svg { flex-shrink: 0; margin-top: 2px; }
.fresh-auth-verdict[data-tone="success"] { color: var(--success); background: var(--success-bg); }
.fresh-auth-verdict[data-tone="danger"] { color: var(--error); background: var(--error-bg); }
.fresh-auth-verdict[data-tone="warning"] { color: var(--warning); background: var(--warning-bg); }
</style>
