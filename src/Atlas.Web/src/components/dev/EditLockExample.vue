<script setup lang="ts">
import { ref } from "vue";
import { Button } from "../ui/button";
import EditLockToggle from "../EditLockToggle.vue";

const locked = ref(true);
const pulse = ref(0);
</script>

<template>
  <div class="edit-lock-example" data-edit-lock-example>
    <p class="st-muted">
      Zámek úprav stránky (EditLockToggle.vue) je tlačítko s ikonou ze základního Button, ne přepínač Switch ani SpacePinToggle: ukládá stav „stránka je zamčená“ s animovaným třmenem
      (zvedne se a pootočí, nebo zaklapne) a umí upozornit pulzem, když čtenář zkusí psát. Pulz se nerestartuje, dokud předchozí nedoběhne.
      Na rozdíl od připnutí nepřepíná obsah, jen to, jestli jde stránka upravovat.
      Zamčený se zámek mění v neutrální odznak „Jen pro čtení“ a hned za ním stojí primární tlačítko Upravit, obojí před publikací:
      zapisovatel na první pohled vidí, že stránka je jen ke čtení a čím přepne. Upravit je totéž odemčení (E), jen pojmenované;
      odemyká jen otevřenou stránku nebo tabuli, znovu se zamyká odznakem. Pokus psát rozbliká odznak i Upravit zároveň, jednou za
      dávku stisků. Na telefonu se odznak zúží na ikonu (název zůstává v aria-label a tipu) a Upravit u stránky nese spodní lišta.
      Stránky se otevřou zamčené volbou profilu „Stránky otevírat: jen pro čtení“ (výchozí je „rovnou k úpravám“). Žádný zvláštní
      režim není: editor je pořád týž, jen needitovatelný.
    </p>
    <div class="edit-lock-row">
      <figure><span class="edit-lock-pair"><EditLockToggle :locked="true" shortcut="E" /></span><figcaption>Jen pro čtení</figcaption></figure>
      <figure><span class="edit-lock-pair"><EditLockToggle :locked="false" shortcut="E" /></span><figcaption>Odemčeno</figcaption></figure>
      <figure><span class="edit-lock-pair"><EditLockToggle :locked="locked" :pulse="pulse" shortcut="E" @toggle="locked = !locked" /></span><figcaption>Živý zámek</figcaption></figure>
      <Button type="button" variant="outline" data-edit-lock-try @click="pulse++">Zkusit psát do zamčené stránky</Button>
    </div>
    <div class="edit-lock-row" data-atlas-reduced-motion>
      <figure><span class="edit-lock-pair"><EditLockToggle :locked="locked" :pulse="pulse" shortcut="E" @toggle="locked = !locked" /></span><figcaption>Omezený pohyb: bez pohybu, pulz je zvýraznění barvou a obrysem</figcaption></figure>
    </div>
  </div>
</template>

<style scoped>
.edit-lock-example { display: grid; gap: 16px; min-width: 0; }
.edit-lock-example > p { margin: 0; }
.edit-lock-row { display: flex; flex-wrap: wrap; align-items: center; gap: 16px 28px; }
/* As in the bar's document-primary-actions: the badge and Upravit side by side. */
.edit-lock-pair { display: flex; align-items: center; gap: 4px; }
figure { display: grid; justify-items: start; gap: 6px; margin: 0; }
figcaption { color: var(--muted); font-size: 12px; }
</style>
