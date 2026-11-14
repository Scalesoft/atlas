import type { SharingPublicMessages } from "./components-sharing-public";

const en: SharingPublicMessages = {
  loading: "Loading the shared page…",
  unavailableTitle: "The link isn't available",
  unavailable: "The link isn't available. It may have expired or been revoked.",
  loadFailed: "The shared page could not be loaded right now.",
  retry: "Retry",
  live: (date) => `Live · saved ${date}`,
  revision: (version) => `Published version ${version}`,
  validUntil: (date) => `valid until ${date}`,
  failed: "Couldn't display this shared page.",
  documentLabel: "Shared read-only document",
};
export default en;
