<script setup lang="ts">
import { ref } from "vue";
import AiFeedback from "../../ai/AiFeedback.vue";
import SettingsSection from "../../settings/SettingsSection.vue";

// AiFeedback (plan phase 3 W8): thumbs for one model output. The component only reports; saving is the parent's job.
const log = ref<string[]>([]);
const note = (text: string) => { log.value = [text, ...log.value].slice(0, 4); };
</script>

<template>
  <SettingsSection title="AiFeedback" description="components/ai/AiFeedback.vue — palce nahoru a dolů pod výstupem modelu. Palec nahoru je jedno kliknutí a mění jen stav tlačítka, bez poděkování. Palec dolů otevře ukotvenou nabídku bez titulku a křížku: Nepřesné · Chybí informace · Špatné zdroje · Nevhodný návrh · Jiné, nepovinnou poznámku do 500 znaků a Odeslat. Proč nové: žádná existující komponenta nehodnotí výstup. Volte ji jen pod výstupem modelu (odpověď, shrnutí, úprava); u pasivních návrhů je zpětnou vazbou Použít nebo Zahodit, palce tam nejsou. Nabídka je Popover, ne dropdown-menu: menu přebírá písmena pro hledání položek a rušilo by psaní poznámky.">
    <div class="aif-example">
      <div class="aif-box">
        <span class="aif-caption">Nové hodnocení</span>
        <AiFeedback @rate="note(`rate ${$event}`)" @feedback="note(`feedback ${$event.reason ?? '—'} · ${$event.note ?? 'bez poznámky'}`)" />
      </div>
      <div class="aif-box">
        <span class="aif-caption">Uložené hodnocení z dřívějška (👍)</span>
        <AiFeedback rating="up" />
      </div>
      <ul class="aif-log" aria-label="Co komponenta nahlásila rodiči"><li v-for="(entry, index) in log" :key="index">{{ entry }}</li><li v-if="!log.length">Zatím nic.</li></ul>
    </div>
  </SettingsSection>
</template>

<style scoped>
.aif-example { display: grid; gap: 12px; max-width: 520px; }
.aif-box { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px; padding: 10px 12px; border: 1px solid var(--line); border-radius: 12px; background: var(--canvas); }
.aif-caption { color: var(--muted); font-size: 12px; font-weight: 600; }
.aif-log { margin: 0; padding: 0 0 0 18px; color: var(--muted); font-size: 12px; }
</style>
