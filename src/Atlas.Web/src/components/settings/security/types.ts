import type { NetworkKind, ProtectionMode } from "../../../protection";

/**
 * Vocabulary of the security composites (protection modes, trusted networks), in the words of the V4 API (src/protection.ts).
 */
export type { NetworkKind, ProtectionMode } from "../../../protection";
/** A vault or secret either inherits or sets its own mode. */
export type ProtectionSetting = ProtectionMode | "inherit";
/** What the network wizard produces; the server normalizes the range and adds id and version. */
export interface NetworkRuleDraft {
  name: string;
  kind: NetworkKind;
  cidr: string;
}
