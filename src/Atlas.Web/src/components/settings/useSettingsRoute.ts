import { computed, inject, onBeforeUnmount, provide, ref, type ComputedRef, type InjectionKey } from "vue";

/**
 * Hash routing for settings screens: `#/<base>/<segment>/<segment>?filter=value`.
 *
 * Every section is a real link, so it can be bookmarked, opened in a new tab and reached with
 * the browser's back button. Filters live in the query and are written with `replaceState`, so
 * narrowing a list never adds a history entry. The application shell listens to `popstate`
 * and hash navigation as before; this composable only reads the hash and produces links.
 */
export interface SettingsRoute {
  /** Path below the base, without leading slash: `global/security/networks`. */
  path: string;
  segments: string[];
  query: Readonly<Record<string, string>>;
}

export interface SettingsRouter {
  base: string;
  route: ComputedRef<SettingsRoute>;
  href(path: string, query?: Record<string, string | undefined | null>): string;
  navigate(path: string, query?: Record<string, string | undefined | null>): void;
  /** Goes to a full hash of this surface, past the leave guard (after the user confirmed leaving). */
  visit(hash: string): void;
  /** Replaces the query of the current route without a history entry. */
  setQuery(query: Record<string, string | undefined | null>): void;
  /** True when `path` is the current path or one of its ancestors. */
  isActive(path: string, exact?: boolean): boolean;
}

export function parseSettingsHash(hash: string, base: string): SettingsRoute {
  const prefix = base.startsWith("#") ? base : `#${base}`;
  if (!hash.startsWith(prefix)) return { path: "", segments: [], query: {} };
  const rest = hash.slice(prefix.length).replace(/^\/+/, "");
  const [rawPath = "", rawQuery = ""] = rest.split("?", 2);
  const segments = rawPath.split("/").filter(Boolean).map((segment) => {
    try {
      return decodeURIComponent(segment);
    } catch {
      return segment;
    }
  });
  const query: Record<string, string> = {};
  for (const [key, value] of new URLSearchParams(rawQuery)) query[key] = value;
  return { path: segments.join("/"), segments, query };
}

export function buildSettingsHash(base: string, path: string, query?: Record<string, string | undefined | null>) {
  const prefix = base.startsWith("#") ? base : `#${base}`;
  const clean = path.replace(/^\/+|\/+$/g, "");
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query ?? {})) if (value) params.set(key, value);
  const search = params.toString();
  return `${prefix}${clean ? `/${clean}` : ""}${search ? `?${search}` : ""}`;
}

const key: InjectionKey<SettingsRouter> = Symbol("settings-router");

export interface SettingsRouteOptions {
  /**
   * Asked before the route moves to another hash of this surface (a link, the nav, Back). Returning
   * false keeps the current page: the address is put back and the caller asks the user (unsaved
   * changes), then continues with `visit`. Hashes outside the surface belong to the application shell.
   */
  guard?: (target: string) => boolean;
}

/** Creates the router for a settings surface and shares it with every page below it. */
export function provideSettingsRoute(base: string, options: SettingsRouteOptions = {}): SettingsRouter {
  const prefix = base.startsWith("#") ? base : `#${base}`;
  const hash = ref(typeof location === "undefined" ? "" : location.hash);
  const within = (value: string) => value === prefix || value.startsWith(`${prefix}/`) || value.startsWith(`${prefix}?`);
  let bypass: string | undefined;
  const sync = () => {
    const next = location.hash;
    if (next === hash.value) return;
    const forced = bypass === next;
    bypass = undefined;
    const pathOf = (value: string) => value.split("?", 1)[0];
    // Query changes (filters) never leave the page, so they never ask.
    if (!forced && options.guard && within(next) && within(hash.value) && pathOf(next) !== pathOf(hash.value) && !options.guard(next)) {
      history.pushState(history.state, "", hash.value);
      return;
    }
    hash.value = next;
  };
  window.addEventListener("hashchange", sync);
  window.addEventListener("popstate", sync);
  onBeforeUnmount(() => {
    window.removeEventListener("hashchange", sync);
    window.removeEventListener("popstate", sync);
  });
  const route = computed(() => parseSettingsHash(hash.value, base));
  const router: SettingsRouter = {
    base,
    route,
    href: (path, query) => buildSettingsHash(base, path, query),
    navigate(path, query) {
      const target = buildSettingsHash(base, path, query);
      if (target === location.hash) return;
      // A real hash navigation: the shell's history handling (popstate) sees it like a clicked link.
      location.hash = target;
      sync();
    },
    visit(target) {
      if (target === location.hash) return;
      bypass = target;
      location.hash = target;
      sync();
    },
    setQuery(query) {
      const target = buildSettingsHash(base, route.value.path, query);
      if (target === location.hash) return;
      history.replaceState(history.state, "", target);
      hash.value = target;
    },
    isActive(path, exact = false) {
      const current = route.value.path;
      return exact ? current === path : current === path || current.startsWith(`${path}/`);
    },
  };
  provide(key, router);
  return router;
}

export function useSettingsRoute(): SettingsRouter {
  const router = inject(key, undefined);
  if (!router) throw new Error("useSettingsRoute() needs provideSettingsRoute() in an ancestor");
  return router;
}

/** For components that also work outside a settings surface (tests, stories). */
export function useOptionalSettingsRoute(): SettingsRouter | undefined {
  return inject(key, undefined);
}
