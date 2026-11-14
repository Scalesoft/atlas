import type { AiProfileView } from "./aiAdmin";

export interface SavedAiProfile {
  id: string;
  name: string;
  version: string;
  profile: AiProfileView;
  isActive: boolean;
}
export interface SavedAiProfiles {
  items: SavedAiProfile[];
  activeVersion: string | null;
}
