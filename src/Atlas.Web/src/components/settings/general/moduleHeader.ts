import { inject, provide, type InjectionKey, type Ref } from "vue";

/**
 * The header of a module page (refresh 2.5, board 10): the module page owns the header with the module's mark and
 * state; the module's own form puts its on/off switch there (`<Teleport :to="header.target.value">`), so the switch
 * stays part of the form's draft and its SaveBar. A form mounted on its own (the first-run setup, a test) gets no
 * header and keeps the switch as a row of its first section.
 */
export interface ModuleHeader {
  target: Ref<HTMLElement | undefined>;
}
const key: InjectionKey<ModuleHeader> = Symbol("settings-module-header");

export function provideModuleHeader(header: ModuleHeader) {
  provide(key, header);
}
export function useModuleHeader(): ModuleHeader | undefined {
  return inject(key, undefined);
}
