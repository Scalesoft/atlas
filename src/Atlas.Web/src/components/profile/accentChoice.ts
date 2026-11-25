/** One swatch of AccentPicker: an id, its name and the hue of its colour; with a `swatch` (its own light and dark colour) it is drawn in exactly that colour. */
export interface AccentChoice<Id extends string = string> { id: Id; label: string; hue: number; swatch?: { light: string; dark: string } }
