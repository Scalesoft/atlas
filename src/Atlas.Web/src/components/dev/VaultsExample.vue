<script setup lang="ts">
import { reactive, ref } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import ReauthDialog from "../settings/ReauthDialog.vue";
import DecisionChain from "../settings/DecisionChain.vue";
import OptionPills from "../settings/OptionPills.vue";
import AccessRequestList from "../settings/requests/AccessRequestList.vue";
import FreshAuthSelfTest from "../settings/security/FreshAuthSelfTest.vue";
import { connectionSteps } from "../settings/security/connection";
import { provideReauthEnvironment, type ReauthEnvironment } from "../settings/useReauth";
import type { AccessRequest } from "../../secretRelease";
import { Button } from "../ui/button";
import ConfirmDialog from "../settings/ConfirmDialog.vue";
import ResultBanner from "../settings/ResultBanner.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import ModeMatrix from "../settings/security/ModeMatrix.vue";
import NetworkRuleWizard from "../settings/security/NetworkRuleWizard.vue";
import ProtectionChip from "../settings/security/ProtectionChip.vue";
import ProtectionWizard from "../settings/security/ProtectionWizard.vue";
import type { ProtectionSetting } from "../settings/security/types";
import type { ProtectionState } from "../../protection";
import ResourceAccessPanel from "../settings/access/ResourceAccessPanel.vue";
import { levelLabel } from "../settings/access/accessKinds";
import type { AccessChangeDraft, AccessSource, ResourceAccess, ResourceAccessView } from "../settings/access/accessModel";
import type { AccessRow } from "../settings/access/accessView";
import VaultLocationWizard from "../settings/vaults/VaultLocationWizard.vue";
import VaultStatusBadge from "../settings/vaults/VaultStatusBadge.vue";
import SecretReferenceExample from "./SecretReferenceExample.vue";
import VaultWizard from "../settings/vaults/VaultWizard.vue";
import type { VaultWizardDraft } from "../settings/vaults/types";
import { vaultLocationRootReady, type VaultLocation, type VaultLocationDraft, type VaultStatus } from "../../vaults";

// Gallery examples of the vault and security composites: local sample data, no API, nothing persisted.
const statuses: VaultStatus[] = ["unlocked", "needsPassword", "unavailable", "error"];
const result = ref("");
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
const wizardKey = ref(0);
const locationKey = ref(0);
const confirmOpen = ref(false);

// The fresh sign-in runs on a local environment: any password works except "chyba"; the pills switch the method.
const reauth = reactive<ReauthEnvironment>({
  method: "local",
  supported: true,
  verifyPassword: async (password) => {
    await wait(400);
    return password === "chyba" ? { ok: false, reason: "password" } : { ok: true, proofId: "ukazka" };
  },
  verifyHub: async () => {
    await wait(700);
    return { ok: true, proofId: "ukazka" };
  },
});
provideReauthEnvironment(reauth);
const reauthMode = ref("local");
const reauthModes = [
  { value: "local", label: "Místní heslo", icon: "key-round" },
  { value: "hub", label: "Hub", icon: "external" },
  { value: "window", label: "Okno 10 minut", icon: "shield-check" },
  { value: "unsupported", label: "Nepodporováno", icon: "shield-alert" },
];
function setReauthMode(value: string) {
  reauthMode.value = value;
  reauth.supported = value !== "unsupported";
  reauth.method = value === "hub" ? "hub" : "local";
  reauth.unsupportedReason = value === "unsupported" ? "Ukázka: poskytovatel neposílá čas přihlášení, takže nové přihlášení nejde doložit." : undefined;
  // The server's re-authentication window after a recent sign-in: the confirmation asks for no password.
  reauth.windowUntil = value === "window" ? Date.now() + 10 * 60_000 : null;
}
const reauthOpen = ref(false);
const requests = ref<AccessRequest[]>([
  { id: "r-1", spaceId: "provoz", vaultId: "v", vaultName: "Servery a síť", secretId: "s", secretTitle: "Hlavní router", level: "read", reason: "Výměna firmware v pátek večer.", state: "pending",
    requesterId: "u-3", requesterName: "Tomáš Král", createdAt: "2026-09-27T07:40:00Z", decidedBy: null, decidedByName: null, decidedAt: null, note: null, grantId: null, grantExpiresAt: null, canManage: true },
]);
function decide(request: AccessRequest, verb: string) {
  requests.value = requests.value.filter((item) => item.id !== request.id);
  result.value = `Žádost ${verb} (jen v galerii).`;
}
const connection = connectionSteps({ address: "10.5.3.24", source: "forwarded", reason: "forwarded", peer: "10.5.0.5", peerIsProxy: true, hops: 1, forwardedFor: ["10.5.3.24"],
  network: { id: "n", name: "Kancelář Praha", kind: "lan", range: "10.5.0.0/16" }, networkException: true, instanceMode: "outsideTrustedNetworks", reauthRequired: false, version: "7" }, 1);
const selfTestProviders = async () => [{ providerId: "hub", displayName: "JetBrains Hub", verdict: "unknown" as const, authTimePresent: null, authTimeFresh: null, checkedAt: null, selfTest: null }];
async function selfTest() {
  await wait(700);
  return {
    status: { id: "t", operation: "selftest" as const, targetKind: "none" as const, targetId: null, method: "oidc" as const, state: "failed" as const, failure: "auth_time_missing" as const, expiresAt: "", proofExpiresAt: null },
    providers: [{ providerId: "hub", displayName: "JetBrains Hub", verdict: "unsupported" as const, authTimePresent: false, authTimeFresh: false, checkedAt: "2026-09-27T09:00:00Z", selfTest: true }],
    error: "",
  };
}

async function submitVault(draft: VaultWizardDraft) {
  await wait(500);
  if (draft.vault.name.toLocaleLowerCase("cs").includes("chyba")) throw new Error("Ukázka odmítnutí: soubor se nepodařilo otevřít.");
  return { id: "demo", name: draft.vault.name, entryCount: draft.mode === "attach" ? 12 : 0, generatedPassword: draft.password === null ? "Ukázka-Hesla-9Lp4-xR8s" : null };
}
async function testLocation(draft: VaultLocationDraft) {
  await wait(400);
  if (!vaultLocationRootReady(draft)) throw new Error("Zadejte složku.");
  return "Ukázka: složka je dostupná a zapisovatelná.";
}
async function saveLocation(draft: VaultLocationDraft): Promise<VaultLocation> {
  await wait(400);
  return { id: "demo-location", name: draft.name, kind: draft.kind, root: draft.root, username: draft.username || null, domain: draft.domain || null, hasPassword: !!draft.password, vaultCount: 0 };
}

// Who reaches one vault as GET /api/access/resources/vault/{id} answers it: locked administrators, a group, people.
const direct = (level: AccessSource["level"], extra: Partial<AccessSource> = {}): AccessSource => ({ via: "direct", label: "Přímý přístup", level, version: "v1", effective: true, ...extra });
const access = ref<ResourceAccess>({
  resource: { kind: "vault", id: "v", title: "Servery a síť", spaceId: "provoz", spaceName: "Provoz" },
  canManage: true,
  automatic: [
    { label: "Správci prostoru", detail: "Mění se v přístupech prostoru Provoz", level: "manage", count: 2 },
    { label: "Správci wiki", detail: "Ve všech prostorech", level: "manage", count: 1 },
  ],
  entries: [
    { subject: { kind: "group", id: "g-1", name: "Provoz tým", memberCount: 4 }, level: "discover", sources: [direct("discover", { expiresAt: "2026-12-31T23:00:00Z" })] },
    { subject: { kind: "user", id: "u-2", name: "Petr Svoboda", detail: "petr" }, level: "read", sources: [direct("read")] },
    { subject: { kind: "user", id: "u-3", name: "Eva Malá", detail: "eva.mala" }, level: "discover", sources: [{ via: "group", label: "Provoz tým", groupId: "g-1", level: "discover", effective: true }] },
  ],
  hasMore: false,
});
const accessView = ref<ResourceAccessView>("effective");
const editing = ref<string | null>(null);
function saveAccess(row: AccessRow, _grant: AccessSource, draft: AccessChangeDraft) {
  access.value = {
    ...access.value,
    entries: access.value.entries.map((entry) =>
      `${entry.subject.kind}:${entry.subject.id}` === row.key
        ? { ...entry, level: draft.level, sources: entry.sources.map((item) => (item.via === "direct" ? { ...item, level: draft.level, startAt: draft.startAt, expiresAt: draft.expiresAt } : item)) }
        : entry,
    ),
  };
  editing.value = null;
  result.value = `${row.title}: přístup ${levelLabel(draft.level)} (jen v galerii).`;
}
// The V4 state of one vault as GET /api/vaults/{id}/protection answers it.
const protectionState: ProtectionState = {
  level: "vault", targetId: "v", explicit: null, effective: "always", source: "instance", canManage: true, version: null,
  chain: [{ level: "instance", targetId: null, name: null, mode: "always" }, { level: "vault", targetId: "v", name: "Servery a síť", mode: null }],
  impact: { vaults: 0, secrets: 14 }, preview: { reauthRequired: true, networkException: false, freshAuth: "supported", blocked: false }, configurationVersion: "7",
};
const wait500 = () => wait(500);
async function saveProtection(setting: ProtectionSetting) {
  await wait500();
  result.value = `Ochrana: ${setting} (jen v galerii).`;
}
</script>

<template>
  <SettingsPage title="Trezory a zabezpečení" description="Sestavy pro trezory a hesla (components/settings/vaults) a pro zabezpečení (components/settings/security). Data a volání jsou ukázková; stránky nastavení jim předávají skutečné API.">
    <ResultBanner v-if="result" :message="result" @dismiss="result = ''" />

    <SettingsSection title="VaultStatusBadge" description="vaults/VaultStatusBadge.vue — stav trezoru ze serveru (V2) jako ikona a slovo. Pro stav trezoru ho používejte místo obecného StatusBadge.">
      <div class="st-actions"><VaultStatusBadge v-for="status in statuses" :key="status" :status="status" /></div>
    </SettingsSection>

    <SettingsSection title="VaultWizard" description="vaults/VaultWizard.vue — Přidat trezor průběžně (co · prostor · umístění · soubor · heslo · souhrn · hotovo): platná volba ihned rozbalí další údaje, chips sledují aktuální platnost a předchozí části zůstávají upravitelné. Neplatný návrh ponechá souhrn a zablokuje uložení; trezor vznikne až výslovným potvrzením. Prostor se přeskočí, když je dán; „Nové umístění“ jen pro správce wiki (canAddLocation). Bez výpisu složky se soubor k připojení zadává názvem. Název s „chyba“ ukáže odmítnutí na souhrnu.">
      <VaultWizard
        :key="wizardKey"
        :spaces="[{ value: 'provoz', label: 'Provoz', icon: 'book-open', description: '2 trezory' }, { value: 'vyvoj', label: 'Vývoj', icon: 'code-xml', description: '1 trezor' }]"
        :locations="[{ id: 'fs01', name: 'Souborový server FS01', kind: 'smb', root: 'smb://fs01/it/keepass', vaultCount: 2 }, { id: 'atlas', name: 'Na serveru wiki', kind: 'managed', root: 'D:\\Atlas\\.data\\vaults\\3f2a', vaultCount: 1 }, { id: 'disk', name: 'Disk serveru', kind: 'local', root: 'D:\\Atlas\\trezory', vaultCount: 0 }]"
        :submit="submitVault"
        :test-location="testLocation"
        @done="result = 'Trezor přidán (jen v galerii).'; wizardKey++"
        @open="result = 'Otevřít trezor (jen v galerii).'; wizardKey++"
        @cancel="wizardKey++"
      />
    </SettingsSection>

    <SettingsSection title="VaultLocationWizard + VaultLocationFields" description="vaults/VaultLocationWizard.vue — volba ihned rozbalí navazující pole, bez Další/Zpět. Horní chips ukazují aktuální vyplnění a co ještě přijde; kliknutí jen posune k otevřené části. Použijte pro závislé údaje na jedné stránce, StepFlow pro skutečně oddělené fáze. Pole s testem připojení sdílí VaultLocationFields.vue s VaultWizard. Tři druhy: Na serveru wiki (složku v datech Atlasu spravuje server, zadává se jen název), Vlastní složka na serveru (absolutní cesta mimo wwwroot) a Sdílená složka SMB.">
      <p class="st-muted">Vyzkoušejte: vyberte SMB, vyplňte název a cestu a vraťte se chipem k druhu. Samotný návrat nic nezneplatní. Po přepnutí na vlastní složku zůstane název i otevřený souhrn, cesta a účet se vyčistí a přehled ukáže „Doplnit“. Doplňte místní cestu: souhrn i chips se ihned aktualizují. Po přepnutí na server wiki pole cesty zmizí a umístění je připravené k uložení. Ukládá až závěrečné tlačítko; nejsou potřeba žádná potvrzení výběrů.</p>
      <p class="st-muted">Pohyb: pole, souhrn i SMB přihlašování se plynule vysouvají přes InlineReveal. Barva chips se při změně stavu přelévá zleva doprava a fajfka se vykreslí tahem. Přepněte druh opakovaně: animace neblokují ovládání ani nevracejí dokončené části na začátek. Při omezeném pohybu zůstává pouze jemné prolnutí.</p>
      <VaultLocationWizard :key="locationKey" :test="testLocation" :submit="saveLocation" @done="result = 'Umístění uloženo (jen v galerii).'; locationKey++" @cancel="locationKey++" />
    </SettingsSection>

    <SecretReferenceExample />

    <SettingsSection title="Přístupy trezoru: ResourceAccessPanel" description="access/ResourceAccessPanel.vue s kind vault — kdo má přístup k trezoru nebo heslu: zamčené řádky správců, skupiny, lidé, úrovně Vidí → Čte → Spravuje. Změnit otevře úpravu na místě s explicitním uložením, u přístupu přes skupinu řádek nabídne skupinu. V nastavení ho obaluje access/ResourceAccess.vue (načtení, nové přihlášení ke každé změně, Přidat přístup na místě); tady jsou data ukázková.">
      <ResourceAccessPanel
        v-model:view="accessView"
        :access="access"
        :editing="editing"
        @change="(row) => (editing = editing === row.key ? null : row.key)"
        @cancel="editing = null"
        @save="saveAccess"
        @revoke="confirmOpen = true"
        @add="result = 'Přidat přístup: v nastavení se na místě seznamu otevře GrantFlow (jen v galerii).'"
      />
    </SettingsSection>

    <SettingsSection title="ConfirmDialog" description="settings/ConfirmDialog.vue — potvrzení nevratné akce (archivace, odebrání umístění, odebrání přístupu). Pro odchod z neuloženého formuláře je UnsavedChangesDialog, pro změnu s novým přihlášením ConfirmWithReauth.">
      <div class="st-actions"><Button variant="destructive" @click="confirmOpen = true"><AtlasIcon name="trash" :size="16" />Odebrat přístup</Button></div>
      <ConfirmDialog :open="confirmOpen" title="Odebrat přístup" confirm-label="Odebrat přístup" confirm-icon="trash" @confirm="confirmOpen = false; result = 'Odebráno (jen v galerii).'" @cancel="confirmOpen = false">
        <p><strong>Provoz tým</strong> přijde o úroveň Vidí k celému trezoru.</p>
        <p>Přístup přes jiné cesty zůstane zachovaný.</p>
      </ConfirmDialog>
    </SettingsSection>

    <SettingsSection title="ProtectionChip + ModeMatrix" description="security/ProtectionChip.vue — účinný režim ochrany s původem; security/ModeMatrix.vue — co režim znamená v LAN/VPN a jinde. Na API V4 (ochrana instance, trezoru a hesla).">
      <div class="st-actions">
        <ProtectionChip mode="always" setting="inherit" from="instance" />
        <ProtectionChip mode="outsideTrustedNetworks" setting="outsideTrustedNetworks" />
        <ProtectionChip mode="never" setting="never" />
      </div>
      <div class="gallery-vault-grid"><ModeMatrix mode="never" /><ModeMatrix mode="outsideTrustedNetworks" /><ModeMatrix mode="always" /></div>
    </SettingsSection>

    <SettingsSection title="Nové přihlášení: ConfirmWithReauth + ReauthDialog" description="settings/ConfirmWithReauth.vue je poslední krok každé bezpečnostní změny v průvodci nebo v řádku (vrací proofId); settings/ReauthDialog.vue ho obaluje dialogem pro jednotlivou akci bez vlastního průvodce (změna úrovně, odebrání přístupu, vydání hesla). Prostředí dodává nastavení (security/apiReauth.ts nad API V4). Deset minut po ověření platí serverové okno: potvrzení nové přihlášení nežádá a změna jde bez důkazu (vydání hesla okno nemá). Zde heslo „chyba“ neprojde; přepínač ukáže Hub, okno a nepodporovaný stav.">
      <OptionPills :model-value="reauthMode" :options="reauthModes" label="Nové přihlášení v ukázce" @update:model-value="setReauthMode" />
      <div class="st-actions"><Button variant="outline" data-gallery-reauth @click="reauthOpen = true"><AtlasIcon name="fingerprint" :size="16" />Otevřít ReauthDialog</Button></div>
      <ReauthDialog :open="reauthOpen" title="Odebrat přístup" confirm-label="Odebrat přístup" @confirm="reauthOpen = false; result = 'Potvrzeno novým přihlášením (jen v galerii).'" @cancel="reauthOpen = false">
        <p class="st-note"><AtlasIcon name="info" :size="15" />Provoz tým přijde o úroveň Vidí k celému trezoru.</p>
      </ReauthDialog>
    </SettingsSection>

    <SettingsSection title="ProtectionWizard" description="security/ProtectionWizard.vue — změna režimu ochrany nad stavem úrovně z API (režim · dopad z počtů API · potvrzení s novým přihlášením pro rozsah úrovně). Volba režimu ihned odkryje dopad a potvrzení; zůstává upravitelná. Změna volby zahodí rozpracované ověření, takže nový návrh vyžaduje nové přihlášení. Trezor a heslo mají navíc kartu „Převzít (nyní: …)“; bez aktivní sítě varuje u režimu Mimo sítě. V nastavení ho obaluje security/ProtectionLevelEditor.vue s EffectiveValue a řetězem původu.">
      <ProtectionWizard target="Trezor Servery a síť" :state="protectionState" :active-networks="0" @save="saveProtection" @cancel="result = ''" />
    </SettingsSection>

    <SettingsSection title="Vaše připojení: DecisionChain + connectionSteps" description="security/connection.ts převádí odpověď GET /api/security/connection na kroky DecisionChain: adresa → jak zjištěna → pravidlo → rozhodnutí. Tester adresy používá testerSteps.">
      <DecisionChain :steps="connection" label="Ukázka vyhodnocení připojení" />
    </SettingsSection>

    <SettingsSection title="AccessRequestList" description="requests/AccessRequestList.vue — čekající žádosti o přístup pro správce: důvod, Schválit s platností (GrantValidityFields, výchozí 30 dní, bez omezení nedostupné) a novým přihlášením pro heslo, Zamítnout s poznámkou. Bez API; na stránkách ho obaluje requests/AccessRequestsPanel.vue nad živým feedem (accessRequestChanged).">
      <AccessRequestList :requests="requests" @approve="(request) => decide(request, 'schválena')" @reject="(request) => decide(request, 'zamítnuta')" />
    </SettingsSection>

    <SettingsSection title="FreshAuthSelfTest" description="security/FreshAuthSelfTest.vue — dlaždice správce wiki „Otestovat nové přihlášení“: přihlášení u Hubu v okně, nic nevydá, ukáže verdikt po poskytovatelích. Ukázka vrátí nepodporovaný Hub (chybí auth_time).">
      <FreshAuthSelfTest :run="selfTest" :providers="selfTestProviders" fresh-auth="unknown" />
    </SettingsSection>

    <SettingsSection title="NetworkRuleWizard" description="security/NetworkRuleWizard.vue — přidání důvěryhodné sítě (druh LAN/VPN/jiná · rozsah s normalizací CIDR a varováními security/cidr.ts · dopad · potvrzení s novým přihlášením). Platný název a rozsah ihned rozbalí dopad a potvrzení; neplatný rozsah ponechá souhrn a zablokuje uložení.">
      <NetworkRuleWizard :existing="[{ name: 'Kancelář Praha', cidr: '10.5.0.0/16' }]" :impact="() => [{ icon: 'network', label: 'Ukázka dopadu na vydávání hesel' }]" @save="(rule) => (result = `Síť ${rule.name} (jen v galerii).`)" @cancel="result = ''" />
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.gallery-vault-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 180px), 1fr)); gap: 10px; }
</style>
