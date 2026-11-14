<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { disclosureHooks } from "../disclosure";
import ActionDisclosure from "../../ActionDisclosure.vue";
import ActionDisclosureItem from "../../ActionDisclosureItem.vue";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import { errorMessage } from "../../../api";
import {
  connectionApi, networksApi, proxiesApi,
  type AddressTestResult, type ConnectionView, type NetworkKind, type TrustedAddress, type TrustedNetworkList, type TrustedProxyList,
} from "../../../protection";
import ConfirmWithReauth from "../ConfirmWithReauth.vue";
import ConflictNote from "../ConflictNote.vue";
import DecisionChain from "../DecisionChain.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import InlineReveal from "../InlineReveal.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import OptionPills from "../OptionPills.vue";
import t from "../../../i18n/settings-global";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import type { ChoiceOption, ImpactItem } from "../types";
import { useSettingsFlash } from "../flash";
import { useSettingsRoute } from "../useSettingsRoute";
import NetworkRuleWizard from "../security/NetworkRuleWizard.vue";
import { formatAddressCount, parseAddress, parseCidr, rangeContains, type ParsedRange } from "../security/cidr";
import { connectionSteps, networkIcon, networkKinds, testerSteps } from "../security/connection";
import { modeInfo } from "../security/protection";
import { conflictMessage, isConflict, securityErrorText } from "../security/securityErrors";
import type { NetworkRuleDraft } from "../security/types";

// Sítě a proxy (global security, every wiki administrator): your connection as the server sees it, the trusted networks with an
// active switch, and (advanced) the trusted proxies, the hop limit and an address tester. Every change is confirmed with a fresh
// sign-in and carries the configuration version; a 409 reloads and says so.
const props = defineProps<{ adding: boolean }>();
const router = useSettingsRoute();
const flash = useSettingsFlash();

const connection = ref<ConnectionView>();
const networks = ref<TrustedNetworkList>();
const proxies = ref<TrustedProxyList>();
const loading = ref(true);
const error = ref("");
const conflict = ref("");
const busy = ref(false);
const actionError = ref("");
let disposed = false;

async function load() {
  loading.value = true;
  try {
    const [view, networkList, proxyList] = await Promise.all([connectionApi.get(), networksApi.list(), proxiesApi.list()]);
    if (disposed) return;
    connection.value = view;
    networks.value = networkList;
    proxies.value = proxyList;
    hopsDraft.value = String(proxyList.hopLimit);
    error.value = "";
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
onMounted(load);
onBeforeUnmount(() => (disposed = true));

const diagnosis = computed(() => (connection.value && proxies.value ? connectionSteps(connection.value, proxies.value.hopLimit) : []));
const size = (range: string) => {
  const parsed = parseCidr(range);
  return parsed.ok ? formatAddressCount(parsed.size) : "";
};
const kindLabel = (kind: NetworkKind | null) => (kind ? networkKinds[kind].label : t.networks.kindProxy);

/** One immediate change at a time, confirmed in place under its row. */
type Pending =
  | { kind: "toggle"; item: TrustedAddress; active: boolean; list: "networks" | "proxies" }
  | { kind: "remove"; item: TrustedAddress; list: "networks" | "proxies" }
  | { kind: "hops"; hops: number }
  | { kind: "proxy"; name: string; range: string };
const pending = ref<Pending>();
function ask(change: Pending) {
  actionError.value = "";
  pending.value = change;
}
/** The words of a row's confirmation by what changes (UX-N-7): removing, switching on or switching off a network or a proxy. */
function confirmCopy(item: TrustedAddress, list: "networks" | "proxies") {
  const change = pendingFor(item);
  const action = change?.kind === "remove" ? "remove" : change?.kind === "toggle" && change.active ? "on" : "off";
  const words = t.networks.confirm[list];
  return { title: words[action].title(item.name), label: words[action].label, note: action === "on" ? words.noteOn : words.noteOff };
}
function pendingFor(item: TrustedAddress) {
  const change = pending.value;
  return change && (change.kind === "toggle" || change.kind === "remove") && change.item.id === item.id ? change : undefined;
}
function cancel() {
  pending.value = undefined;
  actionError.value = "";
  hopsDraft.value = String(proxies.value?.hopLimit ?? 1);
}

async function run(action: () => Promise<unknown>, success: string, icon = "circle-check") {
  busy.value = true;
  actionError.value = "";
  try {
    await action();
    pending.value = undefined;
    flash.notify(success, { icon });
    await load();
    return true;
  } catch (failure) {
    if (isConflict(failure)) {
      pending.value = undefined;
      conflict.value = conflictMessage();
      await load();
    } else actionError.value = securityErrorText(failure);
    return false;
  } finally {
    busy.value = false;
  }
}

async function confirmPending(proof: string | null) {
  const change = pending.value;
  if (!change || !networks.value || !proxies.value) return;
  if (change.kind === "toggle") {
    const api = change.list === "networks" ? networksApi : proxiesApi;
    const version = change.list === "networks" ? networks.value.version : proxies.value.version;
    const { item, active } = change;
    await run(
      () => api.update(item.id, { name: item.name, kind: item.kind, range: item.range, isActive: active, expectedVersion: version }, proof),
      (change.list === "networks" ? t.networks.networkToggled : t.networks.proxyToggled)(item.name, active),
    );
  } else if (change.kind === "remove") {
    const api = change.list === "networks" ? networksApi : proxiesApi;
    const version = change.list === "networks" ? networks.value.version : proxies.value.version;
    await run(() => api.remove(change.item.id, version, proof), (change.list === "networks" ? t.networks.networkRemoved : t.networks.proxyRemoved)(change.item.name), "trash");
  } else if (change.kind === "hops") {
    await run(() => proxiesApi.setHopLimit(change.hops, proxies.value!.version, proof), t.networks.hopsSet(change.hops));
  } else {
    const done = await run(
      () => proxiesApi.create({ name: change.name, range: change.range, isActive: true, expectedVersion: proxies.value!.version }, proof),
      t.networks.proxyAdded(change.name),
    );
    if (done) {
      proxyName.value = "";
      proxyRange.value = "";
      addingProxy.value = false;
    }
  }
}

// Proxies and hops
const hopsDraft = ref("1");
const hopOptions = computed<ChoiceOption[]>(() =>
  Array.from({ length: proxies.value?.maxHopLimit ?? 3 }, (_, index) => index + 1).map((hops) => ({ value: String(hops), label: t.counts.hops(hops), icon: "server" })),
);
function changeHops(value: string) {
  hopsDraft.value = value;
  const hops = Number(value);
  if (hops === proxies.value?.hopLimit) cancel();
  else ask({ kind: "hops", hops });
}
const recovery = computed(() => !!proxies.value?.environment.recovery);
const proxySummary = computed(() => {
  const list = proxies.value;
  if (!list) return "";
  const active = list.items.filter((item) => item.isActive).length;
  return t.networks.proxySummary(t.counts.trustedProxies(active), t.counts.hops(list.hopLimit));
});
const addingProxy = ref(false);
const proxyName = ref("");
const proxyRange = ref("");
const proxyParsed = computed(() => parseCidr(proxyRange.value));
const proxyReady = computed(() => !!proxyName.value.trim() && proxyParsed.value.ok);

// Address tester
const testerAddress = ref("");
const tester = ref<AddressTestResult>();
const testing = ref(false);
const testerError = ref("");
async function test() {
  if (!testerAddress.value.trim()) return;
  testing.value = true;
  testerError.value = "";
  try {
    tester.value = await networksApi.test({ address: testerAddress.value.trim() });
  } catch (failure) {
    testerError.value = errorMessage(failure);
  } finally {
    testing.value = false;
  }
}

// Přidat síť
const activeRules = computed(() => (networks.value?.items ?? []).filter((item) => item.isActive).map((item) => ({ name: item.name, cidr: item.range })));
function networkImpact(range: ParsedRange, kind: NetworkKind): ImpactItem[] {
  const address = connection.value?.address ? parseAddress(connection.value.address) : undefined;
  const inside = !!address && rangeContains(range, address);
  const mode = connection.value?.instanceMode ?? "always";
  return [
    { icon: networkKinds[kind].icon, label: t.networks.impact.fromTrusted(range.cidr, formatAddressCount(range.size)) },
    { icon: "network", tone: "success", label: t.networks.impact.noReauth(modeInfo.outsideTrustedNetworks.label), detail: t.networks.impact.noReauthDetail(modeInfo[mode].label) },
    { icon: "lock", label: t.networks.impact.always },
    connection.value?.address
      ? { icon: inside ? "circle-check" : "info", tone: inside ? "success" : "info", label: inside ? t.networks.impact.inside(connection.value.address) : t.networks.impact.outside(connection.value.address) }
      : { icon: "info", tone: "info", label: t.networks.impact.unknown },
  ];
}
async function saveNetwork(draft: NetworkRuleDraft, proof: string | null) {
  if (!networks.value) return;
  busy.value = true;
  actionError.value = "";
  try {
    const saved = await networksApi.create({ name: draft.name, kind: draft.kind, range: draft.cidr, isActive: true, expectedVersion: networks.value.version }, proof);
    flash.notify(t.networks.networkAdded(draft.name, saved.warnings.join(" ")), { icon: networkKinds[draft.kind].icon, highlight: saved.id });
    router.navigate("security/networks");
  } catch (failure) {
    if (isConflict(failure)) {
      conflict.value = conflictMessage();
      router.navigate("security/networks");
    } else actionError.value = securityErrorText(failure);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <SettingsPage :title="t.networks.title" :description="t.networks.description">
    <template v-if="!props.adding && networks" #actions>
      <Button as-child><a :href="router.href('security/networks/new')" data-add-network><AtlasIcon name="plus" :size="16" />{{ t.networks.add }}</a></Button>
    </template>
    <ConflictNote v-if="conflict" :text="conflict" data-network-conflict @dismiss="conflict = ''" />
    <ErrorState v-if="error" :title="t.networks.loadFailed" :text="error" :busy="loading" @retry="load" />
    <LoadingSkeleton v-else-if="loading && !networks" :label="t.networks.loading" />
    <template v-else-if="networks && proxies && connection">
      <SettingsSection v-if="props.adding" :label="t.networks.addTrusted">
        <NetworkRuleWizard :existing="activeRules" :impact="networkImpact" :busy="busy" :error="actionError" @save="saveNetwork" @cancel="router.navigate('security/networks')" />
      </SettingsSection>
      <template v-else>
        <SettingsSection :title="t.networks.yourConnection" icon="activity" :description="t.networks.yourConnectionText">
          <template #title-extra>
            <StatusBadge :tone="connection.network ? 'success' : 'neutral'" :icon="connection.network ? 'building-2' : 'globe'" :label="connection.network ? t.networks.trustedNetwork : t.networks.otherNetwork" size="sm" />
          </template>
          <DecisionChain :steps="diagnosis" :label="t.networks.chainLabel" data-connection />
        </SettingsSection>

        <SettingsSection :title="t.networks.trusted" icon="building-2" :count="networks.items.length">
          <SettingsList v-if="networks.items.length" :label="t.networks.trusted">
            <SettingsListRow
              v-for="item in networks.items"
              :key="item.id"
              :title="item.name"
              :icon="networkIcon(item.kind)"
              :icon-tone="item.isActive ? undefined : 'neutral'"
              :highlighted="flash.highlight.value === item.id"
              :data-network="item.id"
            >
              <template #description><code class="st-mono">{{ item.range }}</code> · {{ size(item.range) }} · {{ kindLabel(item.kind) }}</template>
              <template #meta>
                <StatusBadge :tone="item.isActive ? 'success' : 'neutral'" :icon="item.isActive ? 'circle-check' : 'circle-x'" :label="item.isActive ? t.networks.active : t.networks.off" size="sm" />
                <StatusBadge v-for="warning in item.warnings" :key="warning" tone="warning" icon="triangle-alert" :label="warning" size="sm" />
              </template>
              <template #actions>
                <Switch :model-value="pendingFor(item)?.kind === 'toggle' ? (pendingFor(item) as { active: boolean }).active : item.isActive" :disabled="busy" :aria-label="t.networks.activeAria(item.name)" data-network-active @update:model-value="ask({ kind: 'toggle', item, active: $event, list: 'networks' })" />
                <ActionDisclosure :label="t.networks.moreActions(item.name)" :disabled="busy">
                  <ActionDisclosureItem :disabled="busy" :aria-label="t.networks.removeNetworkAria(item.name)" data-network-remove @click="ask({ kind: 'remove', item, list: 'networks' })"><AtlasIcon name="trash" :size="16" />{{ t.networks.removeNetwork }}</ActionDisclosureItem>
                </ActionDisclosure>
              </template>
              <InlineReveal :open="!!pendingFor(item)">
                <div class="network-confirm">
                  <ConfirmWithReauth :title="confirmCopy(item, 'networks').title" :confirm-label="confirmCopy(item, 'networks').label" :cancel-label="t.networks.cancel" :busy="busy" :error="actionError" @confirm="confirmPending" @cancel="cancel">
                    <p class="st-note"><AtlasIcon name="info" :size="15" />{{ confirmCopy(item, "networks").note }}</p>
                  </ConfirmWithReauth>
                </div>
              </InlineReveal>
            </SettingsListRow>
          </SettingsList>
          <EmptyState v-else icon="network" :title="t.networks.emptyTitle" :text="t.networks.emptyText" compact>
            <Button as-child variant="outline"><a :href="router.href('security/networks/new')"><AtlasIcon name="plus" :size="16" />{{ t.networks.add }}</a></Button>
          </EmptyState>
        </SettingsSection>

        <SettingsSection :title="t.networks.proxiesTitle" icon="server" advanced :summary="proxySummary" :description="t.networks.proxiesDescription">
          <p v-if="recovery" class="st-note proxy-warning" role="status"><AtlasIcon name="shield-alert" :size="15" />{{ t.networks.recovery }}</p>
          <p v-else-if="proxies.environment.differs" class="st-note" role="status"><AtlasIcon name="info" :size="15" />{{ t.networks.differs(proxies.environment.addresses) }}</p>
          <SettingsList v-if="proxies.items.length" :label="t.networks.proxiesLabel">
            <SettingsListRow v-for="item in proxies.items" :key="item.id" :title="item.name" icon="server" :icon-tone="item.isActive ? undefined : 'neutral'" :data-proxy="item.id">
              <template #description><code class="st-mono">{{ item.range }}</code></template>
              <template #meta>
                <StatusBadge :tone="item.isActive ? 'success' : 'neutral'" :label="item.isActive ? t.networks.trustedBadge : t.networks.off" size="sm" />
                <StatusBadge v-for="warning in item.warnings" :key="warning" tone="warning" icon="triangle-alert" :label="warning" size="sm" />
              </template>
              <template v-if="!recovery" #actions>
                <Switch :model-value="pendingFor(item)?.kind === 'toggle' ? (pendingFor(item) as { active: boolean }).active : item.isActive" :disabled="busy" :aria-label="t.networks.trustProxyAria(item.name)" @update:model-value="ask({ kind: 'toggle', item, active: $event, list: 'proxies' })" />
                <ActionDisclosure :label="t.networks.moreActions(item.name)" :disabled="busy">
                  <ActionDisclosureItem :disabled="busy" :aria-label="t.networks.removeProxyAria(item.name)" @click="ask({ kind: 'remove', item, list: 'proxies' })"><AtlasIcon name="trash" :size="16" />{{ t.networks.removeProxy }}</ActionDisclosureItem>
                </ActionDisclosure>
              </template>
              <InlineReveal :open="!!pendingFor(item)">
                <div class="network-confirm">
                  <ConfirmWithReauth :title="confirmCopy(item, 'proxies').title" :confirm-label="confirmCopy(item, 'proxies').label" :cancel-label="t.networks.cancel" :busy="busy" :error="actionError" @confirm="confirmPending" @cancel="cancel">
                    <p class="st-note"><AtlasIcon name="info" :size="15" />{{ confirmCopy(item, "proxies").note }}</p>
                  </ConfirmWithReauth>
                </div>
              </InlineReveal>
            </SettingsListRow>
          </SettingsList>
          <p v-else class="st-note"><AtlasIcon name="info" :size="15" />{{ t.networks.noProxies }}</p>

          <div v-if="!recovery" class="proxy-add">
            <Button v-if="!addingProxy" variant="outline" size="sm" type="button" :disabled="busy" data-add-proxy @click="addingProxy = true"><AtlasIcon name="plus" :size="15" />{{ t.networks.addProxy }}</Button>
            <InlineReveal :open="addingProxy">
              <div class="proxy-form">
                <SettingsRow v-slot="{ id, describedBy }" :label="t.networks.proxyName" :help="t.networks.proxyNameHelp" stacked>
                  <input :id="id" v-model="proxyName" class="st-input" :aria-describedby="describedBy" data-proxy-name />
                </SettingsRow>
                <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.networks.proxyRange" :help="t.networks.proxyRangeHelp" :error="proxyRange && !proxyParsed.ok ? (proxyParsed as { error: string }).error : undefined" stacked>
                  <input :id="id" v-model="proxyRange" class="st-input st-mono" spellcheck="false" autocomplete="off" :aria-describedby="describedBy" :aria-invalid="invalid" data-proxy-range />
                </SettingsRow>
                <InlineReveal :open="pending?.kind === 'proxy'">
                  <ConfirmWithReauth :title="t.networks.addProxyTitle" :confirm-label="t.networks.addProxy" :cancel-label="t.networks.cancel" :busy="busy" :error="actionError" @confirm="confirmPending" @cancel="cancel" />
                </InlineReveal>
                <div v-if="pending?.kind !== 'proxy'" class="st-actions">
                  <Button size="sm" type="button" :disabled="!proxyReady || busy" @click="ask({ kind: 'proxy', name: proxyName.trim(), range: proxyParsed.ok ? proxyParsed.cidr : proxyRange })"><AtlasIcon name="check" :size="15" />{{ t.networks.continue }}</Button>
                  <Button variant="ghost" size="sm" type="button" @click="addingProxy = false; cancel()">{{ t.networks.cancel }}</Button>
                </div>
              </div>
            </InlineReveal>
          </div>

          <OptionPills :model-value="hopsDraft" :options="hopOptions" :label="t.networks.hopsLabel" data-hops @update:model-value="changeHops" />
          <InlineReveal :open="pending?.kind === 'hops'">
            <div class="network-confirm">
              <ConfirmWithReauth :title="t.networks.saveHopsTitle" :confirm-label="t.networks.saveHops" :cancel-label="t.networks.cancel" :busy="busy" :error="actionError" @confirm="confirmPending" @cancel="cancel" />
            </div>
          </InlineReveal>

          <form class="network-tester" @submit.prevent="test">
            <h4><AtlasIcon name="search" :size="16" />{{ t.networks.testerTitle }}</h4>
            <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.networks.testerNote }}</p>
            <div class="network-tester-inputs">
              <label class="network-tester-field">
                <span>{{ t.networks.testerAddress }}</span>
                <input v-model="testerAddress" class="st-input st-mono" spellcheck="false" autocomplete="off" placeholder="10.5.3.24" data-network-tester />
              </label>
              <Button type="submit" variant="outline" :disabled="testing || !testerAddress.trim()" data-network-test><AtlasIcon :name="testing ? 'loader-circle' : 'search'" :size="15" :class="{ 'atlas-spinner': testing }" />{{ t.networks.tester }}</Button>
            </div>
            <Transition :css="false" v-bind="disclosureHooks">
              <p v-if="testerError" class="st-note" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ testerError }}</p>
            </Transition>
            <DecisionChain v-if="tester" :steps="testerSteps(tester)" :label="t.networks.testerResult" data-tester-result />
          </form>
        </SettingsSection>
      </template>
    </template>
  </SettingsPage>
</template>

<style scoped>
.network-confirm { padding: 4px 0 14px; }
.network-tester { display: grid; gap: 10px; margin-top: 14px; padding-top: 14px; border-top: 1px solid var(--line); }
.network-tester h4 { display: flex; align-items: center; gap: 7px; margin: 0; font-size: 14px; }
.network-tester-inputs { display: flex; flex-wrap: wrap; align-items: flex-end; gap: 12px 18px; }
.network-tester-field { display: grid; gap: 5px; flex: 0 1 260px; font-size: 12.5px; font-weight: 650; color: var(--muted); }
.proxy-add { margin: 10px 0 14px; }
.proxy-form { display: grid; gap: 4px; max-width: 520px; }
.proxy-warning { color: var(--warning); font-weight: 550; }
</style>
