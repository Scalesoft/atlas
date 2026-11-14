<script setup lang="ts">
import { computed, provide, useId } from "vue";
import { useModules } from "../../composables/useModules";
import { homeApi, homeApiKey, type HomeApi, type RecentChange } from "./homeApi";
import type { LibraryView } from "../../shellRoute";
import type { TreeNodeType } from "../../treeMove";
import { dashboardCards, type DashboardCardDefinition } from "./dashboardCards";
import t from "../../i18n/components-home";
import "../settings/settings.css";

/**
 * Domů (4.0): where the reader lands after signing in and from the logo — the cards of `dashboardCards`, each with its own data and
 * states, side by side where there is room. `api` replaces the server's reads (gallery, tests); `cards` the registry (gallery).
 */
const props = defineProps<{ api?: HomeApi; cards?: readonly DashboardCardDefinition[] }>();
const emit = defineEmits<{ select: [id: string, type: TreeNodeType]; navigate: [view: LibraryView]; history: [change: RecentChange] }>();
provide(homeApiKey, props.api ?? homeApi);
const modules = useModules();
const shown = computed(() => (props.cards ?? dashboardCards).filter(card => !card.module || modules.enabled(card.module)));
const headingId = useId();
</script>

<template>
  <section class="home-dashboard settings-ui" :aria-labelledby="headingId" data-home-dashboard>
    <header class="home-header">
      <h1 :id="headingId">{{ t.title }}</h1>
      <p>{{ t.intro }}</p>
    </header>
    <div class="home-grid atlas-stagger" :aria-label="t.cards" role="group">
      <component :is="card.component" v-for="card in shown" :key="card.id" :card="card" :class="{ 'is-wide': card.wide }" :data-home-card="card.id"
        @select="(id: string, type: TreeNodeType) => emit('select', id, type)" @navigate="(view: LibraryView) => emit('navigate', view)"
        @history="(change: RecentChange) => emit('history', change)" />
    </div>
  </section>
</template>

<style scoped>
/* Its own room decides the columns (the workspace with the sidebar or the dock open, or the gallery's frame). */
.home-dashboard { container: home-dashboard / inline-size; width: min(var(--atlas-page-max, 1600px), 100%); margin: 0 auto; padding: clamp(16px, 3vw, 36px) var(--atlas-page-gutter, clamp(16px, 3vw, 36px)); box-sizing: border-box; }
.home-header { margin-bottom: 22px; }
.home-header h1 { margin: 4px 0 8px; font-size: clamp(25px, 3vw, 35px); letter-spacing: -.035em; }
.home-header p { margin: 0; max-width: 560px; color: var(--muted); line-height: 1.6; }
.home-grid { display: grid; grid-template-columns: minmax(0, 1.7fr) minmax(0, 1fr); grid-auto-flow: dense; align-items: start; gap: 20px; }
.home-grid > .is-wide { grid-column: 1; grid-row: span 2; }
.home-grid > :not(.is-wide) { grid-column: 2; }
@container home-dashboard (max-width: 860px) {
  .home-grid { grid-template-columns: minmax(0, 1fr); gap: 16px; }
  .home-grid > .is-wide, .home-grid > :not(.is-wide) { grid-column: 1; grid-row: auto; }
}
</style>
