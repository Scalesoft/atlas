<script setup lang="ts">
import { ref } from "vue";
import WorkspaceSidePanel from "../WorkspaceSidePanel.vue";
import AppDialog from "../AppDialog.vue";
import { Button } from "../ui/button";
import { Textarea } from "../ui/textarea";

const open = ref(false);
const dialogOpen = ref(false);
const clicks = ref(0);
</script>

<template>
  <div class="wsp-example">
    <table class="wsp-compare">
      <caption class="sr-only">Kdy modální panel a kdy panel vedle stránky</caption>
      <thead><tr><th scope="col"></th><th scope="col">AppDialog size="side"</th><th scope="col">WorkspaceSidePanel</th></tr></thead>
      <tbody>
        <tr><th scope="row">Stránka vedle</th><td>Inertní, nedá se číst ani ovládat.</td><td>Zůstává čitelná, vybíratelná a ovladatelná; posouvá se vedle panelu.</td></tr>
        <tr><th scope="row">Fokus</th><td>Zachycený v dialogu.</td><td>Nezachycený; Escape zavře, když je fokus uvnitř, a vrátí ho na spouštěč.</td></tr>
        <tr><th scope="row">Použití</th><td>Samostatná úloha: Historie, Čtenost stránky, potvrzení.</td><td>Průvodní plocha, ze které se ověřuje stránka: asistent se zdroji; se záložkami (<code>tabs</code>) dok stránky Obsah · Asistent · Historie · Přístup (Refresh 2.5, ukázka „Stránka: boční panel stránky“).</td></tr>
        <tr><th scope="row">Úzký displej</th><td>Přes celou obrazovku.</td><td>Panel nechává stránce alespoň 600 px. Když se nevejde, překryje pravý okraj; pod 760 px celou obrazovku. Preferovaná šířka se po rozšíření obnoví (asistent 360–480 px, dok stránky od 300 px).</td></tr>
        <tr><th scope="row">Velmi široký displej</th><td>Vystředěný dialog nebo panel u okraje.</td><td>Vždy u pravého okraje okna, i když je text stránky vystředěný a vlevo od panelu zbývá prázdné místo.</td></tr>
        <tr><th scope="row">Pohyb</th><td>Vyroste z .98 za ztmavením.</td><td>Sloupec zabere místo naráz, stránka se na nové místo přesune transformací a panel se vysune z pravého okraje okna (i přes celou obrazovku na úzkém displeji); zavření obráceně, přerušené pokračuje z místa. Panel otevřený bez žádosti čtenáře (zapamatovaný dok) přijde se stránkou bez posunu.</td></tr>
      </tbody>
    </table>
    <p class="st-muted">Zvolte AppDialog, když úloha vyžaduje pozornost a stránka pod ní nemá smysl. Zvolte WorkspaceSidePanel, když uživatel potřebuje zároveň číst, kopírovat nebo klikat ve stránce. Najednou je nejvýše jeden panel; obsah dodává volající přes sloty.</p>
    <div class="st-actions">
      <Button variant="outline" @click="open = !open">{{ open ? 'Zavřít panel vedle stránky' : 'Otevřít panel vedle stránky' }}</Button>
      <Button variant="outline" @click="dialogOpen = true">Porovnat s modálním panelem</Button>
    </div>
    <div class="wsp-stage">
      <article class="wsp-page">
        <h4>Stránka vedle panelu</h4>
        <p>Text lze označit a zkopírovat, tlačítko funguje, i když je panel otevřený.</p>
        <Button variant="secondary" size="sm" @click="clicks++">Kliknuto {{ clicks }}×</Button>
      </article>
      <WorkspaceSidePanel v-model:open="open" label="Ukázkový panel">
        <template #header><strong>Ukázkový panel</strong></template>
        <div class="wsp-body">
          <p>Obsah panelu se posouvá sám. Šířku změníte tažením levého okraje nebo šipkami.</p>
          <Textarea data-autofocus rows="3" aria-label="Ukázkové pole" placeholder="Fokus začíná tady" />
        </div>
        <template #footer><span class="st-muted">Escape zavře panel.</span></template>
      </WorkspaceSidePanel>
    </div>
    <AppDialog title="Modální panel" size="side" :open="dialogOpen" @close="dialogOpen = false"><p>Stránka za tímto panelem je inertní.</p></AppDialog>
  </div>
</template>

<style scoped>
.wsp-example { display: grid; gap: 12px; }
.wsp-compare { width: 100%; border-collapse: collapse; font-size: 13px; }
.wsp-compare th, .wsp-compare td { padding: 6px 8px; border-bottom: 1px solid var(--line); text-align: left; vertical-align: top; }
.wsp-stage { display: flex; min-height: 300px; border: 1px solid var(--line); border-radius: var(--atlas-radius-lg); overflow: hidden; background: var(--surface); }
.wsp-page { flex: 1; min-width: 0; padding: 16px; }
.wsp-body { display: grid; gap: 10px; padding: 12px; }
/* The demo column sits in the box instead of the viewport (in the app it is sticky at 100dvh). */
.wsp-stage :deep(.workspace-panel:not(.is-overlay)) { position: relative; height: auto; align-self: stretch; }
</style>
