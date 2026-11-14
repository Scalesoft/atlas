<script setup lang="ts">
import AtlasBrandLogo from "../AtlasBrandLogo.vue";
import t from "../../i18n/components-login";
defineProps<{ wikiName: string }>();
</script>

<template>
  <div class="login-layout">
    <aside class="login-art" :aria-label="t.art.label">
      <p class="login-eyebrow">{{ wikiName }} · {{ t.art.eyebrow }}</p>
      <div class="login-theatre" aria-hidden="true">
        <div class="login-aura" />
        <div class="login-orbit" /><div class="login-orbit login-orbit-second" />
        <div class="login-echo login-echo-left"><AtlasBrandLogo motion="static" /></div>
        <div class="login-echo login-echo-right"><AtlasBrandLogo motion="static" /></div>
        <div class="login-hero"><AtlasBrandLogo motion="hero" /></div>
        <span class="login-star" /><span class="login-star login-star-second" /><span class="login-star login-star-third" />
      </div>
      <div class="login-message"><h2>{{ t.tagline.title }}</h2><p>{{ t.tagline.text }}</p></div>
    </aside>
    <div class="login-content"><slot /></div>
  </div>
</template>

<style scoped>
.login-layout { width: min(980px, 100%); display: grid; grid-template-columns: 1.05fr 1fr; overflow: hidden; border: 1px solid var(--line); border-radius: 24px; background: var(--surface); box-shadow: var(--shadow-modal); }
.login-art { position: relative; overflow: hidden; display: flex; flex-direction: column; padding: 36px; min-width: 0; background: color-mix(in srgb, var(--accent-soft) 55%, var(--surface)); }
.login-eyebrow { margin: 0; color: var(--muted); font-size: 11px; letter-spacing: .12em; text-transform: uppercase; overflow-wrap: anywhere; }
.login-theatre { position: relative; display: grid; place-items: center; flex: 1; min-height: 320px; }
.login-hero, .login-echo { width: 220px; height: 220px; }
.login-hero { z-index: 1; animation: login-reveal var(--atlas-brand-login-reveal) var(--atlas-ease-out) both; }
.login-aura { position: absolute; width: 270px; height: 220px; border-radius: 50%; background: radial-gradient(ellipse, rgb(76 120 229 / .2), transparent 70%); filter: blur(15px); animation: login-aura var(--atlas-brand-login-aura) var(--atlas-ease-inout) infinite; }
.login-orbit { position: absolute; width: 290px; height: 110px; border: 1px solid rgb(76 120 229 / .2); border-radius: 50%; transform: rotate(-25deg); opacity: 0; animation: login-orbit-reveal var(--atlas-brand-login-orbit-reveal) var(--atlas-ease-out) var(--atlas-brand-login-orbit-delay) both, login-orbit var(--atlas-brand-login-orbit) linear var(--atlas-brand-login-orbit-delay) infinite; }
.login-orbit-second { width: 260px; height: 170px; transform: rotate(32deg); }
.login-echo { position: absolute; opacity: 0; animation: login-echo var(--atlas-brand-login-echo) var(--atlas-ease-out) both; }
.login-echo-left { --echo-x: -44px; --echo-angle: -15deg; }
.login-echo-right { --echo-x: 44px; --echo-angle: 15deg; animation-delay: var(--atlas-brand-login-echo-stagger); }
.login-star { position: absolute; width: 4px; height: 4px; border-radius: 50%; background: #63dfd3; box-shadow: 0 0 12px #63dfd3; left: 18%; top: 26%; animation: login-star var(--atlas-brand-login-star) var(--atlas-ease-inout) var(--atlas-brand-login-star-delay) infinite both; }
.login-star-second { left: auto; right: 16%; top: 40%; animation-delay: var(--atlas-brand-login-star-delay-2); animation-duration: var(--atlas-brand-login-star-2); }
.login-star-third { left: 40%; top: auto; bottom: 20%; animation-delay: var(--atlas-brand-login-star-delay-3); animation-duration: var(--atlas-brand-login-star-3); }
.login-message { position: relative; }
.login-message h2 { margin: 0 0 10px; color: var(--text); font-size: clamp(26px, 3vw, 34px); font-weight: 600; letter-spacing: -.04em; line-height: 1.2; }
.login-message p { margin: 0; font-size: 13px; color: var(--muted); }
.login-content { display: flex; align-items: center; min-width: 0; min-height: 570px; }
.atlas .login-layout > .login-content :deep(.entry-card) { width: 100%; min-width: 0; padding: 36px; margin: 0; border: 0; border-radius: 0; box-shadow: none; background: transparent; }
@keyframes login-reveal { from { opacity: 0; } to { opacity: 1; } }
@keyframes login-orbit-reveal { to { opacity: .5; } }
@keyframes login-orbit { to { rotate: 360deg; } }
@keyframes login-star { 0%, 100% { opacity: .12; scale: .8; } 50% { opacity: .9; scale: 1.3; } }
@keyframes login-aura { 0%, 100% { transform: scale(.85); opacity: .65; } 50% { transform: scale(1.15); opacity: 1; } }
@keyframes login-echo { from { transform: translate(var(--echo-x), 22px) rotate(var(--echo-angle)) scale(.65); opacity: 0; } 25% { opacity: .24; } to { transform: translate(0, 0) rotate(0) scale(1); opacity: 0; } }
@media (max-width: 700px) {
  .login-layout { grid-template-columns: 1fr; max-width: 480px; }
  .login-art { padding: 24px; }
  .login-theatre { min-height: 180px; }
  .login-hero, .login-echo { width: 145px; height: 145px; }
  .login-orbit { width: 220px; height: 90px; }
  .login-orbit-second { width: 190px; height: 130px; }
  .login-content { min-height: 0; }
  .atlas .login-layout > .login-content :deep(.entry-card) { padding: 26px; }
}
/* Plan O1: the loops (orbits, stars, aura, drift) belong to this entry screen only. Reduced motion (O2): they stand
   and the decoration is hidden; the hero fades in (160 ms, linear) to its last frame. */
@media (prefers-reduced-motion: reduce) {
  .login-hero { animation: login-reveal var(--atlas-motion-surface) linear both; }
  .login-aura { animation: none; }
  .login-echo, .login-orbit, .login-star { display: none; }
}
:global([data-atlas-reduced-motion] .login-hero) { animation: login-reveal var(--atlas-motion-surface) linear both; }
:global([data-atlas-reduced-motion] .login-aura) { animation: none; }
:global([data-atlas-reduced-motion] :is(.login-echo, .login-orbit, .login-star)) { display: none; }
</style>
