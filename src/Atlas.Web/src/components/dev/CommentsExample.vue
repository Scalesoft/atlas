<script setup lang="ts">
import { ref } from "vue";
import type { CommentApi, CommentPage, ObjectComment } from "../../comments/commentApi";
import type { ObjectListener, ViewSessions } from "../../live/viewSession";
import CommentThread from "../comments/CommentThread.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import { Button } from "../ui/button";
import { Switch } from "../ui/switch";

// Local sample data only: an in-memory comment path and a view session the buttons below speak through; nothing leaves the gallery.
const me = "00000000-0000-0000-0000-0000000000a1";
const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();
let next = 1;
const stored = ref<ObjectComment[]>([
  { id: "c3", authorId: me, authorName: "Jana Nováková", body: "Navrhuji **přesunout** nasazení na čtvrtek, viz [Provozní příručka](#/pages/runbook).", anchor: null,
    createdAt: minutesAgo(12), editedAt: minutesAgo(5), deleted: false, canEdit: true, canDelete: true },
  { id: "c2", authorId: "00000000-0000-0000-0000-0000000000b2", authorName: "Petr Svoboda", body: "", anchor: null, createdAt: minutesAgo(90), editedAt: null,
    deleted: true, canEdit: false, canDelete: false },
  { id: "c1", authorId: "00000000-0000-0000-0000-0000000000c3", authorName: "Karel Novák", body: "Postup v sekci *Instalace* je zastaralý:\n\n- chybí krok s certifikátem\n- `kubectl` verze 1.31",
    anchor: "instalace", createdAt: minutesAgo(60 * 26), editedAt: null, deleted: false, canEdit: false, canDelete: false },
  ...Array.from({ length: 21 }, (_, index): ObjectComment => ({ id: `old-${index}`, authorId: "00000000-0000-0000-0000-0000000000c3", authorName: "Karel Novák",
    body: `Starší poznámka ${index + 1}`, anchor: null, createdAt: minutesAgo(60 * 24 * (3 + index)), editedAt: null, deleted: false, canEdit: false, canDelete: false })),
]);
const enabled = ref(true);
const pageOf = (items: ObjectComment[], before?: string): CommentPage => {
  const start = before ? items.findIndex(item => item.id === before) + 1 : 0;
  return { items: items.slice(start, start + 20), hasMore: items.length > start + 20, count: items.filter(item => !item.deleted).length, canPost: enabled.value };
};
const api: CommentApi = {
  page: async (_objectId, before) => pageOf(stored.value, before),
  async post(_objectId, body, anchor) {
    const comment: ObjectComment = { id: `new-${next++}`, authorId: me, authorName: "Jana Nováková", body, anchor: anchor ?? null, createdAt: new Date().toISOString(),
      editedAt: null, deleted: false, canEdit: true, canDelete: true };
    stored.value = [comment, ...stored.value];
    return comment;
  },
  async edit(_objectId, commentId, body) {
    const comment = { ...stored.value.find(item => item.id === commentId)!, body, editedAt: new Date().toISOString() };
    stored.value = stored.value.map(item => (item.id === commentId ? comment : item));
    return comment;
  },
  async remove(_objectId, commentId) {
    stored.value = stored.value.map(item => (item.id === commentId ? { ...item, body: "", anchor: null, deleted: true, canEdit: false, canDelete: false } : item));
  },
  counts: async () => ({}),
};
// The object's view session: „Kolega napíše“ stores a colleague's comment and sends the id-only notice the server would.
const listeners = new Set<ObjectListener>();
const views: Pick<ViewSessions, "open"> = {
  open: () => ({ watch: (_id, listener) => { listeners.add(listener); return () => listeners.delete(listener); }, viewport() {}, presence() {}, close() {} }),
};
function colleagueWrites() {
  const comment: ObjectComment = { id: `colleague-${next++}`, authorId: "00000000-0000-0000-0000-0000000000b2", authorName: "Petr Svoboda",
    body: "Souhlasím, čtvrtek je _lepší_.", anchor: null, createdAt: new Date().toISOString(), editedAt: null, deleted: false, canEdit: false, canDelete: false };
  stored.value = [comment, ...stored.value];
  listeners.forEach(listener => listener.changed?.({ kind: "comments", count: stored.value.length, latestId: comment.id }, 1));
}
// A page without comments yet: its own in-memory list, so the folded row and its first comment do not touch the sample above.
const fresh = ref<ObjectComment[]>([]);
const emptyApi: CommentApi = {
  ...api,
  page: async () => ({ items: fresh.value, hasMore: false, count: fresh.value.length, canPost: true }),
  async post(objectId, body, anchor) {
    const comment = await api.post(objectId, body, anchor);
    stored.value = stored.value.filter(item => item.id !== comment.id);
    fresh.value = [comment, ...fresh.value];
    return comment;
  },
};
const thread = ref(0);
const anchorShown = ref("");
</script>

<template>
  <SettingsPage title="Komentáře" description="Diskuse k objektu (komentáře spec): jedna sdílená časová osa pro stránku, tabuli i dok, s ukázkovými daty.">
    <SettingsSection title="CommentThread"
      description="Plochá časová osa jako ve Wiki.js: nejnovější nahoře, avatar, jméno a relativní čas, „upraveno“, řádek „Komentář byl odstraněn“, „Starší komentáře“ po dvaceti a pole „Napsat komentář…“ s Markdownem editoru stránky v kompaktní variantě, bez úchytu bloků, takže nic nepřesahuje do docku ani do sekce stránky (Ctrl+Enter odešle). Vlastní komentář upravíte a smažete (tužka a koš na konci řádku, ukážou se při najetí nebo fokusu, na dotykové obrazovce stále), správce objektu smaže každý. Klik kamkoli do pole píše. Použijte ji všude, kde se diskutuje k objektu: sekce pod obsahem stránky (title „Komentáře“), panel „Diskuse“ tabule a záložka „Diskuse“ v detailu objektu. Komentáře se načtou až u výřezu a nové přicházejí relací pohledu objektu; pohyb: nový komentář se zjeví, smazaný se prolne na řádek odstranění. Proti ActivityTimeline (záznam změn, jen ke čtení) se do ní píše a upravuje na místě.">
      <div class="comments-example-tools">
        <label class="comments-example-switch"><Switch v-model="enabled" @update:model-value="thread++" />Komentáře v prostoru zapnuté</label>
        <Button variant="outline" size="sm" type="button" @click="colleagueWrites">Kolega napíše komentář</Button>
        <span v-if="anchorShown" class="comments-example-anchor">Místo: {{ anchorShown }}</span>
      </div>
      <div class="comments-example-frame">
        <CommentThread :key="thread" object-id="gallery-page" view-key="page:gallery-page" title="Komentáře" :api="api" :views="views" :lazy="false"
          @anchor="anchorShown = $event" />
      </div>
    </SettingsSection>
    <SettingsSection title="Stránka bez komentářů"
      description="Sekce pod stránkou bez jediného komentáře je jen tichý řádek s nadpisem a „Napsat komentář“: nezabírá místo pod obsahem a nenačítá editor. Pole se otevře na klik s kurzorem v textu; „Zrušit“ nebo Escape v prázdném poli ho zase složí. Záložka „Diskuse“ v doku (bez title) ukazuje pole hned, protože ji čtenář otevřel záměrně.">
      <div class="comments-example-frame">
        <CommentThread object-id="gallery-empty-page" title="Komentáře" :api="emptyApi" :lazy="false" />
      </div>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.comments-example-tools { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; margin-bottom: 16px; }
.comments-example-switch { display: inline-flex; align-items: center; gap: 8px; font-size: 14px; }
.comments-example-anchor { color: var(--muted); font-size: 13px; }
.comments-example-frame { max-width: 720px; }
</style>
