<script setup lang="ts">
import { Editor, Extension } from "@tiptap/core";
import Collaboration from "@tiptap/extension-collaboration";
import Document from "@tiptap/extension-document";
import Paragraph from "@tiptap/extension-paragraph";
import Text from "@tiptap/extension-text";
import { Plugin } from "@tiptap/pm/state";
import { Decoration, DecorationSet } from "@tiptap/pm/view";
import { yCursorPluginKey } from "@tiptap/y-tiptap";
import { onBeforeUnmount, onMounted, ref } from "vue";
import * as Y from "yjs";
import type { RemoteDrag } from "../../boards/boardSession";
import { createCollaborationPresence, type CollaborationPeer } from "../../collaborationPresence";
import BoardPresence from "../boards/canvas/BoardPresence.vue";
import CollaboratorAvatars from "../CollaboratorAvatars.vue";
import { userColor } from "../../live/objectPresence";
import StatusBadge from "../settings/StatusBadge.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import { Button } from "../ui/button";

// Each person in their colour (userColor, as their carets, pointers and history have it).
const samples = [
  { clientId: 1, name: "Jana Nováková", color: userColor("00000002"), cursorAvailable: true },
  { clientId: 2, name: "Petr Král", color: userColor("00000001"), cursorAvailable: true },
  { clientId: 3, name: "Eva Svobodová", color: userColor("00000003"), cursorAvailable: false },
  { clientId: 4, name: "Tomáš Novotný", color: userColor("00000004"), cursorAvailable: true },
  { clientId: 5, name: "Lucie Dvořáková", color: userColor("00000007"), cursorAvailable: true },
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
      // An AI's pointer (an „ai:“ user): the arrow in the AI gradient, its stars writing in its name pill, the aura breathing.
      { userId: "ai:nexus", displayName: "Nexus · AI (MCP)", color: userColor("00000004"), dragging: [], cursor: { x: path(t, 4.2).x + 60, y: path(t, 4.2).y } },
    ];
  }, 50);
}

// An agent's write into a page, on the real overlay (collaborationPresence) and a real collaborative editor: the agent shows (its stars
// at work), then its update arrives as one Yjs update of the client of its presence, as LivePageWrites sends it, and the editor types
// it in word by word with the AI caret on the front (live/agentReveal.ts). Where the typing ends, a cursor decoration of the y-tiptap key
// stands in for the caret the server's awareness state places there.
const agentHost = ref<HTMLElement>();
let agentEditor: Editor | undefined;
let agentDoc: Y.Doc | undefined;
let agentAbort: AbortController | undefined;
let agentTimer: ReturnType<typeof setTimeout> | undefined;
const agentAvatars = ref<CollaborationPeer[]>([]);
const agentPresence = createCollaborationPresence({ onChange: peers => { agentAvatars.value = peers; } });
// An agent's colour is the AI ink (collaborationPresence.css), whatever the server sends.
const agentPeer = { clientId: 9, id: "ai:gallery", name: "Jana Nováková · AI (MCP)", color: userColor("00000002") };
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
function openAgentEditor() {
  agentAbort?.abort();
  agentEditor?.destroy();
  agentDoc?.destroy();
  agentDoc = new Y.Doc();
  agentEditor = new Editor({ element: agentHost.value!, extensions: [Document, Paragraph, Text, Collaboration.configure({ document: agentDoc }), caretAt], editable: false });
  agentEditor.commands.setContent("<p>Shrnutí porady: rozpočet schválen.</p>");
  agentAbort = new AbortController();
  agentPresence.plugin.create({ profile: "rich", signal: agentAbort.signal }).onAttach!({ editor: agentEditor, controller: {} as never, signal: agentAbort.signal });
}
onMounted(openAgentEditor);
function playAgentWrite() {
  clearTimeout(agentTimer);
  agentPresence.update([]);
  openAgentEditor();
  agentPresence.update([agentPeer]);
  agentTimer = setTimeout(() => {
    const editor = agentEditor!, doc = agentDoc!;
    // The agent's update: its own Yjs client (the presence's) appends a sentence, as the server's authority writes it.
    const agent = new Y.Doc();
    agent.clientID = agentPeer.clientId;
    Y.applyUpdate(agent, Y.encodeStateAsUpdate(doc));
    const text = (agent.getXmlFragment("default").get(0) as Y.XmlElement).get(0) as Y.XmlText;
    text.insert(text.length, " Další krok: objednat licence do pátku a pozvat tým na školení.");
    Y.applyUpdate(doc, Y.encodeStateAsUpdate(agent, Y.encodeStateVector(doc)));
    agent.destroy();
    editor.view.dispatch(editor.state.tr.setMeta(yCursorPluginKey, editor.state.doc.content.size - 1));
    // The server keeps the agent's caret 5 s (LivePageWrites.CaretLinger): its text is typed in, its stars play „done“, it fades out.
    agentTimer = setTimeout(() => agentPresence.update([]), 5000);
  }, 1200);
}
onBeforeUnmount(() => { clearInterval(pointerTimer); clearTimeout(agentTimer); agentAbort?.abort(); agentEditor?.destroy(); agentDoc?.destroy(); });
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
      <p>Ukazatel spolupracovníka je jeden: na nástěnce (BoardPresence) i nad stránkou (collaborationPresence s <code>pointers: true</code>). Ve Vue pohledu (nástěnka, ukázky vydání) ho kreslí CollaborationPointer.vue, v overlayi stránky stejné značení live/collaborationPointer.ts <code>createPointerElement</code>; jméno, barva a u AI (uživatel „ai:“, <code>isAgentUser</code>) šipka v AI gradientu z tokenů. Pohyb mu dává volající (pointerGlide.ts), stisk a odchod třídy is-pressed a is-leaving. Na stránce se kotví k nejbližšímu místu v textu, takže ukazuje na stejné slovo při jiné šířce okna i posunu. Mezi vzorky (20× za sekundu, po síti nepravidelně) ho nese pružina v každém snímku (live/pointerGlide.ts): drží rychlost a u vzorku se nezastaví; stisk tlačítka vyšle kroužek. Čtenář živé stránky ani dotyk ukazatel neposílají; při omezeném pohybu skáče bez plynulého přechodu.</p>
      <BoardPresence :remote="pointers" :viewport="{ x: 0, y: 0, zoom: 1 }" />
    </div>
    <div class="st-actions">
      <Button variant="outline" :aria-pressed="pointers.length > 0" @click="togglePointers">{{ pointers.length ? "Zastavit ukazatele" : "Přehrát ukazatele" }}</Button>
    </div>
    <div class="pointer-sample agent-sample">
      <p>Zápis AI agenta: když agent (MCP, osobní API, Asistent) zapíše do otevřené stránky, přijde jeho zápis najednou (server nedrží stránku zamčenou), ale editor ho vepisuje po slovech stálým tempem a AI kurzor jede na čele psaní; text je v dokumentu hned, jen ještě neviditelný, takže se nic nepřeskládá. Kurzor má AI gradient, dýchající záři a AI znak, jehož hvězdičky píšou, dokud text nedoběhne, pak jednou přehrají „hotovo“; kurzor pak zmizí prolnutím. Při omezeném pohybu je text vidět hned. Lidé nezáří. Avatar agenta nese iniciály člověka, za kterého píše, v AI inkoustu se září a místo tužky AI znak: dokud agent pracuje, znak píše (hvězda pulzuje a kývá se), s kurzorem přehraje „hotovo“. Ukázka kreslí skutečný overlay nad malým editorem a skutečné CollaboratorAvatars.</p>
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
.pointer-sample { position: relative; min-height: 160px; margin-block: 8px; padding: 12px 16px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); }
</style>
