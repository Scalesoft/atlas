/**
 * Named Vue components kept for callers that grew up with them (`export * as icons`).
 *
 * Each one is a label on a registry entry — no geometry lives here any more. Prefer
 * `<MarkdownIcon name="…">` from `@atlas/markdown/icons` in new code.
 */
import { defineComponent, h } from 'vue'
import { MarkdownIcon } from '../icons'
import type { MarkdownIconName } from './iconMarkup'

const icon = (name: MarkdownIconName) => defineComponent({
  name: `MarkdownIcon-${name}`,
  props: { size: { type: Number, default: 16 } },
  setup: props => () => h(MarkdownIcon, { name, size: props.size }),
})

export const BoldIcon = icon('bold')
export const ItalicIcon = icon('italic')
export const UnderlineIcon = icon('underline')
export const StrikeIcon = icon('strikethrough')
export const HighlightIcon = icon('highlighter')
export const CodeIcon = icon('code')
export const CodeBlockIcon = icon('square-code')
export const LinkIcon = icon('link')

export const BulletListIcon = icon('list')
export const OrderedListIcon = icon('list-ordered')
export const TaskListIcon = icon('list-checks')
export const QuoteIcon = icon('quote')
export const HorizontalRuleIcon = icon('minus')

export const ParagraphIcon = icon('pilcrow')
export const PilcrowIcon = icon('pilcrow')
export const Heading1Icon = icon('heading-1')
export const Heading2Icon = icon('heading-2')
export const Heading3Icon = icon('heading-3')
export const Heading4Icon = icon('heading-4')
export const Heading5Icon = icon('heading-5')
export const Heading6Icon = icon('heading-6')

export const AlignLeftIcon = icon('align-left')
export const AlignCenterIcon = icon('align-center')
export const AlignRightIcon = icon('align-right')

export const OutlineIcon = icon('outline')
export const CollapseOutlineIcon = icon('panel-right-close')
export const PinIcon = icon('pin')
export const SearchIcon = icon('search')

export const ChevronDownIcon = icon('chevron-down')
export const PlusIcon = icon('plus')
export const TrashIcon = icon('trash-2')
export const GripVerticalIcon = icon('grip-vertical')
export const CloseIcon = icon('x')
export const CheckIcon = icon('check')
export const UndoIcon = icon('undo-2')
export const AlertTriangleIcon = icon('triangle-alert')
export const ImageIcon = icon('image')
export const FullscreenIcon = icon('maximize-2')
export const FullscreenExitIcon = icon('minimize-2')

export const TableIcon = icon('table')
export const TableHeaderIcon = icon('table-header-row')
export const TableMergeIcon = icon('table-merge')
export const RowAddBeforeIcon = icon('row-add-before')
export const RowAddAfterIcon = icon('row-add-after')
export const RowDeleteIcon = icon('row-delete')
export const ColumnAddBeforeIcon = icon('column-add-before')
export const ColumnAddAfterIcon = icon('column-add-after')
export const ColumnDeleteIcon = icon('column-delete')
