<script setup lang="ts">
import { computed, ref } from "vue";
import AtlasIcon from "./AtlasIcon.vue";
import AiGlyph from "./ai/AiGlyph.vue";
import t from "../i18n/components-a";
import type { CollaborationPeer } from "../collaborationPresence";
import { Popover, PopoverContent, PopoverTrigger } from "./ui/popover";
import { Tooltip, TooltipContent, TooltipTrigger } from "./ui/tooltip";

/**
 * Who works on a document with you: the page's co-editors (a click goes to their cursor) and, with `activity`, the colleagues at a
 * board card's object elsewhere (the card's avatars say through which view; nothing to go to). `label` names the group.
 */
const props = withDefaults(defineProps<{ peers: readonly CollaborationPeer[]; label?: string }>(), { label: () => t.collaborators.group });
const emit = defineEmits<{ navigate: [clientId: number] }>();
const overflowOpen = ref(false);
const visiblePeers = computed(() => props.peers.slice(0, 4));
const extraPeers = computed(() => props.peers.slice(4));

function initials(name: string) {
  // An agent writing for a person (3.6-H, "Jana Nováková · AI (MCP: laptop)") wears the person's initials.
  const words = name.split(" · ")[0].trim().split(/\s+/).filter(Boolean);
  return (words.length > 1 ? `${words[0][0]}${words.at(-1)![0]}` : words[0]?.[0] ?? "?").toLocaleUpperCase();
}
function peerLabel(peer: CollaborationPeer) {
  if (peer.activity) return peer.activity;
  if (peer.id?.startsWith("ai:")) return t.collaborators.agent(peer.name);
  return peer.cursorAvailable
    ? t.collaborators.goTo(peer.name)
    : t.collaborators.noCursor(peer.name);
}
// An agent shows only while it writes (LivePageWrites): without its caret yet it is at work, with it it has just written.
const isAgent = (peer: CollaborationPeer) => peer.id?.startsWith("ai:") === true;
function navigate(peer: CollaborationPeer) {
  if (!peer.cursorAvailable) return;
  emit("navigate", peer.clientId);
  overflowOpen.value = false;
}
</script>

<template>
  <Transition name="collaborator-presence">
  <div v-if="peers.length" class="collaborator-avatars" role="group" :aria-label="label">
    <TransitionGroup name="collaborator-list" tag="div" class="collaborator-avatar-row" appear>
      <span v-for="peer in visiblePeers" :key="peer.clientId" class="collaborator-avatar-slot">
        <Tooltip>
          <TooltipTrigger as-child>
            <button
              type="button"
              class="collaborator-avatar"
              :class="{ 'is-unavailable': !peer.cursorAvailable && !peer.activity && !isAgent(peer), 'is-elsewhere': !!peer.activity, 'is-agent': isAgent(peer) }"
              :style="{ '--collaborator-color': peer.color }"
              :data-client-id="peer.clientId"
              :aria-label="peerLabel(peer)"
              :aria-disabled="!peer.cursorAvailable || undefined"
              @click="navigate(peer)"
            >
              <span class="collaborator-avatar-face" :class="{ 'atlas-ai-aura': isAgent(peer) }" aria-hidden="true">
                {{ initials(peer.name) }}
                <span v-if="isAgent(peer)" class="collaborator-mode">
                  <AiGlyph :size="10" :working="!peer.cursorAvailable" :state="peer.cursorAvailable ? 'done' : 'writing'" />
                </span>
                <span v-else class="collaborator-mode"><AtlasIcon name="pencil" :size="10" /></span>
              </span>
            </button>
          </TooltipTrigger>
          <TooltipContent>{{ peerLabel(peer) }}</TooltipContent>
        </Tooltip>
      </span>
    </TransitionGroup>

    <Popover v-if="extraPeers.length" v-model:open="overflowOpen">
      <PopoverTrigger as="button" type="button" class="collaborator-overflow" :aria-label="t.collaborators.moreCount(extraPeers.length)">+{{ extraPeers.length }}</PopoverTrigger>
      <PopoverContent align="end" class="collaborator-overflow-panel">
        <div role="group" :aria-label="t.collaborators.more">
          <button
            v-for="peer in extraPeers"
            :key="peer.clientId"
            type="button"
            class="collaborator-menu-item"
            :class="{ 'is-unavailable': !peer.cursorAvailable }"
            :data-client-id="peer.clientId"
            :aria-label="peerLabel(peer)"
            :aria-disabled="!peer.cursorAvailable || undefined"
            @click="navigate(peer)"
          >
            <span class="collaborator-menu-avatar" :style="{ '--collaborator-color': peer.color }" aria-hidden="true">{{ initials(peer.name) }}</span>
            <span class="collaborator-menu-name">{{ peer.name }}</span>
            <small v-if="peer.activity || !peer.cursorAvailable">{{ peer.activity ?? t.collaborators.cursorUnavailable }}</small>
          </button>
        </div>
      </PopoverContent>
    </Popover>
  </div>
  </Transition>
</template>

<style scoped>
.collaborator-avatars, .collaborator-avatar-row, .collaborator-avatar-slot { display: inline-flex; align-items: center; min-width: 0; }
.collaborator-avatars { flex: 0 0 auto; }
.collaborator-avatar-row { position: relative; isolation: isolate; padding-left: 8px; }
.collaborator-avatar-slot { position: relative; margin-left: -8px; }
.collaborator-avatar {
  display: grid; place-items: center; position: relative;
  width: 32px; height: 36px; flex: 0 0 32px;
  padding: 0; border: 0; border-radius: 999px; background: transparent;
}
.collaborator-avatar-face, .collaborator-menu-avatar {
  display: inline-grid; place-items: center; position: relative;
  width: 30px; height: 30px; flex: 0 0 30px; border-radius: 999px;
  font-size: 10px; font-weight: 700; line-height: 1;
  border: 2px solid var(--surface); background: var(--collaborator-color); color: white;
}
/* The button's hit area stays still while its face moves between its neighbours. */
.collaborator-avatar-face {
  pointer-events: none;
  box-shadow: 0 1px 3px color-mix(in srgb, var(--text) 16%, transparent);
  transition: transform var(--atlas-motion-slow) var(--atlas-ease-out), opacity var(--atlas-motion-base);
}
.collaborator-mode {
  position: absolute; right: -3px; bottom: -2px; width: 14px; height: 14px;
  display: grid; place-items: center; border: 1.5px solid var(--surface); border-radius: 50%;
  background: var(--canvas); color: var(--text);
}
.collaborator-avatar-slot:focus-within { z-index: 2; }
@media (hover: hover) and (pointer: fine) { .collaborator-avatar-slot:hover { z-index: 2; } }
@media (hover: hover) and (pointer: fine) { .collaborator-avatar-row:has(.collaborator-avatar-slot:hover) .collaborator-avatar-slot:not(:hover) { z-index: auto; } }
.collaborator-avatar-slot:focus-within .collaborator-avatar-face { transform: translateY(-3px) scale(1.07); }
.collaborator-avatar:focus-visible .collaborator-avatar-face { outline: 2px solid var(--accent); outline-offset: 2px; }
.collaborator-avatar:focus-visible { outline: none; }
.collaborator-avatar:focus-visible .collaborator-mode svg { animation: atlas-collaborator-pencil calc(var(--atlas-motion-pop) * 2) var(--atlas-ease-inout); }
@media (hover: hover) and (pointer: fine) {
  .collaborator-avatar-row:has(.collaborator-avatar-slot:hover) .collaborator-avatar-face { transform: none; }
  .collaborator-avatar-row .collaborator-avatar-slot:hover .collaborator-avatar-face { transform: translateY(-3px) scale(1.07); }
  .collaborator-avatar-slot:has(+ .collaborator-avatar-slot:hover) .collaborator-avatar-face { transform: translateX(-3px) rotate(-3deg); }
  .collaborator-avatar-slot:hover + .collaborator-avatar-slot .collaborator-avatar-face { transform: translateX(3px) rotate(3deg); }
  .collaborator-avatar-slot:hover .collaborator-mode svg { animation: atlas-collaborator-pencil calc(var(--atlas-motion-pop) * 2) var(--atlas-ease-inout); }
}
.collaborator-avatar:is(.is-unavailable, .is-elsewhere) { cursor: default; }
/* An agent writing for a person (3.6-H): the person's initials on the AI gradient in the AI aura (styles/ai.css), and the AI glyph instead of the pencil,
   at work (writing: the star pulses and sways, the small stars spark) until it has written (done, once). People never glow. */
.collaborator-avatar.is-agent .collaborator-avatar-face { background: var(--ai-gradient); }
/* Its badge, as in the film: the stars in white on the AI gradient, writing while it works. */
.collaborator-avatar.is-agent .collaborator-mode { width: 15px; height: 15px; background: var(--ai-gradient); --ai-ink: white; --ai-line-strong: rgb(255 255 255 / .5); }
/* It joins visibly: one ring of the aura spreads from the face (the film's ripple); under reduced motion it only fades. */
.collaborator-avatar.is-agent .collaborator-avatar-face::after {
  content: ""; position: absolute; inset: -2px; border: 2px solid var(--ai-aura); border-radius: 50%; opacity: 0; pointer-events: none;
  animation: atlas-edit-lock-ring var(--atlas-motion-pop) var(--atlas-ease-out);
}
@media (prefers-reduced-motion: reduce) { .collaborator-avatar.is-agent .collaborator-avatar-face::after { animation-name: st-fade-out; } }
:global([data-atlas-reduced-motion]) .collaborator-avatar.is-agent .collaborator-avatar-face::after { animation-name: st-fade-out; }
.collaborator-avatar.is-unavailable .collaborator-avatar-face { opacity: .7; }
.collaborator-menu-item.is-unavailable { cursor: default; opacity: .7; }
.collaborator-overflow {
  display: inline-grid; place-items: center; width: 28px; height: 28px; margin-left: 4px;
  border: 1px solid var(--line); border-radius: 999px; background: var(--canvas); color: var(--muted);
  font-size: 10px; font-weight: 700;
}
.collaborator-overflow:focus-visible { background: var(--hover); color: var(--text); }
@media (hover: hover) and (pointer: fine) { .collaborator-overflow:hover { background: var(--hover); color: var(--text); } }
.collaborator-overflow:focus-visible, .collaborator-menu-item:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.collaborator-overflow-panel { width: min(260px, calc(100vw - 24px)); padding: 6px; }
.collaborator-overflow-panel [role="group"] { display: grid; gap: 2px; }
.collaborator-menu-item {
  display: grid; grid-template-columns: 30px minmax(0, 1fr); align-items: center; gap: 9px;
  width: 100%; border: 0; border-radius: var(--radius-sm); padding: 6px 8px;
  background: transparent; color: var(--text); text-align: left;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out);
}
.collaborator-menu-item:focus-visible { background: var(--hover); }
@media (hover: hover) and (pointer: fine) { .collaborator-menu-item:hover { background: var(--hover); } }
.collaborator-menu-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.collaborator-menu-item small { grid-column: 2; margin-top: -6px; color: var(--muted); font-size: 11px; }
.collaborator-presence-enter-active, .collaborator-presence-leave-active { transition: opacity var(--atlas-motion-base) var(--atlas-ease-out); }
.collaborator-presence-enter-from, .collaborator-presence-leave-to { opacity: 0; }
.collaborator-list-enter-active, .collaborator-list-leave-active, .collaborator-list-move {
  transition: transform var(--atlas-motion-slow) var(--atlas-ease-out), opacity var(--atlas-motion-base) var(--atlas-ease-out);
}
.collaborator-list-enter-from, .collaborator-list-leave-to { opacity: 0; transform: translateY(4px) scale(.88); }
.collaborator-list-leave-active { position: absolute; pointer-events: none; }
@media (pointer: coarse) {
  .collaborator-avatar-row { padding-left: 0; }
  .collaborator-avatar-slot { margin-left: 0; }
  .collaborator-avatar, .collaborator-overflow { width: 44px; height: 44px; flex-basis: 44px; }
}
@media (prefers-reduced-motion: reduce) {
  .collaborator-avatar-face { transition-property: opacity; transform: none !important; }
  .collaborator-mode svg { animation: none !important; }
  .collaborator-list-enter-active, .collaborator-list-leave-active, .collaborator-list-move { transition-property: opacity; }
  .collaborator-list-enter-from, .collaborator-list-leave-to { transform: none; }
}
[data-atlas-reduced-motion] .collaborator-avatar-face { transition-property: opacity; transform: none !important; }
[data-atlas-reduced-motion] .collaborator-mode svg { animation: none !important; }
[data-atlas-reduced-motion] .collaborator-list-enter-active,
[data-atlas-reduced-motion] .collaborator-list-leave-active,
[data-atlas-reduced-motion] .collaborator-list-move { transition-property: opacity; }
[data-atlas-reduced-motion] .collaborator-list-enter-from,
[data-atlas-reduced-motion] .collaborator-list-leave-to { transform: none; }
</style>
