import { computed, inject, onBeforeUnmount, onMounted, readonly, ref } from "vue";
import { api } from "../../../../api";
import { aiAdminTopic, liveConnectionSource, liveFeedSourceKey, type LiveFeedSource } from "../../../../live/liveFeed";
import t from "../../../../i18n/settings-ai";
import { isMissingEndpoint, type AiOverview, type AiProfileStatus, type AiTestResult, type ProfileKind } from "./aiAdmin";

// Shared only by mounted administration views. Nothing polls: the server tells the `ai-admin` feed of /hubs/live (AiHealthChanged,
// wiki administrators only, no payload) after a probe, a failure, a state change or a settings save. A metadata refresh never probes a model;
// probing costs tokens and happens solely through retry(kind), after an explicit click.
const overview = ref<AiOverview>();
const loading = ref(false);
const error = ref("");
const missing = ref(false);
const retrying = ref<ProfileKind | null>(null);
const result = ref<{ kind: ProfileKind; ok: boolean; code: string | null }>();
const failures = computed<AiProfileStatus[]>(() =>
  overview.value?.enabled ? overview.value.profiles.filter(profile => profile.state === "Unavailable") : []);
// Working profiles with a warning; quick tasks borrowing chat repeat chat's own, so they are left out.
const warnings = computed<AiProfileStatus[]>(() =>
  overview.value?.enabled ? overview.value.profiles.filter(profile => profile.warning && !profile.useChatProfile) : []);

let subscribers = 0;
let generation = 0;
let inFlight: Promise<void> | undefined;
let refreshController: AbortController | undefined;
let retryController: AbortController | undefined;

let closeFeed: (() => void) | undefined;

function refresh(): Promise<void> {
  if (!subscribers) return Promise.resolve();
  if (inFlight) return inFlight;
  const mine = generation;
  const controller = new AbortController();
  refreshController = controller;
  loading.value = true;
  inFlight = api<AiOverview>("/admin/ai", { signal: controller.signal })
    .then(next => {
      if (mine !== generation) return;
      overview.value = next;
      error.value = "";
      missing.value = false;
    })
    .catch(failure => {
      if (mine !== generation || controller.signal.aborted) return;
      if (isMissingEndpoint(failure)) { missing.value = true; error.value = ""; }
      else { missing.value = false; error.value = t.health.loadFailed; }
    })
    .finally(() => {
      if (mine !== generation) return;
      inFlight = undefined;
      refreshController = undefined;
      loading.value = false;
    });
  return inFlight;
}

async function retry(kind: ProfileKind): Promise<void> {
  if (!subscribers || retrying.value) return;
  const mine = generation;
  const controller = new AbortController();
  retryController = controller;
  retrying.value = kind;
  result.value = undefined;
  try {
    const source = overview.value?.profiles.find(profile => profile.kind === kind)?.useChatProfile ? "chat" : kind;
    const tested = await api<AiTestResult>(`/admin/ai/profiles/${source}/test`, { method: "POST", signal: controller.signal });
    if (mine !== generation) return;
    result.value = { kind, ok: tested.ok, code: tested.code };
  } catch {
    if (mine !== generation || controller.signal.aborted) return;
    result.value = { kind, ok: false, code: null };
  } finally {
    if (mine !== generation) return;
    retryController = undefined;
    // An earlier metadata request may predate the test. Finish it before fetching fresh status.
    if (inFlight) await inFlight;
    if (mine === generation) {
      await refresh();
      if (mine === generation) retrying.value = null;
    }
  }
}

function subscribe(source: LiveFeedSource) {
  subscribers++;
  if (subscribers !== 1) return;
  // One feed for all mounted consumers; each (re)subscription and each signal reads once (shared with a read in flight).
  const read = () => { void refresh(); };
  closeFeed = source(aiAdminTopic, { message: read, subscribed: read, unavailable: () => {} });
  void refresh();
}
function release() {
  if (!subscribers || --subscribers) return;
  generation++;
  closeFeed?.(); closeFeed = undefined;
  refreshController?.abort(); refreshController = undefined;
  retryController?.abort(); retryController = undefined;
  inFlight = undefined;
  overview.value = undefined;
  loading.value = false;
  error.value = "";
  missing.value = false;
  retrying.value = null;
  result.value = undefined;
}

/** Administration-only overview state with one live feed across all mounted consumers (the gallery and tests provide a fake one). */
export function useAiAdminHealth() {
  const source = inject(liveFeedSourceKey, null) ?? liveConnectionSource();
  onMounted(() => subscribe(source));
  onBeforeUnmount(release);
  return {
    overview: readonly(overview), failures, warnings, loading: readonly(loading), error: readonly(error),
    missing: readonly(missing), retrying: readonly(retrying), result: readonly(result),
    refresh, retry, dismissResult: () => { result.value = undefined; },
  };
}
