<script setup lang="ts">
import { onBeforeUnmount, ref } from "vue";
import type { RemoteDrag } from "../../boards/boardSession";
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

// Co-editors' pointers: samples arrive 20 times a second, as from the server; each is carried over --atlas-motion-fast.
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
onBeforeUnmount(() => clearInterval(pointerTimer));
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
    <p role="status">{{ selected ? `Ukázka přechodu na kurzor: ${selected}` : 'Zvolte autora nebo přehrajte potvrzení.' }}</p>
    <p>StatusBadge s confirmed použijte až po potvrzené operaci. Pro trvalý stav revize slouží PageStatusBadge. Příchod, odchod a pořadí používají stabilní ID; při omezeném pohybu se zachová text a ikona bez posouvání.</p>
  </SettingsSection>
</template>

<style scoped>
.motion-sample { display: flex; flex-wrap: wrap; align-items: center; gap: 20px; min-height: 64px; margin-block: 8px 16px; }
p { color: var(--muted); font-size: 13px; }
.pointer-sample { position: relative; min-height: 140px; margin-block: 8px; padding: 12px 16px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); }
</style>
