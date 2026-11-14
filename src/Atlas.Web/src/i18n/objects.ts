import { plural, translate } from ".";

/** Objects (objects/): type and relation names, usage counts. The Czech source. */
const cs = {
  types: {
    page: { label: "Stránka", plural: "Stránky" },
    board: { label: "Tabule", plural: "Tabule" },
    contact: { label: "Kontakt", plural: "Kontakty" },
    note: { label: "Poznámka", plural: "Poznámky" },
  },
  kinds: {
    link: { label: "odkazuje na", incoming: "odkazuje sem", use: "odkaz" },
    placement: { label: "je na tabuli", incoming: "je tu umístěno", use: "karta na tabuli" },
    attached: { label: "patří ke stránce", incoming: "má tabuli nasazení", use: "tabule stránky" },
    mention: { label: "zmiňuje", incoming: "je zmíněno ve stránce", use: "zmínka" },
    reference: { label: "je ve stromu", incoming: "je ve stromu jako odkaz", use: "odkaz ve stromu" },
    template: { label: "vznikla ze šablony", incoming: "podle ní vznikla stránka", use: "stránka ze šablony" },
    party: { label: "čeká na", incoming: "se na něj čeká v požadavku", use: "strana požadavku" },
    catalog: { label: "používá z katalogu", incoming: "je použito v nasazení", use: "požadavek z katalogu" },
  },
  /** „na 1 místě“, „na 3 místech“: the locative after „Použito na“. */
  places: (count: number) => plural(count, { one: "# místě", other: "# místech" }),
  detail: "Detail objektu",
  unavailable: "Nedostupné",
  untitled: "Bez názvu",
  leadsNowhere: "Odkaz nikam nevede",
  brokenLink: "rozbitý odkaz",
};
export type ObjectsMessages = typeof cs;

export default translate(cs, { en: () => import("./objects.en") });
