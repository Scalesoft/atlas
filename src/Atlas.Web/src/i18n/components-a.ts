import { translate } from ".";

/** Small shared parts of the shell: ActionMenu, AppDialog, Loading*, AtlasVersionInfo, CollaboratorAvatars, CopyPageLink, EditLockToggle,
 * DocumentTitle, ActivityHeatmap, KeyboardHelpDialog. The Czech source; kept small because these load eagerly. */
const cs = {
  actionMenu: { actions: "Co s tím jde dělat" },
  close: "Zavřít",
  loading: "Načítání…",
  versionInfo: { whatsNew: "Co je nového", info: "Informace o verzi", unknown: "neznámá verze" },
  collaborators: {
    group: "Spolupracovníci v dokumentu",
    more: "Další spolupracovníci",
    moreCount: (count: number) => `Další spolupracovníci: ${count}`,
    agent: (name: string) => `${name} – zapisuje do stránky`,
    goTo: (name: string) => `Přejít na kurzor uživatele ${name}`,
    noCursor: (name: string) => `${name} – kurzor není dostupný`,
    cursorUnavailable: "Kurzor není dostupný",
  },
  copyLink: {
    label: "Kopírovat odkaz na stránku",
    manual: "Automatické kopírování není dostupné. Zkopírujte vybraný odkaz.",
    field: "Odkaz na stránku",
    copied: "Odkaz zkopírován",
    done: "Hotovo",
  },
  editLock: {
    readOnly: "Jen pro čtení",
    unlock: "Jen pro čtení: odemknout úpravy",
    lock: "Zamknout úpravy",
    edit: "Upravit",
    pulse: "Jen pro čtení. Upravíte po stisknutí Upravit.",
  },
  documentTitle: { pageName: "Název stránky" },
};
export type ComponentsAMessages = typeof cs;

export default translate(cs, { en: () => import("./components-a.en") });
