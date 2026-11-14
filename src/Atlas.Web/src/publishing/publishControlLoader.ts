import { shallowRef, type Component, type ShallowRef } from "vue";

/**
 * The publish control is an async component, and an async wrapper mounts its content a task after the first render, so
 * when editing starts it would be the late part of the top bar that pops in beside the buttons that already arrived.
 * Its chunk is fetched on the intent to edit; once it is in, the component itself is rendered at once (`ready`).
 *
 * The fetch is a courtesy and never an error: when it fails (offline, a deploy that replaced the chunk) nothing happens
 * and the async wrapper loads the control when it is rendered; after `dispose` (the app is gone) nothing is fetched
 * and a fetch that finishes late is ignored.
 */
export interface PublishControlLoader {
  readonly ready: ShallowRef<Component | null>;
  prepare(): Promise<void>;
  dispose(): void;
}

export function createPublishControlLoader(load: () => Promise<{ default: Component }>): PublishControlLoader {
  const ready = shallowRef<Component | null>(null);
  let disposed = false, pending: Promise<void> | undefined;
  const arrived = (component: Component) => { if (!disposed) ready.value = component; };
  return {
    ready,
    prepare() {
      if (disposed || ready.value) return Promise.resolve();
      pending ??= load().then(
        module => arrived(module.default),
        () => { pending = undefined; },
      );
      return pending;
    },
    dispose() { disposed = true; },
  };
}
