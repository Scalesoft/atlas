<script setup lang="ts">
// The release codename's display face, loaded only with the What's new chunk.
import "@fontsource-variable/fraunces/index.css";
import { computed, onBeforeUnmount, onMounted, ref, useId } from "vue";
import { motionMs, motionScale, prefersReducedMotion, riseIn } from "../../motion/aiMotion";
import { firstPoint, isMajorRelease, points, type ReleaseNote } from "../../whatsNew/releaseNotes";
import { releaseLabel } from "../../whatsNew/notice";
import { Button } from "../ui/button";
import ReleaseCover from "./ReleaseCover.vue";
import ReleaseMark from "./ReleaseMark.vue";
import t from "../../i18n/components-release";

/**
 * The welcome after an update (Co je nového opened from the notice, or first after a new release): the release's motif and only what
 * is new since the account's last release (`since`). Driven only by the notes: the newest named release (`note`: codename, mark,
 * date, `### Hlavně` sentence and highlights) and the other unseen releases (`others`, summed up in one strip). The mark unfolds
 * first; the texts rise after it in one wave (`--atlas-brand-release-intro`, riseIn). A click or a key skips the intro; then Enter
 * continues to Atlas and Esc closes (`close`), „Celý changelog“ asks for ReleaseChangelog (`changelog`).
 * Once per release; the full changelog stays at hand any time. Without `since` it is that release's splash replayed from the changelog.
 * After an update by patches only (`patches`, newest first, of the named release `note` the account knows) it is a small update of
 * that release: its motif smaller, the newest patch's version and date, and the fixes as a list instead of the highlights.
 * A release with `cover:` in its notes welcomes after the update (`since`: its notes still unseen) with its motif under the editor's
 * invisible ink, to be scratched away (ReleaseCover); replayed from the changelog it stands uncovered.
 * A major release (x.0.0) is announced larger (owner, 4.0): „Nová velká verze“, a slowly turning halo of the release's colours behind
 * the motif with a ring spreading from it, an aurora drifting slowly in the background, a larger codename with a passing sheen, up to
 * twelve highlights on cards tinted in the release's three colours, and the titles of the other points of its notes as chips
 * („A k tomu 31 dalších novinek“); everything stands still under reduced motion.
 */
const props = defineProps<{ note: ReleaseNote; since?: string; others: readonly ReleaseNote[]; patches?: readonly ReleaseNote[] }>();
const emit = defineEmits<{ close: []; changelog: [] }>();

const major = isMajorRelease(props.note.version) && !props.patches?.length;
/** The titles of the notes' other points (the highlights are out of `text`): what else a major release brings, as chips. */
const more = computed(() => major ? [...props.note.text.matchAll(/^- \*\*(.+?):?\*\*/gm)].map(match => match[1]!) : []);
const covered = !!props.note.cover && !!props.since && !props.patches?.length;
const titleId = `release-splash-${useId()}`;
const label = computed(() => `Atlas ${releaseLabel(props.note.version)}`);
const patch = computed(() => props.patches?.[0]);
/** The fixes of the patches, oldest first. */
const fixes = computed(() => [...props.patches ?? []].reverse().flatMap(points));
/** „3.4.1–3.4.3: …“ — the other unseen releases, oldest first, by the first point of each. */
const also = computed(() => {
  if (!props.others.length) return undefined;
  const oldest = props.others.at(-1)!.version, newest = props.others[0]!.version;
  const range = oldest === newest ? releaseLabel(oldest) : `${releaseLabel(oldest)}–${releaseLabel(newest)}`;
  const points = [...props.others].reverse().flatMap(note => firstPoint(note) ?? []);
  return { range, text: points.length ? `${points.join(". ")}.` : "" };
});

const root = ref<HTMLElement>();
let wave: Animation[] = [];
function skip() {
  for (const animation of wave) animation.finish();
  wave = [];
  document.removeEventListener("keydown", introKey, true);
}
/** During the intro any key shows the texts at once; Enter, Space and Esc do nothing else then. */
function introKey(event: KeyboardEvent) {
  if (["Enter", " ", "Escape"].includes(event.key)) { event.preventDefault(); event.stopPropagation(); }
  skip();
}
/** A click during the intro only skips it: the buttons are not visible yet. */
function introClick(event: MouseEvent) {
  if (!wave.length) return;
  event.preventDefault();
  event.stopPropagation();
  skip();
}
function key(event: KeyboardEvent) {
  if (event.key === "Escape" || (event.key === "Enter" && !(event.target instanceof Element && event.target.closest("button, a, [role='button']")))) {
    event.preventDefault();
    event.stopPropagation();
    emit("close");
  }
}

onMounted(() => {
  const el = root.value!;
  if (prefersReducedMotion(el)) return;
  // riseIn takes the prototype's milliseconds and slows them with the preview itself; the token is slowed already.
  wave = riseIn(el.querySelectorAll("[data-wave]"), { delay: motionMs(el, "--atlas-brand-release-intro") / motionScale(el) });
  if (!wave.length) return;
  document.addEventListener("keydown", introKey, true);
  Promise.all(wave.map(animation => animation.finished)).then(skip, () => {});
});
onBeforeUnmount(skip);
</script>

<template>
  <section ref="root" class="release-splash" :aria-labelledby="titleId" data-release-splash :data-patch="patch ? '' : undefined" :data-major="major ? '' : undefined"
    @click.capture="introClick" @keydown="key">
    <div class="release-splash-hero">
      <ReleaseCover v-if="note.mark && covered" :mark="note.mark" :label="t.splash.markLabel(label, note.codename ?? '')" />
      <ReleaseMark v-else-if="note.mark" :mark="note.mark" variant="hero" />
    </div>
    <div class="release-splash-intro" data-wave>
      <p class="release-splash-eyebrow">{{ patch ? t.splash.patchVersion(patch.version) : major ? (since ? t.splash.newMajorVersion : t.splash.majorVersion) : since ? t.splash.newVersion : t.splash.version }}<template v-if="(patch ?? note).date"> · {{ (patch ?? note).date }}</template></p>
      <h1 :id="titleId" class="release-splash-title">
        <span class="release-splash-version">{{ `${label} ` }}</span>
        <span class="release-splash-codename">{{ note.codename }}</span>
      </h1>
      <p v-if="patch" class="release-splash-lead">{{ t.splash.patchLead }}</p>
      <p v-else-if="note.lead" class="release-splash-lead">{{ note.lead }}</p>
      <p v-if="since && patch" class="release-splash-since">{{ t.splash.sincePatches(releaseLabel(since), patches!.length) }}</p>
      <p v-else-if="since" class="release-splash-since">{{ t.splash.sinceReleases(releaseLabel(since), others.length + 1) }}</p>
    </div>
    <ul v-if="patch" class="release-splash-fixes" :aria-label="t.splash.fixesIn(patch.version)">
      <li v-for="(fix, index) in fixes" :key="index" data-wave>{{ fix }}</li>
    </ul>
    <ul v-else-if="note.highlights.length" class="release-splash-cards" :aria-label="t.splash.mainlyIn(label)">
      <li v-for="item in note.highlights" :key="item.title" class="release-splash-card" data-wave>
        <span class="release-splash-card-title">{{ item.title }}<span v-if="item.key" class="release-splash-card-key">{{ item.key }}</span></span>
        <span class="release-splash-card-text">{{ item.text.replace(/\*\*|`/g, "") }}</span>
      </li>
    </ul>
    <div v-if="more.length" class="release-splash-more" data-wave>
      <p class="release-splash-more-title">{{ t.splash.more(more.length) }}</p>
      <ul><li v-for="title in more" :key="title">{{ title }}</li></ul>
    </div>
    <p v-if="also" class="release-splash-also" data-wave><b>{{ t.splash.alsoIn(also.range) }}</b> {{ also.text }}</p>
    <div class="release-splash-actions" data-wave>
      <Button type="button" size="lg" data-focus-landing data-release-continue @click="emit('close')">{{ t.splash.continue }}</Button>
      <Button type="button" size="lg" variant="outline" data-release-changelog @click="emit('changelog')">{{ t.splash.changelog }}</Button>
      <span class="release-splash-hint">{{ t.splash.hint }}</span>
    </div>
  </section>
</template>

<style scoped>
/* The release's motif: a soft glow of the brand colours and a dotted grid fading to the right, in both modes. */
.release-splash {
  --release-glow-1: rgb(112 88 222 / .16); --release-glow-2: rgb(21 157 170 / .14);
  --release-ink-1: #7058de; --release-ink-2: #4c78e5; --release-ink-3: #159daa;
  position: relative; isolation: isolate; overflow: clip;
  display: grid; grid-template-columns: minmax(0, 280px) minmax(0, 1fr); gap: clamp(16px, 4vw, 44px); align-items: center;
  padding: clamp(20px, 4vw, 44px); border: 1px solid var(--line); border-radius: 20px;
  background: radial-gradient(60% 70% at 12% 18%, var(--release-glow-1), transparent 70%), radial-gradient(55% 60% at 92% 88%, var(--release-glow-2), transparent 70%), var(--surface);
  color: var(--text);
}
:global(html[data-mode="dark"] .release-splash) {
  --release-glow-1: rgb(150 120 255 / .2); --release-glow-2: rgb(80 210 200 / .14);
  --release-ink-1: #bd9cff; --release-ink-2: #89b5ff; --release-ink-3: #63dfd3;
}
.release-splash::after {
  content: ""; position: absolute; inset: 0; z-index: -1; pointer-events: none;
  background-image: radial-gradient(color-mix(in srgb, var(--text) 9%, transparent) 1px, transparent 1px); background-size: 22px 22px;
  -webkit-mask-image: linear-gradient(90deg, #000, transparent 55%); mask-image: linear-gradient(90deg, #000, transparent 55%);
}
.release-splash-hero { display: grid; place-items: center; }
.release-splash-intro { min-width: 0; }
.release-splash-eyebrow { margin: 0; color: var(--muted); font-size: 12px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
.release-splash-title { margin: 6px 0 0; line-height: 1; }
.release-splash-version { display: block; margin-bottom: 6px; color: var(--muted); font-size: clamp(16px, 2vw, 20px); font-weight: 700; }
.release-splash-codename {
  display: block; font-family: "Fraunces Variable", Georgia, "Times New Roman", serif; font-size: clamp(38px, 6vw, 64px); font-weight: 600; letter-spacing: -.02em; text-wrap: balance;
  background: linear-gradient(100deg, var(--release-ink-1), var(--release-ink-2) 52%, var(--release-ink-3)); -webkit-background-clip: text; background-clip: text; color: transparent;
}
.release-splash-lead { max-width: 46ch; margin: 14px 0 0; font-size: 17px; line-height: 1.5; }
.release-splash-since { margin: 8px 0 0; color: var(--muted); font-size: 13.5px; }
.release-splash-cards { grid-column: 1 / -1; display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; margin: 0; padding: 0; list-style: none; }
.release-splash-card { display: flex; flex-direction: column; gap: 4px; padding: 14px 16px; border: 1px solid var(--line); border-radius: var(--st-radius, 14px); background: color-mix(in srgb, var(--surface) 72%, transparent); backdrop-filter: blur(6px); }
.release-splash-card-title { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; font-weight: 700; }
.release-splash-card-key { color: var(--release-ink-2); font-size: 12.5px; font-variant-numeric: tabular-nums; white-space: nowrap; }
.release-splash-card-text { color: var(--muted); font-size: 13.5px; line-height: 1.45; }
/* A major release (owner, 4.0: a larger halo): the motif stands on top in the middle, a halo of the release's colours turns and breathes
   behind it and a ring spreads from it; an aurora drifts slowly in the background; the codename is larger with a passing sheen; the
   cards are tinted in the release's three colours; the other points of the notes follow as chips. */
.release-splash[data-major] { grid-template-columns: minmax(0, 1fr); justify-items: center; text-align: center; padding-top: clamp(32px, 6vw, 72px); border-color: color-mix(in srgb, var(--release-ink-1) 35%, var(--line)); }
.release-splash[data-major]::before {
  content: ""; position: absolute; inset: -25%; z-index: -2; pointer-events: none; filter: blur(40px);
  background: radial-gradient(34% 38% at 24% 30%, var(--release-glow-1), transparent 70%), radial-gradient(30% 34% at 78% 22%, var(--release-glow-2), transparent 70%),
    radial-gradient(38% 40% at 62% 82%, color-mix(in srgb, var(--release-ink-2) 14%, transparent), transparent 70%);
  animation: release-aurora 26s ease-in-out infinite alternate;
}
.release-splash[data-major] .release-splash-hero { position: relative; width: min(340px, 72vw); }
.release-splash[data-major] .release-splash-hero :deep(.release-mark[data-variant]), .release-splash[data-major] .release-splash-hero :deep(.release-cover) { width: 100%; }
.release-splash[data-major] .release-splash-hero::before {
  content: ""; position: absolute; inset: -38%; z-index: -1; border-radius: 50%; pointer-events: none;
  background: conic-gradient(from 0deg, var(--release-ink-1), var(--release-ink-2), var(--release-ink-3), var(--release-ink-1));
  filter: blur(64px); opacity: .4; animation: release-halo-turn 16s linear infinite, release-halo-breathe 5s ease-in-out infinite alternate;
}
.release-splash[data-major] .release-splash-hero::after {
  content: ""; position: absolute; inset: -6%; z-index: -1; border-radius: 50%; pointer-events: none;
  border: 1px solid color-mix(in srgb, var(--release-ink-2) 45%, transparent); opacity: 0; animation: release-ring 6s ease-out 1s infinite;
}
.release-splash[data-major] .release-splash-eyebrow { color: var(--release-ink-1); }
.release-splash[data-major] .release-splash-codename {
  font-size: clamp(52px, 10vw, 120px);
  background-image: linear-gradient(100deg, var(--release-ink-1), var(--release-ink-2) 30%, var(--release-ink-3) 45%, color-mix(in srgb, var(--release-ink-3) 40%, white) 50%, var(--release-ink-3) 55%, var(--release-ink-2) 70%, var(--release-ink-1));
  background-size: 300% 100%; background-position: 100% 0; animation: release-sheen 7s ease-in-out 1.6s infinite;
}
.release-splash[data-major] .release-splash-lead { max-width: 56ch; margin-inline: auto; font-size: 20px; }
.release-splash[data-major] .release-splash-cards { width: 100%; text-align: left; }
.release-splash[data-major] .release-splash-card { --tint: var(--release-ink-1); border-color: color-mix(in srgb, var(--tint) 24%, var(--line)); background: color-mix(in srgb, var(--tint) 6%, color-mix(in srgb, var(--surface) 76%, transparent)); }
.release-splash[data-major] .release-splash-card:nth-child(3n + 2) { --tint: var(--release-ink-2); }
.release-splash[data-major] .release-splash-card:nth-child(3n) { --tint: var(--release-ink-3); }
.release-splash[data-major] .release-splash-card-title { color: color-mix(in srgb, var(--tint) 62%, var(--text)); }
.release-splash-more { grid-column: 1 / -1; width: 100%; }
.release-splash-more-title { margin: 0 0 8px; color: var(--muted); font-size: 12px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
.release-splash-more ul { display: flex; flex-wrap: wrap; justify-content: center; gap: 6px; margin: 0; padding: 0; list-style: none; }
.release-splash-more li { padding: 3px 10px; border: 1px solid color-mix(in srgb, var(--release-ink-2) 18%, var(--line)); border-radius: 999px; background: color-mix(in srgb, var(--surface) 70%, transparent); font-size: 12.5px; }
.release-splash[data-major] .release-splash-actions { justify-content: center; }
.release-splash[data-major] .release-splash-hint { flex-basis: 100%; margin-left: 0; }
@media (min-width: 981px) { .release-splash[data-major] .release-splash-cards { grid-template-columns: repeat(4, minmax(0, 1fr)); } }
@keyframes release-halo-turn { to { rotate: 1turn; } }
@keyframes release-halo-breathe { from { scale: .9; opacity: .3; } to { scale: 1.08; opacity: .48; } }
@keyframes release-ring { 0% { scale: .7; opacity: .55; } 100% { scale: 1.7; opacity: 0; } }
@keyframes release-aurora { from { translate: -3% 2%; rotate: -4deg; scale: 1; } to { translate: 4% -3%; rotate: 6deg; scale: 1.12; } }
@keyframes release-sheen { 0%, 55% { background-position: 100% 0; } 100% { background-position: 0 0; } }
@media (prefers-reduced-motion: reduce) { .release-splash[data-major]::before, .release-splash[data-major] .release-splash-hero::before, .release-splash[data-major] .release-splash-hero::after, .release-splash[data-major] .release-splash-codename { animation: none; } }
:global(html[data-atlas-reduced-motion] .release-splash[data-major]::before), :global(html[data-atlas-reduced-motion] .release-splash[data-major] .release-splash-hero::before),
:global(html[data-atlas-reduced-motion] .release-splash[data-major] .release-splash-hero::after), :global(html[data-atlas-reduced-motion] .release-splash[data-major] .release-splash-codename) { animation: none; }
/* A small update of a known release: the motif smaller and quieter, the fixes as a plain two-column list instead of cards. */
.release-splash[data-patch] { grid-template-columns: minmax(0, 160px) minmax(0, 1fr); }
.release-splash[data-patch] .release-splash-hero :deep(.release-mark[data-variant]) { width: 150px; }
.release-splash[data-patch] .release-splash-codename { font-size: clamp(28px, 4vw, 40px); }
.release-splash[data-patch] .release-splash-lead { font-size: 15px; }
.release-splash-fixes { grid-column: 1 / -1; columns: 2 22rem; column-gap: 28px; margin: 0; padding: 0 0 0 18px; color: var(--muted); font-size: 13.5px; line-height: 1.45; }
.release-splash-fixes li { break-inside: avoid; margin-bottom: 6px; }
.release-splash-also { grid-column: 1 / -1; margin: 0; padding-top: 12px; border-top: 1px solid var(--line); color: var(--muted); font-size: 13.5px; }
.release-splash-also b { color: var(--text); }
.release-splash-actions { grid-column: 1 / -1; display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
.release-splash-hint { margin-left: auto; color: var(--muted); font-size: 12.5px; }
@media (max-width: 760px) {
  .release-splash, .release-splash[data-patch], .release-splash[data-major] { grid-template-columns: minmax(0, 1fr); }
  .release-splash-hero :deep(.release-mark[data-variant]) { width: 168px; }
  .release-splash-hero :deep(.release-cover) { width: 220px; }
  .release-splash-cards { grid-template-columns: minmax(0, 1fr); }
  .release-splash-hint { margin-left: 0; }
}
@media (min-width: 761px) and (max-width: 980px) { .release-splash-cards { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
</style>
