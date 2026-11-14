// The attributes of a requirement: the one vocabulary the model, the views and the projection share.

export const requirementDirections = ["internal", "inbound", "outbound"] as const;
export const requirementStates = ["planned", "waiting_for_input", "in_progress", "done", "blocked", "not_applicable"] as const;
export type RequirementDirection = typeof requirementDirections[number];
export type RequirementState = typeof requirementStates[number];

export interface RequirementAttributes {
  readonly key: string;
  readonly direction: RequirementDirection;
  readonly state: RequirementState;
  /** The block's own word for its state („ověřit“, plan 3.4 §4), shown instead of the state's; "" for the state's own. Counts as `state`. */
  readonly stateLabel: string;
  readonly date: string;
  readonly party: string;
  /** The contact the party is (plan 3.1 §2.3), its directory id; "" for a party written as text. */
  readonly partyContactId: string;
  readonly where: string;
  /** The external service an input comes through (ÚZIS, NIA, BankID…, plan 3.4 §5); a catalog's default, rewritable. */
  readonly system: string;
  /** Keys of the page's blocks, or `key@pageId` for a block of another page, comma-separated. */
  readonly requires: string;
}

export interface RequirementItem { readonly attrs: RequirementAttributes; readonly title: string }

export const requirementDefaults: RequirementAttributes = { key: "", direction: "internal", state: "planned", stateLabel: "", date: "", party: "", partyContactId: "", where: "", system: "", requires: "" };

/** The attributes a requirement changes in place, each in its one field (PR-2). */
export type RequirementField = "party" | "date" | "requires" | "system" | "where";
