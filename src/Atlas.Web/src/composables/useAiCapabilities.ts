import { computed, readonly, ref } from "vue";
import { fetchAiCapabilities, noAiCapabilities, onAiUnavailable, type AiCapabilities } from "../ai/capabilities";
import { aiTopic, liveConnectionSource, type LiveFeedSource } from "../live/liveFeed";

// One shared state for the whole app. Everything AI in the UI is `v-if` on a flag from here; an outage only turns flags
// off (AI-25), so there is deliberately no error state, banner or message. Nothing polls: the server tells the `ai` feed of
// /hubs/live (AiChanged, no payload) when what it answers may have changed (a profile became available or unavailable after its
// probe, the settings changed), each (re)subscription reads once, and a call refused as ai_unavailable reads at once.
const state = ref<AiCapabilities>(noAiCapabilities());
const any = computed(() => Object.values(state.value).some(Boolean));

let started = false;
let generation = 0;
let inFlight: Promise<void> | undefined;
let again = false;
let controller: AbortController | undefined;
let unsubscribe: (() => void) | undefined;
let closeFeed: (() => void) | undefined;

/** Loads the capabilities. Calls made while a request is running share it; any number of them cost at most one more request. */
function refresh(): Promise<void> {
  if (inFlight) { again = true; return inFlight; }
  const mine = generation;
  controller = new AbortController();
  inFlight = fetchAiCapabilities(controller.signal)
    .catch(noAiCapabilities)
    .then((next) => { if (mine === generation) state.value = next; })
    .finally(() => {
      inFlight = undefined;
      if (again && started && mine === generation) { again = false; void refresh(); }
      else again = false;
    });
  return inFlight;
}

/** Starts loading with the session (idempotent). Call after the user is known; `source` stands in for /hubs/live in tests. */
function start(source: LiveFeedSource = liveConnectionSource()) {
  if (started) return;
  started = true;
  unsubscribe = onAiUnavailable(() => { void refresh(); });
  const read = () => { if (started) void refresh(); };
  // The read at start stands for the first subscription's, which came a moment later as a second request on every open.
  // shortcut: an AiChanged between that read's answer and the first subscription is missed until the next reconnect; read on the first
  // subscription too if that ever matters more than the request.
  let first = true;
  const subscribed = () => { if (first) first = false; else read(); };
  // Unavailable (offline) changes nothing: the flags stay as last read and the next subscription reads again.
  closeFeed = source(aiTopic, { message: read, subscribed, unavailable: () => {} });
  void refresh();
}

/** Ends the session's feed and forgets everything: the next account starts from all false. */
function stop() {
  started = false;
  generation++;
  again = false;
  controller?.abort(); controller = undefined;
  inFlight = undefined;
  closeFeed?.(); closeFeed = undefined;
  unsubscribe?.(); unsubscribe = undefined;
  state.value = noAiCapabilities();
}

export function useAiCapabilities() {
  return { capabilities: readonly(state), any, refresh, start, stop };
}
