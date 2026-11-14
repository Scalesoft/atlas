import { translate } from ".";

/** The Projector's page actions (catalog switch, a deployment founded), small on purpose: the shell's useProjectorPage needs them eagerly, the rest of the Projector's texts (projector.ts) load with its area. The Czech source. */
const cs = {
  isCatalog: "Stránka je katalog požadavků.",
  isNotCatalog: "Stránka už není katalog požadavků.",
  deploymentCreated: (title: string) => `Nasazení „${title}“ je založené.`,
  open: "Otevřít",
};

export type ProjectorPageMessages = typeof cs;
export default translate(cs, { en: () => import("./projector-page.en") });
