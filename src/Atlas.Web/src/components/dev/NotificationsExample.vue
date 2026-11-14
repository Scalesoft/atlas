<script setup lang="ts">
import { ref } from "vue";
import type { NotificationApi, NotificationItem, NotificationPreference } from "../notifications/notificationInbox";
import NotificationInbox from "../notifications/NotificationInbox.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import { Button } from "../ui/button";

// Local sample data only: an in-memory inbox the buttons below speak through, as the live `user:` channel would; nothing leaves the gallery.
const hoursAgo = (hours: number) => new Date(Date.now() - hours * 3_600_000).toISOString();
let next = 1;
const item = (id: string, kind: string, title: string, actor: string, hours: number, read: boolean, count = 1, type = "atlas.page"): NotificationItem => ({
  id, kind, subjectId: `subject-${id}`, subjectType: type, subjectTitle: title, spaceId: "space", spaceName: "Vývoj", anchor: null, actorId: `actor-${actor}`,
  actorName: actor, count, createdAt: hoursAgo(hours), updatedAt: hoursAgo(hours), read,
});
const stored = ref<NotificationItem[]>([
  item("n1", "comment.mention", "Provozní příručka", "Jana Nováková", 1, false, 3),
  // A background job that failed for good, to the wiki administrators: from no one, it opens the job in Správa wiki.
  { ...item("n7", "job.failed", "Záloha ZIP", "", 2, false, 2, "atlas.job"), anchor: "backups.zip", actorId: null, actorName: null, spaceId: null, spaceName: null },
  item("n2", "page.mention", "Plán nasazení 2.0", "Petr Svoboda", 5, false),
  item("n3", "comment.mention", "Retrospektiva", "Karel Novák", 30, true, 1, "atlas.board"),
  item("n4", "item.shared", "Moje poznámky k vydání", "Jana Nováková", 24 * 4, true),
  item("n5", "page.mention", "Architektura Atlasu 3", "Petr Svoboda", 24 * 12, true),
  // An ended share: told without the item's title, and nothing to open.
  { ...item("n6", "item.unshared", "", "Jana Nováková", 24 * 20, true), subjectType: null, spaceName: null },
]);
const preferences = ref<NotificationPreference[]>([
  { kind: "comment.mention", enabled: true }, { kind: "page.mention", enabled: true }, { kind: "item.shared", enabled: false },
]);
const unreadOf = () => stored.value.filter(entry => !entry.read).length;
const api: NotificationApi = {
  list: async () => ({ items: stored.value, next: null, unread: unreadOf() }),
  unread: async () => ({ count: unreadOf() }),
  async markRead(id) { stored.value = stored.value.map(entry => (entry.id === id ? { ...entry, read: true } : entry)); return { marked: 1, unread: unreadOf() }; },
  async markAllRead() { const marked = unreadOf(); stored.value = stored.value.map(entry => ({ ...entry, read: true })); return { marked, unread: 0 }; },
  preferences: async () => ({ items: preferences.value }),
  async setPreference(kind, enabled) { preferences.value = preferences.value.map(entry => (entry.kind === kind ? { ...entry, enabled } : entry)); return { kind, enabled }; },
};
const signal = ref(0);
const empty = ref(0);
const emptyApi: NotificationApi = { ...api, list: async () => ({ items: [], next: null, unread: 0 }) };
function colleagueMentions() {
  stored.value = [item(`live-${next++}`, "comment.mention", "Rozpočet Q4", "Petr Svoboda", 0, false), ...stored.value];
  signal.value++;
}
</script>

<template>
  <SettingsPage title="Oznámení" description="Jeden inbox pro zmínky a sdílení (inbox spec 2026-10-29), s ukázkovými daty.">
    <SettingsSection title="NotificationInbox"
      description="Knihovna → Oznámení a položka „Oznámení“ v levé liště s počtem nepřečtených. Nejnovější událost nahoře, po dnech (Dnes, Včera, Tento týden, Starší); nepřečtené mají tečku a zvýrazněnou ikonu. Jedna položka na předmět: další zmínka u téhož objektu zvýší počet (3×) a posune ji nahoru. Klik nebo Enter otevře předmět na kotvě a označí ji přečtenou, „Označit vše jako přečtené“ zbytek a fokus přejde na první řádek. Počet nepřečtených nese i záložka Oznámení v Knihovně a na telefonu tečka na tlačítku menu. Z klávesnice: fokus přistane na první nepřečtené, šipky a Home/End chodí po řádcích, zkratka G, pak I otevře Oznámení odkudkoli. Nová položka (živý signál kanálu user:) se zjeví nahoře. Konec sdílení přijde bez názvu položky a bez odkazu (přístup už není). Preference po druzích jsou pod seznamem v „Nastavení oznámení“. Proti PersonalLibraryPanel (stránky s verzí a oblíbenými, stránkování posunem) nese řádek událost s aktérem, počtem a stavem přečtení; vzhled je stejný seznam Knihovny. Při 375 px má řádek šířku karty: dlouhý předmět se zkrátí třemi tečkami, čas a tečka zůstanou v kartě a stránka se neposouvá do strany.">
      <div class="notifications-example-tools">
        <Button variant="outline" data-example-mention @click="colleagueMentions">Kolega vás zmíní</Button>
      </div>
      <NotificationInbox :api="api" :refresh-key="signal" />
    </SettingsSection>
    <SettingsSection title="Prázdný inbox" description="Bez oznámení: prázdný stav místo seznamu, tlačítko „Označit vše“ vypnuté.">
      <NotificationInbox :key="empty" :api="emptyApi" />
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.notifications-example-tools { display: flex; gap: 12px; margin-bottom: 14px; }
</style>
