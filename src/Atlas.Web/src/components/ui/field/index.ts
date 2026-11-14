// Its utilities are generated in the lazy settings stylesheet (styles/tailwind.css leaves this folder out): whoever uses the
// primitive loads them with it, also a screen that loads nothing else of the settings UI (a board, SH-16).
import "../../settings/settings-tailwind.css"
import type { VariantProps } from "class-variance-authority"
import { cva } from "class-variance-authority"

// shadcn-vue "Field" (new-york), copied in by hand: the CLI is not run in this repository.
export { default as Field } from "./Field.vue"
export { default as FieldContent } from "./FieldContent.vue"
export { default as FieldDescription } from "./FieldDescription.vue"
export { default as FieldLabel } from "./FieldLabel.vue"
export { default as FieldTitle } from "./FieldTitle.vue"

export const fieldVariants = cva(
  "group/field flex w-full gap-3 data-[invalid=true]:text-destructive",
  {
    variants: {
      orientation: {
        vertical: ["flex-col [&>*]:w-full [&>.sr-only]:w-auto"],
        horizontal: [
          "flex-row items-center",
          "[&>[data-slot=field-label]]:flex-auto",
          "has-[>[data-slot=field-content]]:items-start has-[>[data-slot=field-content]]:[&>[role=checkbox],[role=radio]]:mt-px",
        ],
        responsive: [
          "flex-col [&>*]:w-full [&>.sr-only]:w-auto @md/field-group:flex-row @md/field-group:items-center @md/field-group:[&>*]:w-auto",
          "@md/field-group:[&>[data-slot=field-label]]:flex-auto",
          "@md/field-group:has-[>[data-slot=field-content]]:items-start @md/field-group:has-[>[data-slot=field-content]]:[&>[role=checkbox],[role=radio]]:mt-px",
        ],
      },
    },
    defaultVariants: {
      orientation: "vertical",
    },
  },
)

export type FieldVariants = VariantProps<typeof fieldVariants>
