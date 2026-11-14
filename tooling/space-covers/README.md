# Built-in space covers

Generic thematic images for space cards in Knihovna, so a new wiki looks lively before anyone uploads a cover.
The SVGs and `catalog.json` in `src/Atlas.Web/src/assets/space-covers/` are generated from the Python drawings
here; edit a drawing, then regenerate:

```bash
python -I tooling/space-covers/generate.py
```

The output is deterministic. Commit the regenerated files together with the drawing change.

## Style

The images are grey line art and get their colour from the card: `SpaceCard.vue` turns them grey, multiplies them
onto the light colour of the space and lightens their dark parts to its darker colour (the same duotone as
uploaded covers), and inverts them first in dark mode. So every cover works with every space colour and theme.

- 640 × 220, white paper, strokes `#3a3a3a` 2 px, fills from `cover_lib.py` (`#f2f2f2` … `#5a5a5a`), dashed `#8a8a8a`.
- IT motifs get the 32 px `#dedede` grid, the other groups plain paper.
- Main subject centre right; keep the bottom left light (the space icon sits there) and the top right quiet
  (the pin toggle).
- Everything at or darker than `#9a9a9a` ends up in the same tint, so a large dark area reads as a solid block:
  keep big surfaces `#f2f2f2` or white and use dark fills for small accents.
- No text, no raster data, no scripts or styles in the SVG.

## Catalog

`catalog.json` lists every cover with its Czech name, group (`it`, `business`, `industry`, `general`), tags, a sample
hue for previews, and the matching data the server uses to pick a cover for a space: `stems` and `icons`. A person
searches the names, the tags and the stems in the gallery (`findSpaceCovers`); `stems` and `icons` are what the server
compares with a space's name, description and icon (`CoverCatalog.Match`).

Matching data is hand-tuned in `matching.py`; its header explains the rules (word prefixes, whole words for three
characters or fewer, icons, the order of the three signals). After a change regenerate and run the matcher's table:

    python -I tooling/space-covers/generate.py
    dotnet test tests/Atlas.Server.Tests -c Release --filter "FullyQualifiedName~CoverCatalog"

The server embeds this one `catalog.json` (`Atlas.Server.csproj`), so the web and the server cannot disagree.
