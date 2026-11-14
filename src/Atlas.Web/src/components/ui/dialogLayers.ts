import type { InjectionKey, Ref } from "vue";

type EscapeLayer = (event: KeyboardEvent) => void;
export interface DialogLayers {
  target: Readonly<Ref<HTMLDialogElement | undefined>>;
  registerEscape: (layer: EscapeLayer) => () => void;
}
// Portalled controls must remain in their native dialog's top layer. The nearest
// dialog also owns their Escape order, ahead of its owner's bubbling listener.
export const dialogLayersKey: InjectionKey<DialogLayers> = Symbol("dialogLayers");
