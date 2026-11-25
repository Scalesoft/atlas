import { defineComponent, h, type Component } from "vue";
import type { AiProvenanceView, AiSentenceView, AiSourceView } from "../../../ai/assistantApi";
import { TooltipProvider } from "../../ui/tooltip";

// Shared fixtures of the AI component tests (plan W8).
export const source = (number: number, extra: Partial<AiSourceView> = {}): AiSourceView => ({
  number, type: "page", pageId: `page-${number}`, spaceId: "space-1", spaceName: "IT a infrastruktura",
  title: `Stránka ${number}`, headingTitle: `Nadpis ${number}`, headingId: `h-${number}`, excerpt: `Úryvek ${number}.`, cited: true, changed: false, ...extra,
});
export const sources = [source(1), source(2), source(3)];
export const sentences: AiSentenceView[] = [
  { text: "Připojíte se klientem `FortiClient` a firemním účtem.", sources: [1] },
  { text: "Notebook musí mít **platný certifikát** zařízení.", sources: [2] },
  { text: "Poprvé požádejte o certifikát den předem.", sources: [2, 3] },
];
export const provenance: AiProvenanceView = { location: "company", provider: "LM Studio", model: "gemma-4-26b" };

/** Tooltips need their provider, which the application root supplies. */
export const withProviders = (component: Component, props: Record<string, unknown> = {}, slots: Record<string, () => unknown> = {}) =>
  defineComponent({ render: () => h(TooltipProvider, null, { default: () => h(component, { ...props }, slots as never) }) });
