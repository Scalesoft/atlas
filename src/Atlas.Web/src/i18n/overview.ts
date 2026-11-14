import { translate } from ".";

/** The overview cards' shared parts (overview/): the slash entry, the level slider, the placeholder. The Czech source. */
const cs = {
  invalidAttribute: "Neplatná hodnota atributu přehledu.",
  /** The overview node's name in the page source („Chráněný blok: Přehled“). */
  nodeLabel: "Přehled",
  unavailable: "Přehled, který tu není k dispozici.",
  loading: (name: string) => `Načítání přehledu: ${name}`,
  slash: {
    label: (name: string) => `Přehled: ${name}`,
    description: "Přehled komponent tohoto druhu na stránce",
    group: "Přehled",
  },
  level: {
    label: "Detail",
    thumb: "Úroveň detailu",
  },
};
export type OverviewMessages = typeof cs;

export default translate(cs, { en: () => import("./overview.en") });
