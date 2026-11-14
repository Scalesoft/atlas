<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from "vue";
import { errorMessage } from "../../../api";
import { contactApi, type ContactApi, type ContactLinkItem, type ContactLinkReport, type ContactLinkRun } from "../../../contacts/contactApi";
import { jobsTopic, useLiveFeed, type JobRunChange } from "../../../live/liveFeed";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import ConfirmDialog from "../ConfirmDialog.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import LiveUpdateNote from "../LiveUpdateNote.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import ResultBanner from "../ResultBanner.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import StatTile from "../StatTile.vue";
import { useOptionalSettingsRoute } from "../useSettingsRoute";
import t from "../../../i18n/settings-global";

/**
 * Nastavení › Kontakty (spec 2026-10-30 §6, K4, WikiAdmin): „Propojit kontakty ve stránkách“ links every contact written in a page
 * without a directory id, in working and published revisions alike. „Zobrazit report“ changes nothing and says what would be linked,
 * created and left unclear; „Propojit kontakty“ (enabled once a report exists), after a confirmation that repeats its numbers, creates
 * the contacts and leaves the pages to a run of the job contacts.link on the server (visible text unchanged, nothing deleted), whose progress this
 * page follows and finds again when opened later, with a link to the run's detail and log under Úlohy na pozadí; „Zastavit“ cancels it (what it did stays). People written only on pages not every
 * wiki reader reads are left unlinked („Jen na neveřejných stránkách“). `api` is replaced by the gallery.
 */
const props = defineProps<{ api?: Pick<ContactApi, "linkReport" | "linkApply" | "linkRun" | "linkCancel"> }>();
const source = computed(() => props.api ?? contactApi);
const router = useOptionalSettingsRoute();
// A queued run waits for the server's first round: it is followed and stopped like a running one.
const active = (state?: string) => state === "queued" || state === "running";

const report = ref<ContactLinkReport | null>(null);
const linkRun = ref<ContactLinkRun | null>(null);
// `loaded`: the run found on open (or its absence) is known; until then nothing can start a second one.
const busy = ref<"" | "report" | "apply" | "cancel">(""), error = ref(""), confirming = ref(false), loaded = ref(false);
const progressRegion = ref<HTMLElement>(), resultRegion = ref<HTMLElement>();
// `focusProgress`: the confirmation is still closing; `focusWanted`: the region of the run takes the focus as soon as it is there (the swap below waits for the old one to leave).
let focusProgress = false, focusWanted = false;
// The run is read again when the server says the job contacts.link changed (useLiveFeed; nothing polls). A failed read says so above the report
// and the progress stays in view; a run started elsewhere is taken up unless a report is open here.
function poll() {
  return source.value.linkRun().then(next => {
    if (active(linkRun.value?.state) || (next && active(next.state) && !report.value)) follow(next);
  }, failure => { if (active(linkRun.value?.state)) error.value = errorMessage(failure); });
}
const { unavailable: liveOff, refresh } = useLiveFeed<JobRunChange>(jobsTopic, poll, change => change.jobId === "contacts.link");
function follow(next: ContactLinkRun | null) {
  const ended = active(linkRun.value?.state) && !active(next?.state);
  linkRun.value = next;
  error.value = "";
  // A run's plan stays in view while it runs; once it ended its tiles would be stale, so only the result shows (from the run's report).
  // Until the run's own plan is done (its report has no pages yet), the report the admin confirmed stays.
  report.value = next && active(next.state) ? (next.report.pages || !report.value ? next.report : report.value) : null;
  // The run ended while the person watches: the result is where the focus goes.
  if (ended) { focusWanted = true; void nextTick(focusRegion); }
}
async function showReport() {
  busy.value = "report"; error.value = "";
  try { report.value = await source.value.linkReport(); linkRun.value = null; }
  catch (failure) { error.value = errorMessage(failure); }
  finally { busy.value = ""; }
}
async function apply() {
  busy.value = "apply"; error.value = "";
  try { follow(await source.value.linkApply()); focusProgress = true; confirming.value = false; }
  catch (failure) { error.value = errorMessage(failure); }
  finally { busy.value = ""; }
}
async function cancel() {
  busy.value = "cancel"; error.value = "";
  try { follow(await source.value.linkCancel()); }
  catch (failure) { error.value = errorMessage(failure); }
  finally { busy.value = ""; }
}
// A run started earlier (here or in another tab) is shown at once and followed while it runs.
onMounted(() => void source.value.linkRun().then(found => { if (found && !report.value) follow(found); }, () => {}).finally(() => { loaded.value = true; }));
/** The dialog has left (and returned the focus to its trigger): the progress is the next thing to read. */
function dialogClosed() {
  if (!focusProgress) return;
  focusProgress = false; focusWanted = true;
  void nextTick(focusRegion);
}
function focusRegion() {
  const region = running.value ? progressRegion.value : resultRegion.value;
  if (!focusWanted || !region) return;
  focusWanted = false;
  region.focus();
}
const running = computed(() => active(linkRun.value?.state));
const percent = computed(() => (linkRun.value ? (linkRun.value.state === "succeeded" ? 100 : Math.min(99, Math.round((linkRun.value.pagesDone / Math.max(1, linkRun.value.pages)) * 100))) : 0));
const confirmText = computed(() => {
  const r = report.value;
  return r ? t.contacts.confirmText(r.linked.length, r.created.length, r.unclear.length, r.restricted?.length ?? 0) : "";
});
const groups = computed(() => (report.value ? ([
  ["unclear", t.contacts.unclear, "triangle-alert", report.value.unclear], ["restricted", t.contacts.restrictedGroup, "lock", report.value.restricted ?? []],
  ["created", t.contacts.created, "plus", report.value.created], ["linked", t.contacts.linked, "link", report.value.linked],
] as const).filter(([, , , items]) => items.length) : []));
const reasonOf = (reason?: string | null) => (!reason ? "" : reason.startsWith("conflict:") ? t.contacts.conflict(reason.slice("conflict:".length)) : t.contacts.reasons[reason] ?? reason);
const placeOf = (item: ContactLinkItem) => [t.contacts.place(item.occurrences, t.counts.pagesLocative(item.pageIds.length)), reasonOf(item.reason),
  item.spaceId ? t.contacts.spaceContact : ""].filter(Boolean).join(" · ");
</script>

<template>
  <SettingsPage :title="t.contacts.title" icon="users" :description="t.contacts.description">
    <SettingsSection :title="t.contacts.sectionTitle" icon="link" :description="t.contacts.sectionDescription">
      <div class="contact-link-actions">
        <Button variant="outline" type="button" :disabled="!loaded || !!busy || running" :aria-busy="busy === 'report'" data-contact-link-report @click="showReport"><AtlasIcon name="file-text" :size="16" />{{ t.contacts.showReport }}</Button>
        <Button type="button" :disabled="!loaded || !report || !!busy || running" data-contact-link-apply @click="confirming = true"><AtlasIcon name="link" :size="16" />{{ t.contacts.apply }}</Button>
      </div>
      <LoadingNote :active="!!busy" :label="busy === 'apply' ? t.contacts.creating : t.contacts.scanning" />
      <!-- The page is there at once; the state of the last run is read after it, in the room its progress takes. -->
      <LoadingSkeleton v-if="!loaded" :rows="1" row-height="96px" :label="t.contacts.checking" />
      <ErrorState v-if="error && !confirming" :title="t.contacts.failedTitle" :text="error" :retry-label="t.contacts.retry" @retry="running ? poll() : showReport()" />
      <LiveUpdateNote v-if="liveOff && running" @refresh="refresh" />
      <template v-if="linkRun">
        <!-- Progress and result cross-fade: the one that leaves is gone before the next appears. -->
        <Transition name="st-fade" mode="out-in" appear @after-enter="focusRegion">
        <div v-if="running" key="progress" ref="progressRegion" class="contact-link-progress" tabindex="-1" data-contact-link-progress>
          <div class="contact-link-meter" role="progressbar" :aria-valuenow="percent" aria-valuemin="0" aria-valuemax="100" :aria-label="t.contacts.meter(t.activity.percent(percent))"><span :style="{ width: `${percent}%` }" /></div>
          <!-- The live region says only that it runs (announced once); the numbers change with each step the server tells and are read from the progress bar. -->
          <p class="st-note"><AtlasIcon name="hourglass" :size="15" /><span class="visually-hidden" role="status">{{ t.contacts.running }}</span>{{ t.contacts.progress(linkRun!.pagesDone, linkRun!.pages, t.activity.percent(percent)) }}</p>
          <div><Button variant="outline" size="sm" type="button" :disabled="!!busy" :aria-busy="busy === 'cancel'" data-contact-link-cancel @click="cancel"><AtlasIcon name="close" :size="16" />{{ t.contacts.stop }}</Button></div>
        </div>
        <div v-else-if="linkRun?.state === 'succeeded' || linkRun?.state === 'cancelled' || linkRun?.state === 'failed'" key="result" ref="resultRegion" class="contact-link-result" tabindex="-1" data-contact-link-result>
          <ResultBanner v-if="linkRun.state === 'succeeded'" :message="t.contacts.succeeded(linkRun.report.linked.length, linkRun.report.created.length, linkRun.report.unclear.length, linkRun.revisions)" />
          <ResultBanner v-else icon="triangle-alert" :message="t.contacts.ended(linkRun.state === 'cancelled', linkRun.revisions, linkRun.pagesDone)" />
        </div>
        </Transition>
        <p v-if="router" class="st-note"><AtlasIcon name="list-checks" :size="15" /><a class="st-link" :href="router.href(`general/jobs/contacts.link/runs/${linkRun.id}`)" data-contact-link-run-detail>{{ t.contacts.runDetail }}</a></p>
      </template>
      <template v-if="report">
        <div class="contact-link-tiles" data-contact-link-counts>
          <StatTile :label="t.contacts.willLink" :value="String(report.linked.length)" icon="link">{{ t.contacts.willLinkNote }}</StatTile>
          <StatTile :label="t.contacts.willCreate" :value="String(report.created.length)" icon="plus">{{ t.contacts.willCreateNote }}</StatTile>
          <StatTile :label="t.contacts.unclear" :value="String(report.unclear.length)" icon="triangle-alert" :tone="report.unclear.length ? 'warning' : undefined">{{ t.contacts.unclearNote }}</StatTile>
          <StatTile :label="t.contacts.restricted" :value="String(report.restricted?.length ?? 0)" icon="lock">{{ t.contacts.restrictedNote }}</StatTile>
          <StatTile :label="t.contacts.pages" :value="String(report.pages)" icon="file-text">{{ t.contacts.pagesNote }}</StatTile>
        </div>
        <section v-for="[key, label, icon, items] in groups" :key="key" class="contact-link-group" :data-contact-link-group="key">
          <h4><AtlasIcon :name="icon" :size="15" />{{ label }}</h4>
          <ul>
            <li v-for="(item, index) in items" :key="index"><strong>{{ item.name }}</strong><small v-if="placeOf(item)"> · {{ placeOf(item) }}</small></li>
          </ul>
        </section>
      </template>
    </SettingsSection>
    <ConfirmDialog :open="confirming" :title="t.contacts.confirmTitle" :confirm-label="t.contacts.apply" confirm-icon="link" :destructive="false" :busy="busy === 'apply'" :error="confirming ? error : ''"
      @confirm="apply" @cancel="confirming = false" @closed="dialogClosed">
      <p><strong>{{ confirmText }}</strong></p>
      <p>{{ t.contacts.confirmBody }}</p>
    </ConfirmDialog>
  </SettingsPage>
</template>

<style scoped>
.contact-link-actions { display: flex; flex-wrap: wrap; gap: 10px; }
.contact-link-tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(160px, 1fr)); gap: 10px; animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out); }
.contact-link-group h4 { display: flex; align-items: center; gap: 6px; margin: 0 0 6px; font-size: 14px; }
.contact-link-group ul { display: grid; gap: 4px; margin: 0; padding: 0 0 0 4px; list-style: none; font-size: 13.5px; overflow-wrap: anywhere; }
.contact-link-group small { color: var(--muted); }
.contact-link-progress { display: grid; gap: 8px; }
.contact-link-progress:focus-visible, .contact-link-result:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.contact-link-meter { height: 8px; border-radius: 999px; background: color-mix(in srgb, var(--line) 70%, var(--surface)); overflow: hidden; }
.contact-link-meter > span { display: block; height: 100%; border-radius: inherit; background: var(--accent); transition: width var(--atlas-motion-slow) var(--atlas-ease-out); }
@media (prefers-reduced-motion: reduce) { .contact-link-meter > span { transition: none; } }
</style>
