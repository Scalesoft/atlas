import type { MarkdownIconName } from "@atlas/markdown/icons";
import type { RequirementDirection, RequirementState } from "./requirementTypes";

// How states and directions look in the views (the editor's icon registry); their words are in requirementSummary.ts.

export const stateIcons: Record<RequirementState, MarkdownIconName> = {
  planned: "clock", waiting_for_input: "hourglass", in_progress: "loader-circle", done: "circle-check", blocked: "octagon-x", not_applicable: "ban",
};

export const directionIcons: Record<RequirementDirection, MarkdownIconName> = { internal: "user", inbound: "arrow-down", outbound: "arrow-up" };
