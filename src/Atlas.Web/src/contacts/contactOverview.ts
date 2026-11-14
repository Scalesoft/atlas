import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import t from "../i18n/contacts";
import { h, type FunctionalComponent } from "vue";
import PageContactsCard from "../components/contacts/PageContactsCard.vue";
import { registerOverviewKind, type OverviewCardAction } from "../overview/overviewKinds";
import { boardContactNodes, carriesContacts, contactLevels, defaultContactLevel, pageContacts } from "./pageContacts";
import "./contactOverview.css";

/** The card over collected people (a read page, a board): the same card as in the editor, with the level of `scope`. */
const ContactsReadCard: FunctionalComponent<{ items: readonly MarkdownContact[]; scope: string; actions?: readonly OverviewCardAction[] }> = props =>
  h(PageContactsCard, { contacts: props.items, pageId: props.scope, actions: props.actions });

/**
 * The overview of kind `contacts` (plan 3.4 R6, overviewKinds.ts): the people of a page or a board, their cards' level, the vCard and
 * the e-mails. On a board also the live contact cards and the people its texts mention (`boardContactNodes`). A change of level plays
 * the shared motion over the contact cards (overviewMotion.ts).
 */
registerOverviewKind({
  kind: "contacts", get label() { return t.overview.label; }, collect: pageContacts, boardNodes: boardContactNodes, boardReads: carriesContacts, card: PageContactsCard, readCard: ContactsReadCard,
  levels: contactLevels, defaultLevel: defaultContactLevel, itemSelector: ".md-contact-list .md-contact-card",
});
