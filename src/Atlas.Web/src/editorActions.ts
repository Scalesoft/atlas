/** One editor plugin toolbar action, handed to the shell so it can live in the page menu. */
export interface EditorPluginAction {
  id: string;
  label: string;
  description?: string;
  /** Registry icon name (`paperclip`, `search`, …) or null for text-only actions. */
  icon: string | null;
  /**
   * The plugin's own group of the action. Atlas's plugins name it as a label of the page menu („Nasazení“); the editor's groups
   * are identifiers (`content`, `integrations`) of what inserts something.
   */
  group?: string;
  enabled: boolean;
  run: () => boolean;
}
