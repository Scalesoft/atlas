import { type ClassValue, clsx } from "clsx";
import { twMerge } from "tailwind-merge";

// Generated shadcn components import this by convention. `twMerge` is what lets
// a caller's `class` override a component's own default utility rather than
// both landing in the class list and the earlier one winning by source order.
export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs));
}
