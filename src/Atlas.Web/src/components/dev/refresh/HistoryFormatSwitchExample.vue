<script setup lang="ts">
import HistoryFormatSwitch from "../../HistoryFormatSwitch.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import "../../settings/settings.css";
</script>

<template>
  <div class="settings-ui" data-history-format-example>
    <SettingsSection title="HistoryFormatSwitch" description="HistoryFormatSwitch.vue — přepínač Náhled | Markdown v Historii stránky: verze jako se čte, nebo její uložený Markdown. Volba platí pro všechna zobrazení historie a pamatuje se (useHistoryFormat). Zvolte ji tam, kde se jedna verze ukazuje ve dvou podobách; nezávislé volby filtru jsou OptionPills, výběr z více možností FilterMenu.">
      <HistoryFormatSwitch />
    </SettingsSection>
  </div>
</template>
