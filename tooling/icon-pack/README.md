# Icon pack

The icons behind "Další ikony": every icon of one pinned Lucide release that Atlas does not draw already. A developer
runs the generator; builds, CI and the running application read only the committed files.

| File | Content |
|---|---|
| `source.json` | the pin: repository, version (the tag) and its commit |
| `labels.cs.json` | Czech label and search words of every pack icon; corrected by hand |
| `src/Atlas.Web/src/assets/icon-pack/markup.json` | generated: name → inner SVG markup |
| `src/Atlas.Web/src/assets/icon-pack/catalog.json` | generated: categories, the category of each core icon, label, category and search words of each pack icon |
| `src/Atlas.Web/src/assets/icon-pack/meta.json` | generated: Lucide version and the number of pack icons |
| `src/Atlas.Server/Platform/Validation/icon-pack-names.txt` | generated: the names the server accepts |

The rules live in `lib.mjs` and are pinned by `node --test tooling/icon-pack/lib.test.mjs`. The web client inserts the
markup as HTML, so `innerMarkup` rebuilds every drawing from an allow-list of seven elements and their geometry
attributes and stops on anything else. Do not loosen it to let an icon through.

## Regenerate

Needs git and `pnpm --dir src/Atlas.Web install --frozen-lockfile` (the core registry is read from the installed editor
package).

```
node tooling/icon-pack/generate.mjs
```

Run it after a change of `labels.cs.json`, of the offered icons in `components/settings/appearanceIcons.ts`, of `atlasIconNames.ts` or of the
editor package, and commit the four generated files. A second run changes nothing. The first run clones the pinned tag
into `.cache/` (not committed) and every run checks that the checkout is at the pinned commit and unchanged; delete
`.cache/` to clone again. While `labels.cs.json` has a problem the generator lists the problems and writes nothing.

`node tooling/icon-pack/generate.mjs --sample` prints every 30th icon with its label and words, for a read against the
drawings.

## Update Lucide

1. Put the new version and the commit of its tag into `source.json`.
2. `generate.mjs --todo` writes the icons without a label to `.cache/todo-NN.json`, 150 in a file, with Lucide's tags
   and categories. A category Lucide added stops the generator until it has a row in `FOLD` in `lib.mjs`.
3. Translate each file into `.cache/labels-part-NN.json` in the form of `labels.cs.json`:
   `"<name>": {"label": "…", "words": ["…", "…"]}`.
4. `generate.mjs --merge` folds the parts into `labels.cs.json`, lists the problems and ends with `N problems`. A folded
   part is renamed to `labels-part-NN.merged.json`, so corrections made in `labels.cs.json` afterwards survive the next
   `--merge`; a new part replaces the entries it names. Repeat until `0 problems`.
5. `generate.mjs`, then update the Lucide entry in `NOTICES.md` and commit.

## Without GitHub

`generate.mjs --source <dir>` reads a checkout of the Lucide repository made elsewhere. The same two checks apply: it
must be at the pinned commit, with `icons` and `categories` unchanged.

## Names are never removed

A name is stored on spaces and pages. When Lucide drops or renames an icon, the generator keeps its drawing and catalog
entry from the committed files and reports it as kept from the previous pack; its entry in `labels.cs.json` stays. A
name leaves the pack only when it becomes valid elsewhere: as an offered icon of `components/settings/appearanceIcons.ts` or as a legacy name.
