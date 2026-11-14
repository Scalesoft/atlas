import { h } from "vue";
import { lazyComponent } from "../../lazyComponent";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import ErrorState from "./ErrorState.vue";
import t from "../../i18n/settings-shell";

/**
 * What a surface or a page of the settings shows when its chunk cannot be loaded (offline, or replaced by a deployment
 * while the tab was open): the reason and the reload the module settings offer, in place of an empty area. The loaded
 * or failed result stays with the component for the life of the tab, so a reload is the way forward.
 */
const Unavailable = () => h(ErrorState, {
  title: t.lazy.title,
  text: t.lazy.text,
  retryLabel: t.lazy.retry,
  onRetry: () => window.location.reload(),
});

/** `lazyComponent` for the settings: the skeleton while the chunk loads, `Unavailable` when it stays gone. Declare the result once per module. */
export const lazySettings = (loader: Parameters<typeof lazyComponent>[0]) => lazyComponent(loader, { loading: LoadingSkeleton, error: Unavailable });
