# Third-Party Notices

`@atlas/markdown` builds on top of the following open-source projects.
Each dependency is distributed separately under its respective license. This
package retains this attribution alongside its own source and distribution.

## Tiptap (MIT)

> Copyright (c) 2024–present, Tiptap Inc.
> https://github.com/ueberdosis/tiptap

Used core & extension packages (all MIT, peer range `^3.27.4`; lockfile resolves
3.31.0 as of 2026-09-02):

* `@tiptap/core`
* `@tiptap/starter-kit`
* `@tiptap/vue-3`
* `@tiptap/pm`
* `@tiptap/extension-bubble-menu`
* `@tiptap/extension-character-count`
* `@tiptap/extension-drag-handle`
* `@tiptap/extension-drag-handle-vue-3`
* `@tiptap/extension-focus`
* `@tiptap/extension-highlight`
* `@tiptap/extension-image`
* `@tiptap/extension-link`
* `@tiptap/extension-node-range`
* `@tiptap/extension-placeholder`
* `@tiptap/extension-table`
* `@tiptap/extension-table-cell`
* `@tiptap/extension-table-header`
* `@tiptap/extension-table-row`
* `@tiptap/extension-task-item`
* `@tiptap/extension-task-list`
* `@tiptap/extension-text-align`
* `@tiptap/extension-typography`
* `@tiptap/extension-underline`
* `@tiptap/suggestion`

Optional integrations added on 2026-09-05 use the same locked Tiptap release,
3.31.0 (optional peer range `^3.31.0`):

* `@tiptap/extension-find-and-replace`
* `@tiptap/extension-invisible-characters`
* `@tiptap/extension-code-block-lowlight`
* `@tiptap/extension-emoji`
* `@tiptap/extension-unique-id`
* `@tiptap/extension-text-style` (peer of InvisibleCharacters)

### Optional extension dependencies

* Lowlight 3.3.0 — MIT, Copyright Titus Wormer.
* Highlight.js 11.11.2 / 11.12.0 — BSD-3-Clause, Copyright (c) 2006 Ivan Sagalaev.
* RE2JS 2.8.6 — MIT, Copyright (c) 2023 Oleksii Vasyliev.
* emojibase / emojibase-data 17.0.0 — MIT, Copyright (c) 2017–2019 Miles Johnson.
* emoji-regex 10.6.0 — MIT, Copyright Mathias Bynens.
* is-emoji-supported 0.0.5 — MIT declared in its package manifest,
  Vincent Thibault. No separate license file is shipped in that package.
* uuid 14.0.2 — MIT, Copyright (c) 2010–2020 Robert Kieffer and contributors.
* dequal 2.0.3, devlop 1.1.0, @types/hast 3.0.5 and @types/unist 3.0.3 — MIT;
  retain the license files included in their distributions.

The emoji integration uses Unicode text, not a redistributed emoji image set.
It removes fallback image URLs and does not use the GitHub image catalog or a CDN.
The device's own font supplies emoji glyphs. The full additional dependency
inventory, versions and local license evidence are recorded in
`docs/tiptap-extension-license-inventory-2026-09-05.json` in the source repository.

Lowlight and its assertion helpers are tree-shaken into the optional
highlighting chunk. MIT copyright notices: Lowlight — Titus Wormer;
devlop — Copyright (c) 2023 Titus Wormer <tituswormer@gmail.com>;
dequal — Copyright (c) Luke Edwards <luke.edwards05@gmail.com> (lukeed.com).
The MIT permission and warranty terms apply to these components:

> (The MIT License)
>
> Copyright (c) Titus Wormer <tituswormer@gmail.com>
>
> Permission is hereby granted, free of charge, to any person obtaining
> a copy of this software and associated documentation files (the
> 'Software'), to deal in the Software without restriction, including
> without limitation the rights to use, copy, modify, merge, publish,
> distribute, sublicense, and/or sell copies of the Software, and to
> permit persons to whom the Software is furnished to do so, subject to
> the following conditions:
>
> The above copyright notice and this permission notice shall be
> included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED 'AS IS', WITHOUT WARRANTY OF ANY KIND,
> EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
> MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
> IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
> CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
> TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
> SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Note: `@tiptap/extension-drag-handle` was historically distributed as
"Tiptap Pro" (commercial). As of Tiptap 3.x it is open-source MIT.

## ProseMirror (MIT)

> Copyright (C) 2015–present by Marijn Haverbeke and others.
> https://prosemirror.net/

Used transitively via Tiptap (`prosemirror-state`, `prosemirror-view`,
`prosemirror-model`, `prosemirror-tables`, `prosemirror-dropcursor`,
`prosemirror-gapcursor`, `prosemirror-history`, `prosemirror-keymap`,
`prosemirror-schema-list`, `prosemirror-transform`).

## Floating UI (MIT)

> Copyright (c) 2021-present Floating UI contributors
> https://floating-ui.com/

* `@floating-ui/dom@^1.6.0`

## tiptap-markdown (MIT)

> https://github.com/aguingand/tiptap-markdown

Markdown serialisation / parsing layer.

## Vue 3 (MIT)

> Copyright (c) 2018-present, Yuxi (Evan) You and Vue contributors
> https://vuejs.org/

Peer dependency.

## Apache ECharts (Apache-2.0)

Optional chart-rendering peer dependency: `echarts@^6.1.0`. It is loaded only
by the charts module; the editor core does not require it.

Upstream notice from the installed ECharts 6.1.0 distribution:

> Apache ECharts
> Copyright 2017-2026 The Apache Software Foundation
>
> This product includes software developed at
> The Apache Software Foundation (https://www.apache.org/).

Sources: [Apache ECharts LICENSE](https://github.com/apache/echarts/blob/master/LICENSE)
and [NOTICE](https://github.com/apache/echarts/blob/master/NOTICE).
The upstream distribution also includes its subcomponent license notices;
preserve those files when redistributing bundled ECharts code.

## Lucide icons (ISC)

`src/ui/iconMarkup.ts` embeds icon *geometry* copied from Lucide 1.41.0 (via the
installed `@lucide/vue` distribution) by `tooling/import-lucide-icons.mjs`. No
Lucide code or package is a dependency of `@atlas/markdown` — only the path
data is redistributed, unmodified apart from dropping Lucide's React `key`
props and the wrapping `<svg>` element. The Atlas settings entries added on 2026-09-27
(`shield` … `map-pin`, see the comment in the registry) were copied the same way
from `@lucide/vue` 1.23.0; `filter` is Lucide's `funnel` drawing.

> ISC License
>
> Copyright (c) 2026 Lucide Icons and Contributors
>
> Permission to use, copy, modify, and/or distribute this software for any
> purpose with or without fee is hereby granted, provided that the above
> copyright notice and this permission notice appear in all copies.
>
> THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
> WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
> MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
> ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
> WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
> ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
> OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

Source: [Lucide LICENSE](https://github.com/lucide-icons/lucide/blob/main/LICENSE).
Lucide derives part of its set from Feather Icons (MIT, Copyright (c) 2013-2017
Cole Bemis); that notice is preserved upstream in the same file.

---

Dependency packages retain their own license files when installed by the
consumer. This `NOTICES.md` is shipped with `@atlas/markdown`; consumers
redistributing a build should preserve it and the license notices of the
dependencies they bundle.
