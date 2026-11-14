import { computed, reactive } from "vue";
import { errorMessage } from "../../../api";
import { apiTokenAdminApi } from "../../../accessApi";
import { networksApi, protectionApi, type ProtectionMode } from "../../../protection";

/**
 * The global security facts of the Přehled (wiki administrator): the instance default and what inherits it, the active trusted
 * networks and the vaults and secrets with their own mode. Loaded when global settings open and again on the overview.
 */
export function createSecurityOverview(api: { protection: typeof protectionApi; networks: typeof networksApi; tokens?: Pick<typeof apiTokenAdminApi, "summary"> } = { protection: protectionApi, networks: networksApi, tokens: apiTokenAdminApi }) {
  const state = reactive({
    loaded: false,
    error: "",
    mode: null as ProtectionMode | null,
    inheriting: { vaults: 0, secrets: 0 },
    networks: 0,
    activeNetworks: 0,
    overrides: { vaults: 0, secrets: 0 },
    /** The token-rights upgrade set levels no wiki administrator has confirmed yet (api_levels_review). */
    levelsReview: false,
  });
  let generation = 0;
  async function load() {
    const current = ++generation;
    try {
      const [instance, networks, overrides, tokens] = await Promise.all([
        api.protection.get("instance"), api.networks.list(), api.protection.overrides(),
        // An extra: a failing summary never hides the protection facts.
        api.tokens?.summary().catch(() => ({ levelsReview: false })),
      ]);
      if (current !== generation) return;
      state.mode = instance.effective;
      state.inheriting = instance.impact ?? { vaults: 0, secrets: 0 };
      state.networks = networks.items.length;
      state.activeNetworks = networks.items.filter((item) => item.isActive).length;
      state.overrides = { vaults: overrides.filter((item) => !item.secretId).length, secrets: overrides.filter((item) => item.secretId).length };
      state.levelsReview = tokens?.levelsReview === true;
      state.error = "";
    } catch (failure) {
      if (current === generation) state.error = errorMessage(failure);
    } finally {
      if (current === generation) state.loaded = true;
    }
  }
  return Object.assign(state, {
    load,
    outsideWithoutNetworks: computed(() => state.mode === "outsideTrustedNetworks" && state.activeNetworks === 0),
  });
}
export type SecurityOverview = ReturnType<typeof createSecurityOverview>;
