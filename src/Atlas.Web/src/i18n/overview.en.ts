import type { OverviewMessages } from "./overview";

const en: OverviewMessages = {
  invalidAttribute: "Invalid overview attribute value.",
  nodeLabel: "Overview",
  unavailable: "This overview is not available.",
  loading: name => `Loading the overview: ${name}`,
  slash: {
    label: name => `Overview: ${name}`,
    description: "An overview of the components of this kind on the page",
    group: "Overview",
  },
  level: {
    label: "Detail",
    thumb: "Level of detail",
  },
};

export default en;
