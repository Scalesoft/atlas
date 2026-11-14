import type { Component } from "vue";
import type { LibraryView } from "../../shellRoute";
import t from "../../i18n/components-home";
import RecentChangesCard from "./RecentChangesCard.vue";
import RecentVisitsCard from "./RecentVisitsCard.vue";
import WaitingCard from "./WaitingCard.vue";

/**
 * The cards of the home dashboard, in their order. A card is its component (it loads its own data and renders DashboardCard with
 * the title and „Zobrazit vše“ of its entry), so the next card is one file and one line here. `module`: shown only while that
 * optional module is on (useModules). `wide`: takes the main column on a wide screen.
 */
export interface DashboardCardDefinition {
  id: string;
  title: () => string;
  icon: string;
  component: Component;
  showAll?: LibraryView;
  module?: string;
  wide?: boolean;
}

export const dashboardCards: readonly DashboardCardDefinition[] = [
  { id: "changes", title: () => t.changes.title, icon: "history", component: RecentChangesCard, wide: true },
  { id: "recent", title: () => t.recent.title, icon: "recent", component: RecentVisitsCard, showAll: "recent" },
  { id: "waiting", title: () => t.waiting.title, icon: "notifications", component: WaitingCard, showAll: "drafts" },
];
