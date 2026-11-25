import { hintLabel } from "../keyboard/shortcuts";
import { translate } from ".";

/** The page tree and the status badge of a page (components/PageTree, PageStatusBadge), small because the shell shows them at once; the rest of the page-area texts are in components-b. The Czech source. */
const cs = {
  tree: {
    noNest: "Sem nejde zanořit",
    dropRefused: (title: string) => `Sem stránku přesunout nemůžete: stránku „${title}“ nemáte právo upravovat.`,
    noAccessLink: "Odkaz bez přístupu",
    open: "Otevřít",
    openAttached: "Otevřít připojenou tabuli",
    copyLink: "Kopírovat odkaz",
    newSubpage: "Nová podstránka",
    newBoard: "Nová tabule",
    rename: "Přejmenovat",
    move: "Přesunout…",
    sortByTitle: "Řadit abecedně",
    removeFromTree: "Odebrat ze stromu",
    moveToTrash: "Přesunout do koše…",
    expand: "Rozbalit",
    collapse: "Sbalit",
    newName: (title: string) => `Nový název stránky ${title}`,
    sortedTitle: "Podstránky řazené abecedně",
    sortedMark: "podstránky řazené abecedně",
    referenceNoAccess: "Odkaz, k cíli nemáte přístup",
    referenceMark: " — Odkaz",
    hasBoard: `má tabuli nasazení (${hintLabel("Mod+Enter")})`,
    openBoardOf: (title: string) => `Otevřít tabuli nasazení stránky ${title}`,
    deploymentBoard: "Tabule nasazení",
    actionsOf: (title: string) => `Akce: ${title}`,
    actions: "Akce",
  },
  badge: {
    draft: "Koncept",
    unpublished: "Nepublikované změny",
    published: "Publikováno",
  },
};
export type ComponentsTreeMessages = typeof cs;

export default translate(cs, { en: () => import("./components-tree.en") });
