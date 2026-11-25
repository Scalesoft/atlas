<script setup lang="ts">
import { computed, onErrorCaptured, ref, watch } from "vue";
import { MarkdownRenderer } from "@atlas/markdown/vue";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/midnight.css";
import { readDocument } from "../../document";
import type { PublicShareState } from "../../sharing";
import AtlasBrandLogo from "../AtlasBrandLogo.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import ErrorState from "../settings/ErrorState.vue";
import { formatDateTime } from "../../formatDate";
import { atlasContentPlugins } from "../../documentWidgets";
import { createOverviewReading } from "../../overview/overviewReading";
import t from "../../i18n/components-sharing-public";

const props = defineProps<{ state: PublicShareState }>();
defineEmits<{ retry: [] }>();
const failed = ref(false);
const value = computed(() => {
  if (props.state.kind !== "ready") return null;
  try { return readDocument(props.state.value.contentJson); } catch { return null; }
});
// A shared page has no id for its reader (the link's token stays out of the browser's storage): one level for every shared page.
const plugins = [...atlasContentPlugins({ kind: "public" }), createOverviewReading(() => "shared")];
onErrorCaptured(() => { failed.value = true; return false; });
watch(() => props.state.kind === "ready" ? props.state.value.contentJson : null, () => { failed.value = false; });
</script>

<template>
  <main id="main" class="public-share-shell">
    <header class="public-share-brand"><AtlasBrandLogo /><span>Atlas</span></header>
    <section v-if="state.kind === 'loading'" class="public-share-card"><LoadingSkeleton :rows="6" :label="t.loading" /></section>
    <section v-else-if="state.kind === 'error'" class="public-share-card public-share-error">
      <h1>{{ t.unavailableTitle }}</h1>
      <p v-if="state.missing" role="alert">{{ t.unavailable }}</p>
      <ErrorState v-else :title="t.loadFailed" :retry-label="t.retry" @retry="$emit('retry')" />
    </section>
    <article v-else class="public-share-card">
      <header class="public-share-heading">
        <h1>{{ state.value.title }}</h1>
        <p><template v-if="state.value.live">{{ t.live(formatDateTime(state.value.publishedAt)) }}</template><template v-else>{{ t.revision(state.value.revisionVersion) }}</template> · {{ t.validUntil(formatDateTime(state.value.expiresAt)) }}</p>
      </header>
      <p v-if="!value || failed" role="alert" class="notice error">{{ t.failed }}</p>
      <!-- The public API has already removed private integrations and rewritten attachment URLs. -->
      <!-- @vue-generic {'rich'} -->
      <MarkdownRenderer v-else :value="value" profile="rich" :ariaLabel="t.documentLabel" :plugins="plugins" />
    </article>
  </main>
</template>

<style scoped>
/* .atlas is a flex row: without flex: 1 the shell shrank to its content, so the card hung at the left edge and a short
   state (the unavailable link) was narrower than an article instead of the page being centred. */
.public-share-shell{flex:1;min-width:0;min-height:100dvh;padding:clamp(16px,4vw,48px);background:var(--canvas);color:var(--text)}
.public-share-brand{display:flex;align-items:center;gap:10px;max-width:980px;margin:0 auto 18px;font-weight:700}.public-share-brand :deep(.atlas-brand-logo){flex:0 0 28px;width:28px;height:28px}
.public-share-card{max-width:980px;margin:auto;padding:clamp(20px,5vw,64px);border:1px solid var(--line);border-radius:16px;background:var(--surface);box-shadow:var(--shadow)}
.public-share-heading{margin-bottom:28px;border-bottom:1px solid var(--line);padding-bottom:18px}.public-share-heading h1{margin:0;font-size:clamp(28px,5vw,44px)}.public-share-heading p{margin:8px 0 0;color:var(--muted)}
.public-share-error{display:grid;gap:12px;justify-items:start}.public-share-error :where(h1,p){margin:0}
.md-renderer{--font-sans:"DM Sans",system-ui,sans-serif}
/* The shell's phone breakpoint (680 px), not a value of its own. */
@media(max-width:680px){.public-share-shell{padding:12px}.public-share-card{padding:20px;border-radius:12px}}
.public-share-card{animation:atlas-reveal var(--atlas-motion-surface) var(--atlas-ease-out)}
</style>
