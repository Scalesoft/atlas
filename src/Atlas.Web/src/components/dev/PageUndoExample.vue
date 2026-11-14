<script setup lang="ts">
import { ref } from "vue";
import { Button } from "../ui/button";
import PageUndoWedge from "../PageUndoWedge.vue";

// Local stand-in for the page editor's undo stack: a change adds a step, the wedge takes one back.
const steps = ref(0);
const locked = ref(false);
</script>

<template>
  <div class="page-undo-example" data-page-undo-example>
    <p class="st-muted">
      PageUndoWedge.vue — „Zpět“ v levém horním rohu stránky, pod horní lištou: tichý klín, který při posouvání zůstává na místě. Dělá přesně
      to, co Ctrl+Z v editoru stránky (příkaz editoru <code>undo</code>), a je vidět jen pisateli na odemčené stránce, když je co vracet. Stisk
      nebere editoru fokus ani výběr. Stejný klín je i nad tabulí (BoardView) v rohu vlevo nahoře: vrací krok tabule (karty, přesuny) a je vidět jen pisateli na odemčené
      tabuli; při úpravě textu nebo náhledu stránky vrací krok toho editoru (jako Ctrl+Z v něm). BoardToolRail si ponechává své „Zpět“ a „Znovu“ jako stálá tlačítka; text karty tabule se vrací Ctrl+Z jako na stránce.
    </p>
    <div class="st-actions">
      <Button type="button" variant="outline" data-page-undo-change @click="steps++">Udělat změnu</Button>
      <Button type="button" variant="ghost" :aria-pressed="locked" @click="locked = !locked">{{ locked ? "Odemknout" : "Zamknout úpravy" }}</Button>
      <span class="st-muted">Kroků k vrácení: {{ steps }}</span>
    </div>
    <div class="page-undo-frame">
      <PageUndoWedge :editable="!locked" :can-undo="steps > 0" @undo="steps--" />
      <h3>Postup zakázky</h3>
      <p v-for="index in 6" :key="index">Texty stránek se upravují přímo na místě. Každá změna se ukládá průběžně a lze ji vrátit klávesou Ctrl+Z nebo tlačítkem vlevo nahoře.</p>
    </div>
  </div>
</template>

<style scoped>
.page-undo-example { display: grid; gap: 12px; min-width: 0; }
.page-undo-example > p { margin: 0; }
/* The page's own scroller in small: the wedge sticks to its top (no workspace bar here). */
.page-undo-frame { --atlas-workspace-bar-height: 0px; height: 260px; overflow: auto; border: 1px solid var(--line); border-radius: var(--atlas-radius-md, 10px); background: var(--surface); }
.page-undo-frame > h3 { margin: 0; padding: 48px 40px 8px; font-size: 24px; }
.page-undo-frame > p { margin: 0; padding: 0 40px 12px; }
</style>
