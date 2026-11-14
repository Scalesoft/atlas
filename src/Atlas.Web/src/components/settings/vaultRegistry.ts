import { locale } from "../../i18n";
import { computed, inject, onBeforeUnmount, onMounted, provide, ref, type ComputedRef, type InjectionKey, type Ref } from "vue";
import { errorMessage } from "../../api";
import { vaultApi, type Vault, type VaultLocation } from "../../vaults";

/**
 * The vault registry of one settings surface: the vaults the caller manages and the locations,
 * loaded once and shared by the navigation counts, the overview and the vault pages. Pages that
 * change a vault put the server's answer back with `replace`. It refreshes when the window regains
 * focus; no timer polls it (live updates in Atlas come over SignalR, never by polling).
 */
export interface VaultRegistry {
  vaults: Ref<Vault[]>;
  locations: Ref<VaultLocation[]>;
  loading: Ref<boolean>;
  error: Ref<string>;
  loaded: Ref<boolean>;
  load(): Promise<void>;
  replace(vault: Vault): void;
  remove(id: string): void;
  vault(id: string): Vault | undefined;
  inSpace(spaceId: string): Vault[];
  troubled: ComputedRef<Vault[]>;
  saveLocation(location: VaultLocation): void;
  removeLocation(id: string): void;
}

const key: InjectionKey<VaultRegistry> = Symbol("vault-registry");

export function createVaultRegistry(): VaultRegistry {
  const vaults = ref<Vault[]>([]);
  const locations = ref<VaultLocation[]>([]);
  const loading = ref(false);
  const loaded = ref(false);
  const error = ref("");
  let generation = 0;
  // A local change (created, archived, renamed) makes a list read that started before it stale.
  let changes = 0;
  let disposed = false;

  async function load() {
    const current = ++generation;
    const seen = changes;
    loading.value = true;
    try {
      const [list, places] = await Promise.all([vaultApi.list(), vaultApi.locations().catch(() => [] as VaultLocation[])]);
      if (disposed || current !== generation || seen !== changes) return;
      vaults.value = list;
      locations.value = places;
      error.value = "";
      loaded.value = true;
    } catch (failure) {
      if (!disposed && current === generation) error.value = errorMessage(failure);
    } finally {
      if (!disposed && current === generation) loading.value = false;
    }
  }
  const byName = (a: { name: string }, b: { name: string }) => a.name.localeCompare(b.name, locale);
  const focused = () => void load();
  onMounted(() => {
    void load();
    window.addEventListener("focus", focused);
  });
  onBeforeUnmount(() => {
    disposed = true;
    window.removeEventListener("focus", focused);
  });
  return {
    vaults,
    locations,
    loading,
    loaded,
    error,
    load,
    replace(vault) {
      changes++;
      vaults.value = vaults.value.some((item) => item.id === vault.id)
        ? vaults.value.map((item) => (item.id === vault.id ? vault : item))
        : [...vaults.value, vault].sort(byName);
    },
    remove(id) {
      changes++;
      vaults.value = vaults.value.filter((item) => item.id !== id);
    },
    vault: (id) => vaults.value.find((item) => item.id === id),
    inSpace: (spaceId) => vaults.value.filter((item) => item.spaceId === spaceId),
    troubled: computed(() => vaults.value.filter((item) => item.status !== "unlocked")),
    saveLocation(location) {
      changes++;
      locations.value = [...locations.value.filter((item) => item.id !== location.id), location].sort(byName);
    },
    removeLocation(id) {
      changes++;
      locations.value = locations.value.filter((item) => item.id !== id);
    },
  };
}

export function provideVaultRegistry(registry: VaultRegistry) {
  provide(key, registry);
}
export function useVaultRegistry(): VaultRegistry {
  const registry = inject(key, undefined);
  if (!registry) throw new Error("useVaultRegistry() needs provideVaultRegistry() in an ancestor");
  return registry;
}
