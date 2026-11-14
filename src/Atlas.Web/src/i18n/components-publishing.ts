import { plural, translate } from ".";

/** components/publishing: PublishControl, PublishFindings, ChangeSummaryField, PagePublicationState. The Czech source. */
const cs = {
  control: {
    publish: "Publikovat",
    publishAnyway: "Publikovat i tak",
    publishing: "Publikování…",
    group: "Publikace",
    withSummary: "Publikovat s popisem změny…",
    before: "Před publikací",
    backToEdit: "Zpět k úpravám",
    discard: "Zahodit změny…",
    /** „1 kontrola“, „3 kontroly“, „5 kontrol“. */
    checkCount: (count: number) => plural(count, { one: "# kontrola", few: "# kontroly", other: "# kontrol" }),
  },
  /** DiscardChangesDialog. */
  discard: {
    action: "Zahodit změny",
    text: "Stránka se vrátí k publikované verzi. Zahozený stav zůstane v historii a jde obnovit.",
    label: "Co se zahodí (oproti publikované verzi)",
    done: "Změny byly zahozeny. Stránka je zpět v publikované verzi; zahozený stav zůstal v historii.",
  },
  findings: {
    warning: "Varování",
    info: "Informace",
    label: "Nálezy kontroly",
    checking: "Kontroluji…",
    show: "Zobrazit",
    open: "Otevřít",
  },
  summary: {
    label: "Popis změny (nepovinné)",
    help: "Zobrazí se v historii stránky a v Poslední publikace.",
    pageChanged: "Stránka se mezitím změnila. Zkuste navrhnout znovu.",
    proposalFailed: "Návrh se nepodařilo připravit. Můžete popis napsat ručně.",
    suggest: "Navrhnout",
    proposalLabel: "Návrh popisu změny",
  },
};
export type PublishingMessages = typeof cs;

export default translate(cs, { en: () => import("./components-publishing.en") });
