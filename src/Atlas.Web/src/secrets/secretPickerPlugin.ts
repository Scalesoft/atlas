import { defineMarkdownCommand, type MarkdownPlugin } from "@atlas/markdown/core";
import t from "../i18n/secrets";

/**
 * The entry point of the host picker (SecretPicker) in the editor's own menus: the slash menu, the block inserter and the
 * page's plugin actions. It adds no document node (no schema), so the collaboration schema is the secrets plugin's alone.
 */
export function createSecretPickerPlugin(open: () => void): MarkdownPlugin {
  const pick = defineMarkdownCommand<void>({
    id: "atlas.secrets.pick",
    apply: () => ({ editor, dispatch }) => {
      if (!editor.isEditable) return false;
      // Open after the command settles: the slash menu closes first and gives focus back.
      if (dispatch) queueMicrotask(open);
      return true;
    },
  });
  const action = {
    id: "atlas.secrets.pick",
    label: t.action.label,
    description: t.action.description,
    icon: "svg:key-round",
    keywords: t.action.keywords,
    command: pick(),
  };
  return {
    id: "atlas-secret-picker",
    apiVersion: 1,
    create: () => ({ commands: [pick], actions: { slash: [action], toolbar: [{ ...action, variants: ["document"] }] } }),
  };
}
