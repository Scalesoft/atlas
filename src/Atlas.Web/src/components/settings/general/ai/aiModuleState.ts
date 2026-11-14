import { api } from "../../../../api";
import type { ModuleState } from "../moduleState";
import { fallbackState, isMissingEndpoint, overviewState, type AiOverview, type AiProfileView, type AiSwitchView } from "./aiAdmin";

/**
 * The one word on the module's card: Vypnuto / Dostupné / Nedostupné / Nenastaveno, from the overview
 * (A3). Against a server without the overview (older build) the card says what A1 knows (the
 * switch and whether Embeddings are configured), never more.
 */
export async function aiModuleState(): Promise<ModuleState> {
  try {
    return overviewState(await api<AiOverview>("/admin/ai"));
  } catch (failure) {
    if (!isMissingEndpoint(failure)) throw failure;
  }
  const [sw, embeddings] = await Promise.all([api<AiSwitchView>("/admin/ai/switch"), api<AiProfileView>("/admin/ai/profiles/embeddings")]);
  return fallbackState(sw, embeddings);
}
