<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import { Editor } from "@tiptap/core";
import StarterKit from "@tiptap/starter-kit";
import { createMarkdownStubbedPopover } from "@atlas/markdown/core";
import { Button } from "../ui/button";

// Gallery example of the stub → load → swap helper (Atlas.Markdown features/modules/stubbedPopover.ts). The "editor" here is a fake
// chunk that arrives after the chosen delay; the helper is the real one. Studená = every opening downloads again (a first use).
const delay = ref(400);
const cold = ref(true);
const host = ref<HTMLElement | null>(null);
let editor: Editor | undefined;
onMounted(() => { editor = new Editor({ element: host.value!, extensions: [StarterKit], content: "<p>Text stránky, u kterého se otevírá editor komponenty.</p>" }); });
onBeforeUnmount(() => editor?.destroy());

const loader = () => new Promise<{ draw(container: HTMLElement): void }>(resolve => setTimeout(() => resolve({
  draw(container) {
    const form = document.createElement("form");
    for (const label of ["Název", "Popisek", "Adresa"]) {
      const row = document.createElement("label"); row.textContent = label;
      const input = document.createElement("input"); input.className = "md-context-input"; row.append(input); form.append(row);
    }
    container.append(form);
  },
}), delay.value));
let popover = createMarkdownStubbedPopover(loader);
function open(event: MouseEvent) {
  if (!editor) return;
  if (cold.value) popover = createMarkdownStubbedPopover(loader);
  popover.open({ editor, anchor: event.currentTarget as HTMLElement, title: "Nastavení komponenty", rows: 3, rowHeight: 45, render: (module, container) => module.draw(container) });
}
</script>

<template>
  <div class="stubbed-example">
    <p>Editor komponenty se otevře okamžitě jako rámec se skeletonem stejné velikosti a vstoupí jako každá nabídka; skutečný editor se doplní, až doběhne vstup i stažení, beze změny velikosti. Po prodlevě pohybu se objeví řádky skeletonu, rychlé načtení neblikne. Psaní v prvním snímku se předá prvnímu poli. Zkuste pomalé načtení a omezený pohyb nahoře.</p>
    <div class="st-actions">
      <Button variant="outline" @click="open">Otevřít nastavení komponenty</Button>
      <label>Stažení trvá <select v-model.number="delay" aria-label="Doba stažení editoru"><option :value="0">hned</option><option :value="400">0,4 s</option><option :value="1500">1,5 s</option></select></label>
      <label><input v-model="cold" type="checkbox" /> Poprvé (bez cache)</label>
    </div>
    <div ref="host" class="stubbed-example-page" />
  </div>
</template>
