import { ref } from "vue";
import { readStored, writeStored } from "./lib/browserStorage";

export type HistoryFormat = "rendered" | "source";

const KEY = "atlas.history.format";
function stored(): HistoryFormat {
  return readStored(KEY) === "source" ? "source" : "rendered";
}
// One choice for every history view (a revision's changes and the full version, in the page history
// and in draft approval): switching in one keeps the other, and the browser remembers it.
const format = ref<HistoryFormat>(stored());

export function useHistoryFormat() {
  return {
    format,
    choose(value: HistoryFormat) {
      format.value = value;
      writeStored(KEY, value);
    },
  };
}
