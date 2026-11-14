import { tellTwins } from "../spaceTwins";
import { foldText } from "./foldText";

/**
 * P8 „Stejná jména se vždy rozliší“, for a picker whose options come from its owner: of options with one name the ones whose own
 * description does not tell them apart (it is empty or the same) get their order, „2. z 3“ (spaceTwins.ts). An option without a twin, or
 * whose description is its own, gets `undefined` when the picker shows the description itself (`describes`, the default). A picker that shows
 * no description (SearchableTags) passes `false` and gets the description as the hint where it is unique among the twins. Aligned with `options`.
 */
export function twinHints<T extends { label: string; description?: string }>(options: readonly T[], describes = true): Array<string | undefined> {
  return tellTwins(options, option => foldText(option.label.trim()), option => option.description).map(hint => (hint && (!describes || /^\d+\. z \d+$/.test(hint)) ? hint : undefined));
}
