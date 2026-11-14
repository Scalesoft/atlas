<script setup lang="ts">
import componentsLibraryHome from "../../i18n/components-library-home";
import { computed } from "vue";
import type { Space } from "../../api";
import type { TreeNodeType } from "../../treeMove";
import type { LibraryTab } from "../../personalLibrary";
import { lazyComponent } from "../../lazyComponent";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import EmptyState from "../settings/EmptyState.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import type { LibraryPart } from "./LibraryPage.vue";

/**
 * The part of Knihovna the address names, rendered inside LibraryPage's slot: the space directory, the contacts,
 * the personal lists and the inbox, or the plain lists of recent/favorite/draft/updated pages. The shell owns the data
 * (spaces, pins, refresh counters) and routing; this host owns only which part shows.
 * Every part loads lazily, so a tab nobody opens downloads nothing.
 */
const SpaceDirectory = lazyComponent(() => import("../SpaceDirectory.vue"), { loading: LoadingSkeleton });
const PersonalLibraryPanel = lazyComponent(() => import("../PersonalLibraryPanel.vue"), { loading: LoadingSkeleton });
const ContactDirectory = lazyComponent(() => import("../contacts/ContactDirectory.vue"), { loading: LoadingSkeleton });
const ContactOverview = lazyComponent(() => import("../contacts/ContactOverview.vue"), { loading: LoadingSkeleton });
const PersonalItems = lazyComponent(() => import("../personal/PersonalItems.vue"), { loading: LoadingSkeleton });
const NotificationInbox = lazyComponent(() => import("../notifications/NotificationInbox.vue"), { loading: LoadingSkeleton });

const props = defineProps<{
  view: LibraryPart;
  userId: string;
  spaces: Space[];
  canCreate: boolean;
  canManage: boolean;
  pendingIds: ReadonlySet<string>;
  limit: number;
  /** Bumped by the shell when its lists change; the personal lists and items reload on it. */
  refreshKey: number;
  personalRefreshKey: number;
  notificationSignal: number;
  /** The shell's first list of spaces has arrived; before that Prostory shows a placeholder, not "no spaces". */
  spacesLoaded: boolean;
}>();
const emit = defineEmits<{
  open: [space: Space];
  pin: [space: Space, pinned: boolean];
  create: [];
  navigate: [part: LibraryPart];
  select: [id: string, type: TreeNodeType, spaceId?: string];
  unread: [count: number];
}>();

/** The tab served by the plain page lists; the other parts have their own component. */
const listTab = computed<LibraryTab | undefined>(() =>
  props.view === "spaces" || props.view === "contacts" || props.view === "personal" || props.view === "shared" || props.view === "notifications" ? undefined : props.view);
</script>

<template>
  <LoadingSkeleton v-if="view === 'spaces' && !spacesLoaded" :rows="4" row-height="88px" :label="componentsLibraryHome.content.loadingSpaces" />
  <SpaceDirectory v-else-if="view === 'spaces'" :key="`spaces:${userId}`" :spaces="spaces" :can-create="canCreate" :can-manage="canManage" :pending-ids="pendingIds" :limit="limit"
    :storage-key="`atlas:reading:${userId}:library-view`" @open="emit('open', $event)" @pin="(space, pinned) => emit('pin', space, pinned)" @create="emit('create')" />
  <!-- Kontakty: the overview of where people occur, with the directory to edit as its Adresář (UX1-14). -->
  <ContactOverview v-else-if="view === 'contacts'" :key="`contacts:${userId}`"><template #directory><ContactDirectory /></template></ContactOverview>
  <PersonalItems v-else-if="view === 'personal' || view === 'shared'" :key="`${view}:${userId}`" :mode="view === 'personal' ? 'mine' : 'shared'"
    :refresh-key="refreshKey" />
  <NotificationInbox v-else-if="view === 'notifications'" :key="`notifications:${userId}`" :refresh-key="notificationSignal" @unread="emit('unread', $event)" />
  <PersonalLibraryPanel v-else-if="listTab" tabbed :tab="listTab" :refresh-key="refreshKey + personalRefreshKey" @select="(id, type) => emit('select', id, type)">
    <template v-if="listTab === 'recent'" #empty>
      <EmptyState class="library-start" icon="recent" :title="componentsLibraryHome.content.noneOpenedTitle" :text="componentsLibraryHome.content.noneOpenedText">
        <Button variant="outline" @click="emit('navigate', 'spaces')"><AtlasIcon name="folder" />{{ componentsLibraryHome.content.browse }}</Button>
      </EmptyState>
    </template>
  </PersonalLibraryPanel>
</template>
