<script setup lang="ts">
import { Editor, Extension } from "@tiptap/core";
import Document from "@tiptap/extension-document";
import Paragraph from "@tiptap/extension-paragraph";
import Text from "@tiptap/extension-text";
import { Plugin } from "@tiptap/pm/state";
import { Decoration, DecorationSet } from "@tiptap/pm/view";
import { yCursorPluginKey } from "@tiptap/y-tiptap";
import { onBeforeUnmount, onMounted, ref } from "vue";
import type { RemoteDrag } from "../../boards/boardSession";
import { createCollaborationPresence } from "../../collaborationPresence";
import BoardPresence from "../boards/canvas/BoardPresence.vue";
import CollaboratorAvatars from "../CollaboratorAvatars.vue";
import StatusBadge from "../settings/StatusBadge.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import { Button } from "../ui/button";

const samples = [
  { clientId: 1, name: "Jana Nováková", color: "#5274bd", cursorAvailable: true },
  { clientId: 2, name: "Petr Král", color: "#348879", cursorAvailable: true },
  { clientId: 3, name: "Eva Svobodová", color: "#a46b45", cursorAvailable: false },
  { clientId: 4, name: "Tomáš Novotný", color: "#8d5cf6", cursorAvailable: true },
  { clientId: 5, name: "Lucie Dvořáková", color: "#ad4c70", cursorAvailable: true },
];
const peers = ref(samples.slice(0, 3));
const confirmation = ref(0);
const selected = ref("");

// Co-editors' pointers: samples arrive 20 times a second, as from the server; pointerGlide.ts carries them between.
const pointers = ref<RemoteDrag[]>([]);
let pointerTimer: ReturnType<typeof setInterval> | undefined;
const path = (t: number, phase: number) => ({ x: 150 + 120 * Math.cos(t / 900 + phase), y: 60 + 34 * Math.sin(t / 600 + phase) });
function togglePointers() {
  if (pointerTimer) { clearInterval(pointerTimer); pointerTimer = undefined; pointers.value = []; return; }
  const start = Date.now();
  pointerTimer = setInterval(() => {
    const t = Date.now() - start;
    pointers.value = [
      { userId: "jana", displayName: "Jana Nováková", color: samples[0]!.color, dragging: [], cursor: path(t, 0) },
      { userId: "petr", displayName: "Petr Král", color: samples[1]!.color, dragging: [], cursor: path(t, 2.4) },
    ];
  }, 50);
}

// An agent's caret over a page: the real overlay (collaborationPresence) on a small editor. In the application the caret comes from the
// agent's awareness state, which the server sends after an MCP, API or assistant write (LivePageWrites); here a cursor decoration of
// the y-tiptap key stands in for it, as the collaboration extension would place it.
const agentHost = ref<HTMLElement>();
let agentEditor: Editor | undefined;
let agentTimer: ReturnType<typeof setTimeout> | undefined;
const agentPresence = createCollaborationPresence({ onChange: () => {} });
const agentAbort = new AbortController();
const agentPeer = { clientId: 9, id: "ai:gallery", name: "Jana Nováková · AI (MCP)", color: "#7c3aed" };
// The avatar of the agent: at work (no caret yet) its glyph writes, then it has written (its caret arrives) and plays „done“.
const agentAvatars = ref<{ clientId: number; id: string; name: string; color: string; cursorAvailable: boolean }[]>([]);
onMounted(() => {
  const caretAt = Extension.create({
    name: "galleryAgentCaret",
    addProseMirrorPlugins: () => [new Plugin({
      key: yCursorPluginKey,
      state: {
        init: () => DecorationSet.empty,
        apply: (transaction, current) => {
          const at = transaction.getMeta(yCursorPluginKey) as number | null | undefined;
          if (at === undefined) return current.map(transaction.mapping, transaction.doc);
          return at === null ? DecorationSet.empty : DecorationSet.create(transaction.doc, [Decoration.widget(at, () => document.createElement("span"), { key: "9" })]);
        },
      },
    })],
  });
  agentEditor = new Editor({ element: agentHost.value!, extensions: [Document, Paragraph, Text, caretAt], content: "<p>Shrnutí porady: rozpočet schválen.</p>", editable: false });
  agentPresence.plugin.create({ profile: "rich", signal: agentAbort.signal }).onAttach!({ editor: agentEditor, controller: {} as never, signal: agentAbort.signal });
});
function playAgentWrite() {
  const editor = agentEditor!;
  clearTimeout(agentTimer);
  editor.commands.setContent("<p>Shrnutí porady: rozpočet schválen.</p>");
  agentPresence.update([]);
  agentAvatars.value = [{ ...agentPeer, cursorAvailable: false }];
  agentTimer = setTimeout(() => {
    const end = editor.state.doc.content.size - 1;
    editor.view.dispatch(editor.state.tr.insertText(" Další krok: objednat licence do pátku.", end));
    editor.view.dispatch(editor.state.tr.setMeta(yCursorPluginKey, editor.state.doc.content.size - 1));
    agentPresence.update([agentPeer]);
    agentAvatars.value = [{ ...agentPeer, cursorAvailable: true }];
    // The server keeps the agent's caret about two seconds (LivePageWrites.CaretLinger), then it fades out.
    agentTimer = setTimeout(() => { agentPresence.update([]); agentAvatars.value = []; }, 2000);
  }, 1600);
}
onBeforeUnmount(() => { clearInterval(pointerTimer); clearTimeout(agentTimer); agentAbort.abort(); agentEditor?.destroy(); });
</script>

<template>
  <SettingsSection title="Spolupráce a potvrzení" description="CollaboratorAvatars přebírá pouze povolené autory z aktivní relace. Tužka označuje autora, nikoli právě probíhající psaní. Detail se zobrazí najetím nebo fokusem; klepnutí přejde na dostupný kurzor. Čtenář bez kurzoru se z těchto dat nedá odvodit.">
    <div class="motion-sample">
      <CollaboratorAvatars :peers="peers" @navigate="selected = samples.find(peer => peer.clientId === $event)!.name" />
      <StatusBadge v-if="confirmation" :key="`confirmed-${confirmation}`" tone="success" label="Stránka byla publikována." confirmed />
      <StatusBadge v-else tone="success" label="Publikováno" />
    </div>
    <div class="st-actions">
      <Button variant="outline" :disabled="peers.length === samples.length" @click="peers = samples.slice(0, peers.length + 1)">Přidat člověka</Button>
      <Button variant="outline" :disabled="!peers.length" @click="peers = peers.slice(0, -1)">Odebrat člověka</Button>
      <Button variant="outline" @click="peers = [...peers.slice(1), ...peers.slice(0, 1)]">Prohodit pořadí</Button>
      <Button @click="confirmation++">Přehrát potvrzení</Button>
    </div>
    <div class="pointer-sample">
      <p>Ukazatel spolupracovníka je jeden: na nástěnce (BoardPresence) i nad stránkou (collaborationPresence s <code>pointers: true</code>). Na stránce se kotví k nejbližšímu místu v textu, takže ukazuje na stejné slovo při jiné šířce okna i posunu. Mezi vzorky (20× za sekundu, po síti nepravidelně) ho nese pružina v každém snímku (live/pointerGlide.ts): drží rychlost a u vzorku se nezastaví; stisk tlačítka vyšle kroužek. Čtenář živé stránky ani dotyk ukazatel neposílají; při omezeném pohybu skáče bez plynulého přechodu.</p>
      <BoardPresence :remote="pointers" :viewport="{ x: 0, y: 0, zoom: 1 }" />
    </div>
    <div class="st-actions">
      <Button variant="outline" :aria-pressed="pointers.length > 0" @click="togglePointers">{{ pointers.length ? "Zastavit ukazatele" : "Přehrát ukazatele" }}</Button>
    </div>
    <div class="pointer-sample agent-sample">
      <p>Kurzor AI agenta: když agent (MCP, osobní API, Asistent) zapíše do otevřené stránky, server pošle po zápisu jeho kurzor hned za text, který napsal. Kurzor má AI inkoust, jemnou záři a AI znak, který jednou přehraje „hotovo“; asi dvě sekundy stojí, kde agent psal, pak zmizí prolnutím. Lidé nezáří. Avatar agenta nese iniciály člověka, za kterého píše, v AI inkoustu se září a místo tužky AI znak: dokud agent pracuje, znak píše (hvězda pulzuje a kývá se), s kurzorem přehraje „hotovo“. Ukázka kreslí skutečný overlay nad malým editorem a skutečné CollaboratorAvatars.</p>
      <CollaboratorAvatars class="agent-avatars" :peers="agentAvatars" />
      <div ref="agentHost" class="agent-editor" />
    </div>
    <div class="st-actions">
      <Button variant="outline" @click="playAgentWrite">Přehrát zápis agenta</Button>
    </div>
    <p role="status">{{ selected ? `Ukázka přechodu na kurzor: ${selected}` : 'Zvolte autora nebo přehrajte potvrzení.' }}</p>
    <p>StatusBadge s confirmed použijte až po potvrzené operaci. Pro trvalý stav revize slouží PageStatusBadge. Příchod, odchod a pořadí používají stabilní ID; při omezeném pohybu se zachová text a ikona bez posouvání.</p>
  </SettingsSection>
</template>

<style scoped>
.motion-sample { display: flex; flex-wrap: wrap; align-items: center; gap: 20px; min-height: 64px; margin-block: 8px 16px; }
p { color: var(--muted); font-size: 13px; }
.agent-editor { padding-top: 22px; font-size: 15px; }
.agent-avatars { min-height: 36px; }
.pointer-sample { position: relative; min-height: 140px; margin-block: 8px; padding: 12px 16px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); }
</style>
