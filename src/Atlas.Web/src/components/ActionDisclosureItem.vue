<script setup lang="ts">
import { DropdownMenuCheckboxItem } from "reka-ui";
import { DropdownMenuItem } from "./ui/dropdown-menu";
import AtlasIcon from "./AtlasIcon.vue";

// `destructive`: an action that throws work away or removes something (Přesunout do koše), last in the menu.
// `checked`: a switch among the actions („Řadit abecedně“): a menuitemcheckbox with the check while on; `click` still toggles it.
// The explicit `undefined` default: Vue casts an absent boolean prop to false, which made every action a checkbox.
withDefaults(defineProps<{ disabled?: boolean; destructive?: boolean; checked?: boolean }>(), { checked: undefined });
</script>

<!-- One action inside ActionDisclosure. `as-child` keeps a real <button>, so the
     panel's existing styles (`.action-disclosure-panel :deep(button)`) and every
     caller's markup carry over unchanged, while reka-ui owns the roving focus,
     typeahead and menuitem role.

     This replaces stamping `role="menuitem"` onto slotted children from an
     `onUpdated` hook: that only ever labelled the buttons, it could not make
     them participate in keyboard navigation, and it mutated DOM the component
     did not render. -->
<template>
  <DropdownMenuCheckboxItem v-if="checked !== undefined" as-child :model-value="checked" :disabled="disabled">
    <button type="button" :disabled="disabled"><slot /><AtlasIcon v-if="checked" class="action-disclosure-check" name="check" :size="14" /></button>
  </DropdownMenuCheckboxItem>
  <DropdownMenuItem v-else as-child :disabled="disabled">
    <button type="button" :disabled="disabled" :data-destructive="destructive || undefined"><slot /></button>
  </DropdownMenuItem>
</template>

<style scoped>
.action-disclosure-check { margin-left: auto; padding-left: 12px; box-sizing: content-box; color: var(--accent); }
</style>
