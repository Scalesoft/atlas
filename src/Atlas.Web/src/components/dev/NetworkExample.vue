<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import ChoiceCards from "../settings/ChoiceCards.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import SettingsRow from "../settings/SettingsRow.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import SummaryList from "../settings/SummaryList.vue";
import ResultBanner from "../settings/ResultBanner.vue";
import { parseCidr } from "../settings/security/cidr";

// Gallery composition only: no settings persistence or network policy.
const kind = ref<string | string[] | null>(null);
const name = ref("");
const cidr = ref("");
const description = ref("");
const completed = ref(false);
const range = computed(() => parseCidr(cidr.value));
const ready = computed(() => !!kind.value && !!name.value.trim() && range.value.ok);
const summary = computed(() => range.value.ok ? [
  { key: "name", icon: "network", label: "Název", value: name.value },
  { key: "kind", icon: "shield-check", label: "Připojení", value: kind.value === "vpn" ? "VPN" : "Kancelář" },
  { key: "range", icon: "map-pin", label: "Rozsah", value: range.value.cidr, code: true },
] : []);
watch([kind, name, cidr, description], () => { completed.value = false; });
function reset() {
  kind.value = null;
  name.value = cidr.value = description.value = "";
  completed.value = false;
}
</script>

<template>
  <form class="network-example" @submit.prevent="completed = ready">
    <ChoiceCards v-model="kind" label="Jaké připojení přidáváte?" :options="[
      { value: 'vpn', label: 'VPN', description: 'Vzdálené připojení týmu.', icon: 'shield-check' },
      { value: 'office', label: 'Kancelář', description: 'Síť na pracovišti.', icon: 'network' },
    ]" />
    <InlineReveal :open="!!kind">
      <SettingsRow v-slot="{ id, describedBy }" label="Název" help="Pojmenujte připojení pro přehled sítě." stacked>
        <Input :id="id" v-model="name" :aria-describedby="describedBy" placeholder="VPN pro externisty" />
      </SettingsRow>
      <SettingsRow v-slot="{ id, describedBy, invalid }" label="Rozsah (CIDR)" help="IPv4 nebo IPv6 s délkou prefixu. Například 10.4.0.0/22."
        :error="cidr && !range.ok ? range.error : undefined" stacked>
        <Input :id="id" v-model="cidr" :aria-describedby="describedBy" :aria-invalid="invalid" placeholder="10.4.0.0/22" />
      </SettingsRow>
      <InlineReveal :open="ready">
        <SummaryList label="Souhrn sítě" :items="summary" />
        <p v-if="range.ok && range.adjusted" class="st-muted">Rozsah je upraven na začátek sítě: {{ range.cidr }}.</p>
        <SettingsSection title="Doplňující údaje" advanced :summary="description || 'Popis není vyplněn.'">
          <SettingsRow v-slot="{ id }" label="Popis" stacked><Input :id="id" v-model="description" placeholder="Komu síť patří, kdo ji spravuje" /></SettingsRow>
        </SettingsSection>
      </InlineReveal>
    </InlineReveal>
    <div class="st-actions">
      <Button type="submit" :disabled="!ready">Uložit ukázku</Button>
      <Button type="button" variant="ghost" @click="reset">Začít znovu</Button>
    </div>
    <ResultBanner v-if="completed" message="Ukázka potvrzena. Nic se neuložilo." @dismiss="completed = false" />
  </form>
</template>

<style scoped>
.network-example { display: grid; gap: 16px; }
</style>
