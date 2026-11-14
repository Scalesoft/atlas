import { translate } from ".";

/** The public shared page (sharing/PublicSharedPage): shown to people without an account, so its file stays small. The Czech source. */
const cs = {
  loading: "Načítání sdílené stránky…",
  unavailableTitle: "Odkaz není dostupný",
  unavailable: "Odkaz není dostupný. Mohl vypršet nebo být zrušen.",
  loadFailed: "Sdílenou stránku se teď nepodařilo načíst.",
  retry: "Zkusit znovu",
  live: (date: string) => `Živě · uloženo ${date}`,
  revision: (version: number) => `Publikovaná revize ${version}`,
  validUntil: (date: string) => `platí do ${date}`,
  failed: "Tuto sdílenou stránku se nepodařilo zobrazit.",
  documentLabel: "Sdílený dokument pouze pro čtení",
};
export type SharingPublicMessages = typeof cs;

export default translate(cs, { en: () => import("./components-sharing-public.en") });
