import { translate } from ".";

/** The „Co je nového“ notice (whatsNew/notice.ts); eager, so small. The release notes themselves are bilingual files (whatsNew/releaseNotes.ts). The Czech source. */
const cs = {
  patchTitle: (release: string, named: string) => `Atlas ${release} — opravy verze ${named}`,
  title: (named: string) => `Atlas ${named} — co je nového`,
  patchText: "Drobné opravy verze, kterou už znáte.",
  text: "Podívejte se, co se v nové verzi změnilo.",
  show: "Zobrazit",
};

export type WhatsNewMessages = typeof cs;
export default translate(cs, { en: () => import("./whatsnew.en") });
