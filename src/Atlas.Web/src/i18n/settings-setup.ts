import { translate } from ".";

const cs = {
  backupImport: { title: "Obnovit ze zálohy", description: "Přidat obsah nebo obnovit archivované stránky" },
  modules: "Nastavení modulů",
  eyebrow: "ATLAS · VAŠE WIKI JE PŘIPRAVENÁ",
  heading: "Připojte své nástroje",
  intro: "Vyberte, co chcete připojit. Vše můžete nechat na později.",
  completed: "Wiki a správce jsou vytvořené",
  opening: "Otevírání…",
  openWiki: "Otevřít wiki",
  modulesHeading: "Moduly a integrace",
  loading: "Načítání modulů…",
  navigation: "Nastavení jednotlivých modulů",
  formMissingTitle: "Formulář modulu není dostupný",
  formMissingText: "Tato verze klienta formulář modulu nezná. Obnovte stránku nebo ověřte verzi nasazení.",
  refresh: "Obnovit stránku",
  dirty: "Před přechodem na další modul uložte nebo zrušte rozepsané změny.",
  back: "Zpět",
  step: (position: number, total: number) => `Krok ${position} z ${total} · nepovinné`,
  ready: "Připraveno",
  later: "Kdykoli později ve Správě wiki",
  finishing: "Dokončování…",
  nextModule: "Další možnost",
  emptyTitle: "Žádné moduly k nastavení",
  emptyText: "Tato instalace nemá žádný modul, který by šlo nastavit.",
  loadFailed: "Moduly se nepodařilo načíst",
  reload: "Načíst znovu",
  adminOnlyTitle: "Jen pro správce wiki",
  adminOnlyText: "Moduly a integrace nastavuje správce wiki.",
};
export type SettingsSetupMessages = typeof cs;

export default translate(cs, { en: () => import("./settings-setup.en") });
