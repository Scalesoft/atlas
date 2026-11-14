<script setup lang="ts">
import { DropdownMenuGroup } from "reka-ui";

// A named group of ActionDisclosure items (Vložit, Projector, Export, Stránka): a line separates it from the next one and a
// screen reader announces its name. Part of ActionDisclosure; its styles live there.
defineProps<{ label: string }>();
</script>

<template>
  <DropdownMenuGroup class="action-disclosure-group" :aria-label="label"><slot /></DropdownMenuGroup>
</template>
