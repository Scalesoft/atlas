# Shared suggestions

The framework-neutral list, popup and renderer are exported from `@atlas/markdown/core`. Plugins should import that public entry point.

For synchronous results, configure upstream `Suggestion` with `items` and `render: createMarkdownSuggestionRenderer({ pluginKey, label, mapItem })`. Use `shouldShow: shouldShowMarkdownSuggestion` to exclude Yjs-origin transactions without loading collaboration into core. `mapItem` supplies a stable unique `id`, `title`, optional `description`, `icon` (Unicode or `svg:IconName`), `avatar` and `disabled` state.

For asynchronous host data, configure upstream `items: () => []` and pass `source: { search(query, { signal }), debounceMs, limit, minQueryLength }` to the shared renderer. The renderer owns loading, empty, error and retry presentation. Defaults are 200 ms, 20 results and one query character. Every new query immediately removes selectable old results. Both abort and a generation guard protect against late responses; retry works by clicking its row or pressing Enter while the error is shown. Use `closeMarkdownSuggestion(editor, pluginKey)` before replacing a data source or changing the triggering configuration.

The renderer keeps typing focus in the editor and owns its temporary ARIA relationship, popup, listeners, request cancellation and mutation observer. Arrow keys and Home/End select available options. Enter and Tab confirm outside IME composition (P8); Shift+Tab moves up like ArrowUp, and neither moves focus out of the editor. Escape ends the session and consumes the first Escape. Pointer dismissal never restores focus. Selections are bounded as independent undo operations.

Pass the editor and `onDismiss` when using `createSuggestionPopup` directly so it participates in the same exclusive panel registry as component popovers. A replacement panel ends the Suggestion session as well as removing the portal. Popups mount in `ownerDocument`, copy editor tokens, register fullscreen ownership and follow caret geometry. They are capped at 320 px and at available viewport height. Shared CSS is included by the editor stylesheet; core imports no Vue or CSS.
