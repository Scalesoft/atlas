import { defineMarkdownCommand, type MarkdownPlugin } from "@atlas/markdown/core";
import t from "../i18n/contacts";
import { normalizeContact, type ContactsPluginOptions, type MarkdownContact } from "@atlas/markdown/plugins/contacts";
import type { Editor } from "@tiptap/core";
import { NodeSelection, TextSelection, type Transaction } from "@tiptap/pm/state";
import ContactPicker from "../components/contacts/ContactPicker.vue";
import { useToast } from "../composables/useToast";
import { captureTypeAhead, type TypeAhead } from "../lib/typeAhead";
import { openVuePopover } from "../projector/openVuePopover";
import { contactsIn, linkContact, repeatedContact, type ContactAsk } from "./contactLinking";
import type { PersonHit } from "./contactSearch";
import { learnAlias } from "./contactSources";

export type PeopleSearch = ((text: string, signal: AbortSignal) => Promise<PersonHit[]>) & { cached?: (text: string) => PersonHit[] };

const node = (inline: boolean, contact: MarkdownContact) => ({ type: inline ? "contactInline" : "contactCard", attrs: { data: { version: 1, contacts: [normalizeContact(contact)] } } });

/**
 * ContactPicker at the caret; the person picked, linked to the directory contact (contactLinking.ts), goes where the caret was, followed
 * through the edits of others meanwhile. A contact the page has already is said, never refused.
 */
export function openContactPicker(editor: Editor, inline: boolean, people: PeopleSearch, typeAhead?: TypeAhead) {
  if (editor.isDestroyed || !editor.isEditable) return;
  let at = editor.state.selection.from;
  const follow = ({ transaction }: { transaction: Transaction }) => { at = transaction.mapping.map(at); };
  editor.on("transaction", follow);
  const anchor = () => {
    try { const box = editor.view.coordsAtPos(at); return new DOMRect(box.left, box.top, 0, box.bottom - box.top); }
    catch { return editor.view.dom.getBoundingClientRect(); }
  };
  const close = openVuePopover({ editor, anchor: editor.view.dom, positionAnchor: { getBoundingClientRect: anchor }, title: inline ? t.slash.inText : t.slash.contact,
    variant: "panel", component: ContactPicker, closed: () => editor.off("transaction", follow),
    props: { search: people, link: linkContact, learn: learnAlias, typeAhead, normalize: normalizeContact, pick: (contact: MarkdownContact) => {
      close();
      if (editor.isDestroyed) return;
      if (contact.objectId && contactsIn(editor.state.doc, contact.objectId)) useToast().warning(repeatedContact());
      editor.chain().focus().setTextSelection(at).insertContent(node(inline, contact)).command(({ tr }) => {
        // A card left selected lends the next keys to its toolbar and the app's letter keys (PR-1): the caret goes on the line after it.
        if (!(tr.selection instanceof NodeSelection)) return true;
        const after = tr.selection.to;
        if (!tr.doc.resolve(after).nodeAfter?.isTextblock) tr.insert(after, tr.doc.type.schema.nodes.paragraph!.create());
        tr.setSelection(TextSelection.create(tr.doc, after + 1));
        return true;
      }).run();
    } } });
}

/**
 * The contacts plugin's own picker (`ContactsPluginOptions.pick`, used by „Udělat kontakt“ over a selected name): the same ContactPicker
 * at `anchor` with the name as its first search, the first person preselected; the person, linked, or null when it closes. The plugin
 * puts the person where the name was. `link` is the gallery's seam; Atlas links through the directory.
 */
export function pickContact(people: PeopleSearch, link: (contact: MarkdownContact, ask: ContactAsk) => Promise<MarkdownContact | null> = linkContact,
  learn: typeof learnAlias = learnAlias): NonNullable<ContactsPluginOptions["pick"]> {
  return ({ editor, query, signal, anchor }) => new Promise(settle => {
    let picked: MarkdownContact | null = null;
    const close = openVuePopover({ editor, anchor: editor.view.dom, positionAnchor: { getBoundingClientRect: anchor }, title: t.slash.inText, variant: "panel",
      signal, component: ContactPicker, closed: () => settle(picked),
      props: { search: people, link, learn, typeAhead: { take: () => ({ text: query, enter: false }) }, normalize: normalizeContact, pick: (contact: MarkdownContact) => {
        if (contact.objectId && contactsIn(editor.state.doc, contact.objectId)) useToast().warning(repeatedContact());
        picked = contact;
        close();
      } } });
  });
}

/**
 * The editor's contacts plugin with its two inserts („Kontakt“, „Kontakt v textu“, in the slash menu and the page menu) opening the
 * people picker at the caret instead of a placeholder „Nový kontakt“. The plugin's nodes, commands and `@` stay its own; only the
 * actions' command changes, so the hook maps onto the editor package as an option later.
 */
export function withContactPicker(plugin: MarkdownPlugin, people: PeopleSearch): MarkdownPlugin {
  // `query`: the name typed after the command (`/kontakt jan kovarik`); `inline` unset: in the text when the line has text.
  const pick = defineMarkdownCommand<{ inline?: boolean; query?: string }>({ id: "atlas.contacts.pick", apply: ({ inline: asked, query = "" }) => ({ editor, commands, dispatch, tr }) => {
    const inline = asked ?? tr.selection.$from.parent.content.size > 0;
    // The menus ask whether the contact could go here; the placeholder is never inserted.
    if (!dispatch) return commands.insertContent(node(inline, { name: "?" }));
    // After the slash menu has closed, so its teardown does not take the picker with it.
    // What is typed on meanwhile is the search (`/kontakt` Enter `jan kovarik` Enter in one go), after the typed name.
    const ahead = captureTypeAhead(editor.view.dom.ownerDocument);
    const typed: TypeAhead = query ? { take: () => { const more = ahead.take(); return { text: query + more.text, enter: more.enter }; } } : ahead;
    setTimeout(() => openContactPicker(editor, inline, people, typed));
    return true;
  } });
  const inserts: Record<string, boolean> = { "contacts.insert": false, "contact-inline.insert": true };
  return {
    ...plugin,
    create(context) {
      const created = plugin.create(context);
      type Action = NonNullable<NonNullable<typeof created.actions>["slash"]>[number];
      // „Kontakt“ takes the name typed after it into the picker's search (`/kontakt jan`); its place follows the line.
      const swap = (action: Action): Action => (!(action.id in inserts) ? action : inserts[action.id]
        ? { ...action, command: pick({ inline: true }), parameter: undefined }
        : { ...action, command: pick({ inline: false }), parameter: { label: t.slash.contact, command: (text: string) => pick({ query: text }) } });
      return { ...created, commands: [...(created.commands ?? []), pick],
        actions: { ...created.actions, slash: created.actions?.slash?.map(swap), toolbar: created.actions?.toolbar?.map(swap) } };
    },
  };
}
