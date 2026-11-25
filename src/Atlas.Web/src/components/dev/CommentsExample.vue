<script setup lang="ts">
import { ref } from "vue";
import type { CommentApi, CommentPage, ObjectComment } from "../../comments/commentApi";
import type { ObjectListener, ViewSessions } from "../../live/viewSession";
import { quoteMarkdown, requestedQuote } from "../../comments/commentFocus";
import CommentThread from "../comments/CommentThread.vue";
import PageSidePanel from "../PageSidePanel.vue";
import PageDiscussionPeek from "../comments/PageDiscussionPeek.vue";
import type { DiscussionBubble } from "../../comments/pageDiscussionFeed";
import AtlasIcon from "../AtlasIcon.vue";
import SendButton from "../SendButton.vue";
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
// SendButton: a send that takes a moment, so the plane's flight and its return are seen.
const sendText = ref("");
const sendBusy = ref(false);
function sendDemo() {
  if (!sendText.value.trim() || sendBusy.value) return;
  sendBusy.value = true;
  setTimeout(() => { sendBusy.value = false; sendText.value = ""; }, 1200);
}
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
// The page's Diskuse in its dock: the selected text of the sample paragraph is what App's Alt C hands over (`requestedQuote`).
const article = ref<HTMLElement>();
const dock = ref(0);
// The peek under the avatars and the dock take turns: a bubble flies in for a colleague's comment while the discussion is closed;
// opening the discussion sends the bubbles toward the panel, which slides in from the right.
const dockOpen = ref(false);
const bubbles = ref<DiscussionBubble[]>([]);
const unread = ref(0);
function peekWrites() {
  colleagueWrites();
  if (dockOpen.value) return;
  unread.value++;
  bubbles.value = [...bubbles.value, { id: `peek-${next++}`, authorId: "00000000-0000-0000-0000-0000000000b2", authorName: "Petr Svoboda", text: "Souhlasím, čtvrtek je lepší" }].slice(-5);
}
async function peekReply(markdown: string) { await api.post("gallery-page", markdown); }
function toggleDock() {
  dockOpen.value = !dockOpen.value;
  if (dockOpen.value) { bubbles.value = []; unread.value = 0; }
}
function quoteSelection() {
  const selection = window.getSelection();
  if (!selection || selection.isCollapsed || !article.value?.contains(selection.anchorNode)) return;
  requestedQuote.value = quoteMarkdown(selection.toString());
}
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
    <SettingsSection title="Diskuse stránky v doku"
      description="Stránka má tu samou diskusi jako tabule: záložka „Diskuse“ v doku stránky (PageSidePanel, tab comments) je stejné CommentThread nad stejným objektem a stejnou relací pohledu, žádný druhý chat. Otevře ji bublina u avatarů s počtem komentářů (zvýrazněným, dokud nepřečtete kolegův nový) a Alt C; kurzor jde rovnou do pole, Escape zavře dok a vrátí kurzor do textu. Pole odpovědi (v panelu i v bublině nahoře) je jedno: odesílá Enter, Shift+Enter píše nový řádek a papírové letadélko vedle pole to říká těm, kdo to nevědí (SendButton: tlumené u prázdného pole, akcentové s textem, při odeslání odletí). Je-li ve stránce označený text, nový komentář začne jeho citací (Markdown „> …“), v galerii tlačítkem pod odstavcem. Oproti sekci pod obsahem nezabírá místo článku a zůstane otevřená při čtení.">
      <div class="comments-example-tools">
        <Button variant="outline" size="sm" type="button" @click="quoteSelection">Citovat označený text</Button>
        <Button variant="ghost" size="sm" type="button" @click="dock++">Znovu otevřít</Button>
        <Button variant="outline" size="sm" type="button" data-gallery-peek-write @click="peekWrites">Kolega napíše (bublina nahoře)</Button>
        <span class="comments-example-anchor">
          <Button variant="ghost" size="icon-sm" type="button" class="comments-example-bubble-button" :aria-pressed="dockOpen" :aria-label="'Diskuse'" @click="toggleDock"><AtlasIcon name="quote" />
            <span v-if="unread" class="comments-example-unread">{{ unread }}</span></Button>
          <PageDiscussionPeek :bubbles="bubbles" :docked="dockOpen" :ttl="8000" :reply="peekReply" @open="toggleDock" @dismiss="bubbles = bubbles.filter(item => item.id !== $event)" />
        </span>
      </div>
      <p ref="article" class="comments-example-article">Zálohy běží každou noc ve dvě hodiny a obnova z nich se zkouší jednou za čtvrtletí. Označte část věty a použijte tlačítko výše.</p>
      <Transition name="comments-example-dock">
      <div v-if="dockOpen" class="comments-example-frame comments-example-dock">
        <PageSidePanel :key="dock" tab="comments" page-id="gallery-page" space-id="gallery-space" page-title="Provozní příručka" :document-root="null" :version="3" :is-published="true"
          :published-version="3" :comment-api="api" :views="views" />
      </div>
      </Transition>
    </SettingsSection>
    <SettingsSection title="SendButton"
      description="Papírové letadélko vedle pole, které odesílá Enterem: jedna komponenta pro komentář, rychlou odpověď v bublině i otázku asistentovi. Bez textu je tlumené (aria-disabled, Tabem dosažitelné, nic neudělá), s textem akcentové; při odeslání letadélko odletí se zprávou a po odeslání přilétne nové. Tón quiet (komentáře) nebo ai (přechod asistenta). Při omezeném pohybu nic nelétá. Použijte ho u pole s odesláním Enterem; u formuláře s textovým potvrzením zůstává Button.">
      <form class="comments-example-send" @submit.prevent="sendDemo">
        <input v-model="sendText" class="comments-example-send-input" aria-label="Zpráva" placeholder="Napište a stiskněte Enter" />
        <SendButton :ready="!!sendText.trim()" :busy="sendBusy" label="Odeslat (Enter)" />
        <SendButton tone="ai" :ready="!!sendText.trim()" :busy="sendBusy" label="Odeslat otázku (Enter)" />
      </form>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.comments-example-tools { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; margin-bottom: 16px; }
.comments-example-switch { display: inline-flex; align-items: center; gap: 8px; font-size: 14px; }
.comments-example-anchor { color: var(--muted); font-size: 13px; }
.comments-example-frame { max-width: 720px; }
.comments-example-article { max-width: 720px; margin: 0 0 12px; font-size: 14px; line-height: 1.6; }
/* The dock's own width, as WorkspaceSidePanel gives it. */
.comments-example-anchor { position: relative; display: inline-flex; margin-left: auto; }
.comments-example-bubble-button { position: relative; }
.comments-example-bubble-button[aria-pressed="true"] { background: var(--accent-soft); color: var(--accent); }
.comments-example-unread { position: absolute; top: -3px; right: -3px; min-width: 16px; padding: 0 4px; border-radius: 999px; background: var(--accent); color: var(--on-accent, #fff); font-size: 10.5px; font-weight: 650; line-height: 16px; text-align: center; }
/* The panel slides in from its edge (st-slide-in); reduced motion: the distance is 0, only the fade stays. */
.comments-example-dock-enter-active { animation: st-slide-in var(--atlas-motion-slow) var(--atlas-ease-out) both; }
.comments-example-dock-leave-active { animation: st-slide-in var(--atlas-motion-base) var(--atlas-ease-in) reverse both; }
.comments-example-send { display: flex; align-items: center; gap: 8px; max-width: 420px; }
.comments-example-send-input { flex: 1; min-width: 0; height: 36px; padding: 0 10px; border: 1px solid var(--input-line); border-radius: 8px; background: var(--surface); color: var(--text); font: inherit; font-size: 14px; }
.comments-example-send-input:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
.comments-example-dock { max-width: 400px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface); }
</style>
