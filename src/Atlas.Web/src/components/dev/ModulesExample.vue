<script setup lang="ts">
import { ref } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import ActivityTimeline from "../settings/ActivityTimeline.vue";
import ResultBanner from "../settings/ResultBanner.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import StatusBadge from "../settings/StatusBadge.vue";
import TileCard from "../settings/TileCard.vue";
import CopyField from "../settings/general/CopyField.vue";
import OidcProviderWizard from "../settings/general/OidcProviderWizard.vue";
import ZipDropzone from "../settings/general/ZipDropzone.vue";
import { backupActivity, downloadable, type BackupRun } from "../settings/general/backups";
import { auditActivity, type AccessAuditEvent } from "../settings/auditActivity";
import { emptyProviderDraft, type OidcProviderDraft } from "../settings/general/oidc";

// Gallery examples of the module and backup composites: local sample data, no API, nothing persisted.
const result = ref("");
const wizardKey = ref(0);
const editKey = ref(0);
const archive = ref<File | null>(null);
const now = new Date("2026-09-27T12:00:00");

const savedProvider: OidcProviderDraft = {
  ...emptyProviderDraft(),
  savedId: "hub",
  id: "hub",
  displayName: "JetBrains Hub",
  issuer: "https://hub.example.cz/hub",
  clientId: "atlas-wiki",
  hasSecret: true,
  adminMappings: [{ claimType: "groups", claimValue: "atlas-admins" }],
};
function applied(provider: OidcProviderDraft) {
  result.value = `Poskytovatel „${provider.displayName}“ (${provider.id}) je v návrhu; uložil by se tlačítkem Uložit přihlášení (jen v galerii).`;
  wizardKey.value++;
  editKey.value++;
}

const runs: BackupRun[] = [
  { id: "run-3", startedAt: "2026-09-27T11:40:00", finishedAt: null, status: "running", trigger: "manual", mode: "full", destination: "smb://fs01/zalohy/wiki", fileName: null, size: 0, message: "" },
  { id: "run-2", startedAt: "2026-09-27T02:00:00", finishedAt: "2026-09-27T02:00:04", status: "succeeded", trigger: "scheduled", mode: "full", destination: "smb://fs01/zalohy/wiki", fileName: "atlas-20260927.zip", size: 3_480_000, message: "Záloha byla vytvořena." },
  { id: "run-1", startedAt: "2026-09-26T02:00:00", finishedAt: "2026-09-26T02:00:01", status: "skipped", trigger: "scheduled", mode: "full", destination: "smb://fs01/zalohy/wiki", fileName: null, size: 0, message: "" },
  { id: "run-0", startedAt: "2026-09-25T02:00:00", finishedAt: "2026-09-25T02:00:03", status: "failed", trigger: "scheduled", mode: "full", destination: "smb://fs01/zalohy/wiki", fileName: null, size: 0, message: "Cíl není dostupný." },
];
const history = runs.map(backupActivity);
const runOf = (id: string) => runs.find((run) => run.id === id);
// The audit's tool calls through the real mapping: an assistant proposal and its decision, an MCP section edit retried three times
// (one line, "3×") and a refused one.
const toolCall = (id: string, minute: number, action: string, apiOperation: string, apiOutcome: string, more: Partial<AccessAuditEvent> = {}): AccessAuditEvent => ({
  id, occurredAt: `2026-09-27T11:${String(minute).padStart(2, "0")}:00`, actorId: "jana", actorName: "Jana Nováková", action, userId: null, userName: null, groupId: null,
  groupName: null, spaceId: "s", spaceName: "Obchod", role: null, isActive: null, isWikiAdmin: null, canIssueTokens: null,
  apiChannel: action.startsWith("mcp") ? "mcp" : "assistant", apiOperation, apiOutcome, pageId: "nabidka", pageTitle: "Cenová nabídka", ...more,
});
const toolCalls = [
  toolCall("t1", 54, "assistant.write", "assistant.decision", "applied"),
  toolCall("t2", 53, "assistant.write", "assistant.proposal", "pending"),
  toolCall("t3", 40, "mcp.write", "atlas_update_section", "ok", { apiTokenName: "Claude Code" }),
  toolCall("t4", 38, "mcp.write", "atlas_update_section", "ok", { apiTokenName: "Claude Code" }),
  toolCall("t5", 35, "mcp.write", "atlas_update_section", "ok", { apiTokenName: "Claude Code" }),
  toolCall("t6", 31, "mcp.write", "atlas_update_section", "version_conflict", { apiTokenName: "Claude Code" }),
].map(auditActivity);
</script>

<template>
  <SettingsPage title="Moduly a zálohy" description="Sestavy pro Moduly a integrace a pro Zálohy (components/settings/general). Data jsou ukázková; stránky ModulesPage a BackupsPage jim předávají skutečné API.">
    <ResultBanner v-if="result" :message="result" @dismiss="result = ''" />

    <SettingsSection title="Přehled modulů: TileCard se stavem" description="ModulesPage bez podstránky — každý modul jako TileCard s ikonou, popisem a stavem (StatusBadge v #badge). Karty vedou na vlastní stránku modulu general/modules/<id>. Pro čísla na přehledu je StatTile, pro řádky seznamu SettingsListRow.">
      <div class="gallery-modules-grid">
        <TileCard title="Firemní přihlášení" description="Připojte JetBrains Hub nebo jiného poskytovatele OpenID Connect." icon="identity">
          <template #badge><StatusBadge tone="success" icon="circle-check" label="1 poskytovatel" size="sm" /></template>
          <template #footer>Otevřít nastavení</template>
        </TileCard>
        <TileCard title="YouTrack" description="Propojte stránky s úkoly a povolte projekty pro jednotlivé prostory." icon="external" :index="1">
          <template #badge><StatusBadge tone="warning" icon="triangle-alert" label="Chybí spojení" size="sm" /></template>
          <template #footer>Otevřít nastavení</template>
        </TileCard>
        <TileCard title="Import Wiki.js" description="Zkontrolujte a bezpečně přeneste existující obsah ze ZIP exportu." icon="download" :index="2">
          <template #badge><StatusBadge tone="info" icon="upload" label="Jednorázový import" size="sm" /></template>
          <template #footer>Otevřít nastavení</template>
        </TileCard>
        <TileCard title="Server MCP" description="Povolte nástrojům, například Claude Code, práci s wiki přes osobní token. Ikona plug, ne symbol AI." icon="plug" :index="3">
          <template #badge><StatusBadge tone="neutral" icon="ban" label="Vypnuto" size="sm" /></template>
          <template #footer>Otevřít nastavení</template>
        </TileCard>
      </div>
    </SettingsSection>

    <SettingsSection title="Zálohy: způsoby na přehledu" description="BackupsPage — jedna stránka pro všechny zálohy. Každý způsob (Archivy ZIP, Git) je TileCard bez odkazu: stav v #badge, fakta ve výchozím slotu a vlastní akce v #footer (Zálohovat teď, Synchronizovat teď), dostupná z každé části stránky. Pod kartami jsou SettingsTabs s nastavením každého způsobu a s obnovou ze zálohy; každá záložka má vlastní SaveBar. Tuto skladbu použijte, když jeden úkol má víc rovnocenných cest. Pro nezávislé moduly s vlastní stránkou zůstávají karty s odkazem výše.">
      <div class="gallery-modules-grid">
        <TileCard title="Archivy ZIP" description="Pravidelný úplný snímek wiki a příloh do složky serveru nebo na síťový disk." icon="folder">
          <template #badge><StatusBadge tone="success" icon="circle-check" label="V pořádku" size="sm" /></template>
          <span>Poslední záloha 27. 9. 2026 2:00 · další za 14 hodin</span>
          <template #footer><Button type="button" size="sm" @click="result = 'Záloha by se spustila (jen v galerii).'"><AtlasIcon name="download" :size="15" />Zálohovat teď</Button></template>
        </TileCard>
        <TileCard title="Git repozitář" description="Po každé uložené změně zapíše commit do vzdáleného repozitáře. Doplňuje archivy, nenahrazuje je." icon="git-branch" :index="1">
          <template #badge><StatusBadge tone="info" icon="hourglass" label="Čeká na zápis" size="sm" /></template>
          <span>3 stránky se zapíší do 10 minut · poslední commit 4f2c9e1</span>
          <template #footer><Button type="button" size="sm" variant="outline" @click="result = 'Synchronizace by se spustila (jen v galerii).'"><AtlasIcon name="refresh" :size="15" />Synchronizovat teď</Button></template>
        </TileCard>
      </div>
    </SettingsSection>

    <SettingsSection title="OidcProviderWizard" description="general/OidcProviderWizard.vue — přidání nebo úprava poskytovatele přihlášení s průběžným rozbalováním (název · spojení · přístup · správci · souhrn). Platné údaje ihned odkryjí další část, dřívější údaje zůstávají upravitelné. Chips se přepočítávají a animují; neplatný návrh ponechá souhrn viditelný, ale zablokuje potvrzení. Mění jen návrh stránky: poskytovatele vrací událostí done a uloží ho až SaveBar stránky, protože server bere všechny poskytovatele jedním požadavkem. Potvrzení návrhu i následné uložení stránky zůstávají výslovné. Pro změnu ukládanou přímo závěrečným tlačítkem použijte vzor VaultLocationWizard.">
      <OidcProviderWizard :key="wizardKey" origin="https://wiki.example.cz" :taken-ids="['hub']" @done="applied" @cancel="wizardKey++" />
    </SettingsSection>

    <SettingsSection title="OidcProviderWizard: úprava uloženého" description="Stejný průvodce s uloženým poskytovatelem: tajný klíč se nezobrazuje, prázdné pole ho ponechá, zaškrtnutím se odebere. Změna ID upozorní na novou návratovou adresu.">
      <OidcProviderWizard :key="`edit-${editKey}`" :provider="savedProvider" origin="https://wiki.example.cz" @done="applied" @cancel="editKey++" />
    </SettingsSection>

    <SettingsSection title="CopyField" description="general/CopyField.vue — veřejná hodnota ke zkopírování do jiného systému (návratová adresa OIDC, veřejný deploy klíč Zálohy do Gitu). Potvrdí kopírování na místě, a když schránka odmítne, řekne to u hodnoty. valueAttrs dá stránce stabilní háček přímo na hodnotu. Pro tajnou hodnotu zobrazenou jen jednou je OneTimeSecret.">
      <CopyField label="Návratová adresa pro registraci klienta" value="https://wiki.example.cz/signin-oidc/hub" copy-label="Kopírovat adresu" />
      <CopyField label="Veřejný klíč" value="ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGdhbGxlcnkga2V5 atlas@wiki.example.cz" copy-label="Kopírovat klíč" :value-attrs="{ 'data-gallery-public-key': '' }" />
    </SettingsSection>

    <SettingsSection title="ZipDropzone" description="general/ZipDropzone.vue — jeden ZIP k nahrání (export Wiki.js, záloha): cíl pro přetažení, který je zároveň popiskem skrytého souborového pole, s velikostí vybraného souboru a limitem. Soubor drží rodič; nic se tu nenahrává.">
      <ZipDropzone v-model="archive" label="Přetáhněte ZIP sem nebo vyberte soubor" :max-bytes="64 * 1024 * 1024" />
      <p class="st-note">{{ archive ? `Vybráno: ${archive.name}` : "Zatím nic nevybráno." }}</p>
    </SettingsSection>

    <SettingsSection title="ActivityTimeline: historie záloh" description="settings/ActivityTimeline.vue s vlastním štítkem výsledku (ActivityItem.badge: Probíhá, Dokončeno, Beze změny, Selhalo) a akcí u záznamu (slot #item-actions: Stáhnout ZIP). Pro historii přístupů zůstávají výchozí výsledky Povoleno/Zamítnuto; filtry dává ActivityLog.">
      <ActivityTimeline :items="history" :now="now" label="Historie záloh">
        <template #item-actions="{ item }">
          <a v-if="runOf(item.id) && downloadable(runOf(item.id)!)" class="st-link" href="#/gallery/modules" @click.prevent="result = `Stažení ${runOf(item.id)!.fileName} (jen v galerii).`">Stáhnout ZIP</a>
        </template>
      </ActivityTimeline>
    </SettingsSection>

    <SettingsSection title="ActivityTimeline: volání nástrojů v auditu" description="Totéž ActivityTimeline nad auditActivity (Nastavení → Audit): věta říká, co nástroj udělal se kterou stránkou (ActivityItem.href = odkaz na ni, via = kanál za cílem), štítek výsledku vysvětluje kód (Čeká na potvrzení, Konflikt verzí, …; „Co se stalo“ v Podrobnostech). Stejný záznam opakovaný do 10 minut se složí do jednoho řádku s „3×“ a časy opakování v Podrobnostech. Nad auditem je přehled ActivitySummarySection (StatTile + pořadí lidí a nástrojů) ze serveru.">
      <ActivityTimeline :items="toolCalls" :now="now" label="Audit" />
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.gallery-modules-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr)); gap: 12px; }
</style>
