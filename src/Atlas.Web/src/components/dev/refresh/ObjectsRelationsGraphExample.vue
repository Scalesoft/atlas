<script setup lang="ts">
import RelationsGraph from "../../objects/RelationsGraph.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import type { RelationGraph } from "../../../objects/relationsApi";
import "../../settings/settings.css";

const id = (n: number) => `a0000000-0000-4000-8000-00000000000${n}`;
const node = (n: number, title: string) => ({ id: id(n), typeId: "page", title, spaceId: "space", spaceName: "Provoz", readable: true });
const graph: RelationGraph = {
  nodes: [node(1, "Serverovna"), node(2, "Instalace"), node(3, "Certifikáty"), node(4, "Nouzový postup")],
  edges: [
    { sourceId: id(1), targetId: id(2), kind: "link", count: 1 },
    { sourceId: id(3), targetId: id(1), kind: "mention", count: 2 },
    { sourceId: id(1), targetId: id(4), kind: "link", count: 1 },
  ],
};
</script>

<template>
  <div class="settings-ui" data-relations-graph-example>
    <SettingsSection title="RelationsGraph" description="RelationsGraph.vue — vztahy objektu jako graf (Vztahy stránky, panel Použito na): BoardCanvas jen ke čtení nad odvozenými prvky. Najetí rozsvítí sousedy (Shift celý řetěz), šipky chodí k nejbližšímu uzlu, Ctrl+Enter objekt otevře, Esc vrací, uzel jde přetáhnout jako odkaz. Zvolte ho, když se ptáte „co s čím souvisí“; seznam míst použití zůstává UsagePanel, volné rozmístění vlastních prvků BoardCanvas.">
      <div class="relations-example-stage">
        <RelationsGraph :graph="graph" :focus-id="id(1)" label="Vztahy stránky Serverovna" />
      </div>
    </SettingsSection>
  </div>
</template>

<style scoped>
.relations-example-stage { height: 360px; min-width: 0; }
</style>
