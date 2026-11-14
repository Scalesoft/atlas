<script setup lang="ts">
import { onBeforeUnmount } from "vue";
import PageTemplatesPage from "../../PageTemplatesPage.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { pageTemplateApi } from "../../../pageTemplateApi";
import type { Space } from "../../../api";
import "../../settings/settings.css";

// The list is read from the server; the example answers it locally for as long as it is shown.
const original = pageTemplateApi.list;
pageTemplateApi.list = async () => [];
onBeforeUnmount(() => { pageTemplateApi.list = original; });
const space = { id: "gallery", name: "Provoz" } as Space;
</script>

<template>
  <div class="settings-ui" data-page-templates-example>
    <SettingsSection title="PageTemplatesPage" description="PageTemplatesPage.vue — Nastavení prostoru → Šablony stránek: šablony jsou obyčejné stránky, řádek jen otevře stránku, nová šablona vzniká běžným vytvořením stránky; řádek rozbalí Použito na (stránky vytvořené ze šablony). Ukázka je prázdný prostor: prázdný stav s Nová šablona. Zvolte ji pro seznam jednoho druhu stránek prostoru; obecný seznam s řazením je SettingsList.">
      <PageTemplatesPage :space="space" />
    </SettingsSection>
  </div>
</template>
