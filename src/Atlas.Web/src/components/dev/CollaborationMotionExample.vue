<script setup lang="ts">
import { ref } from "vue";
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
    <p role="status">{{ selected ? `Ukázka přechodu na kurzor: ${selected}` : 'Zvolte autora nebo přehrajte potvrzení.' }}</p>
    <p>StatusBadge s confirmed použijte až po potvrzené operaci. Pro trvalý stav revize slouží PageStatusBadge. Příchod, odchod a pořadí používají stabilní ID; při omezeném pohybu se zachová text a ikona bez posouvání.</p>
  </SettingsSection>
</template>

<style scoped>
.motion-sample { display: flex; flex-wrap: wrap; align-items: center; gap: 20px; min-height: 64px; margin-block: 8px 16px; }
p { color: var(--muted); font-size: 13px; }
</style>
