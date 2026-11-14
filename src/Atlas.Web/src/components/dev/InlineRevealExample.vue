<script setup lang="ts">
import { ref } from "vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import InlineReveal from "../settings/InlineReveal.vue";
import AppDialog from "../AppDialog.vue";
import ActionDisclosure from "../ActionDisclosure.vue";
import ActionDisclosureItem from "../ActionDisclosureItem.vue";

const open = ref(false);
const extended = ref(false);
const nested = ref(false);
const dialogOpen = ref(false);
const selected = ref("Zatím beze změny");
</script>

<template>
  <div class="reveal-example" data-motion-lifecycle-example>
    <p>Rychle opakujte otevření a zavření, nebo změňte délku otevřeného obsahu. Rozbalení plynule navazuje na právě viditelnou výšku; zavíraná pole už nepřebírají fokus.</p>
    <div class="st-actions">
      <Button variant="outline" aria-controls="gallery-reveal-lifecycle" :aria-expanded="open" @click="open = !open">{{ open ? 'Sbalit podrobnosti' : 'Rozbalit podrobnosti' }}</Button>
      <Button variant="ghost" :disabled="!open" @click="extended = !extended">{{ extended ? 'Zkrátit obsah' : 'Přidat obsah' }}</Button>
      <Button variant="outline" @click="dialogOpen = true">Dialog s nabídkou akcí</Button>
    </div>
    <InlineReveal id="gallery-reveal-lifecycle" v-model:open="open" keep-mounted>
      <div class="reveal-example-body">
        <label>Poznámka <Input aria-label="Poznámka k ukázce" placeholder="Text zůstane zachovaný i po sbalení" /></label>
        <p v-if="extended">Delší vysvětlení změny nastavení doplňuje původní krátký obsah. Při zúžení okna se řádky zalamují a výška se přizpůsobí bez oříznutí posledního řádku nebo prázdného místa.</p>
        <Button variant="ghost" aria-controls="gallery-reveal-nested" :aria-expanded="nested" @click="nested = !nested">Další možnosti</Button>
        <InlineReveal id="gallery-reveal-nested" v-model:open="nested"><p>Vnořené podrobnosti se rozbalují současně s nadřazenou částí.</p></InlineReveal>
      </div>
    </InlineReveal>
    <AppDialog title="Nabídka uvnitř dialogu" :open="dialogOpen" @close="dialogOpen = false">
      <div class="reveal-example-body">
        <p>Escape nejprve zavře nabídku, druhé stisknutí dialog. Fokus se vrací na odpovídající ovládací prvek.</p>
        <ActionDisclosure label="Akce uvnitř dialogu"><ActionDisclosureItem @click="selected = 'První akce vybrána'">První akce</ActionDisclosureItem><ActionDisclosureItem @click="selected = 'Poslední akce vybrána'">Poslední akce</ActionDisclosureItem></ActionDisclosure>
        <p aria-live="polite">{{ selected }}</p>
      </div>
    </AppDialog>
  </div>
</template>

<style scoped>
.reveal-example { display: grid; gap: 12px; }
.reveal-example > p { margin: 0; color: var(--muted); font-size: 13px; }
.reveal-example-body { display: grid; gap: 12px; padding: 16px; }
.reveal-example-body p { margin: 0; }
.reveal-example-body label { display: grid; gap: 6px; }
</style>
