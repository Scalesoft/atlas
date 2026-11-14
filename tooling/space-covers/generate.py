"""Regenerate the built-in space covers and their catalog.

    python -I tooling/space-covers/generate.py

Writes one SVG per motif and catalog.json to src/Atlas.Web/src/assets/space-covers/. The drawings live in the
batch*.py modules beside this script; cover_lib.py holds the shared palette and helpers. Output is deterministic,
so a regenerated tree must show no diff unless a drawing changed.
"""
import importlib
import json
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.dont_write_bytecode = True
sys.path.insert(0, str(HERE))
from cover_lib import svg  # noqa: E402
from matching import MATCH  # noqa: E402

STEM = re.compile(r'[a-z0-9]{2,}')
DEFAULT_ICON = 'book-open'


def matching(cid):
    """The stems and icons of one cover, checked: a malformed entry would silently never match."""
    if cid not in MATCH:
        raise SystemExit(f'matching.py has no entry for {cid}')
    stems, icons = (part.split() for part in MATCH[cid])
    if not stems:
        raise SystemExit(f'{cid} needs at least one stem')
    for stem in stems:
        if not STEM.fullmatch(stem):
            raise SystemExit(f'{cid}: stem {stem!r} must be lower-case ASCII letters and digits, two or more')
        covered = [other for other in stems if other != stem and len(stem) > 3 and other.startswith(stem)]
        if covered:
            raise SystemExit(f'{cid}: stem {stem!r} already matches {", ".join(covered)}; remove the longer ones')
    if len(set(stems)) != len(stems) or len(set(icons)) != len(icons):
        raise SystemExit(f'{cid}: a stem or an icon is listed twice')
    if DEFAULT_ICON in icons:
        raise SystemExit(f'{cid}: {DEFAULT_ICON} is the default icon and implies nothing')
    return stems, icons

BATCHES = ['batch1', 'batch2a', 'batch2b', 'batch3a', 'batch3b']
GROUPS = {'IT': 'it', 'Firma': 'business', 'Obor': 'industry', 'Obecné': 'general'}
OUT = HERE.parent.parent / 'src' / 'Atlas.Web' / 'src' / 'assets' / 'space-covers'


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    catalog = []
    seen = set()
    for name in BATCHES:
        mod = importlib.import_module(name)
        for cid, (grid, body) in mod.C.items():
            if cid in seen:
                raise SystemExit(f'duplicate cover id {cid} in {name}')
            seen.add(cid)
            title, group, tags, hue = mod.M[cid]
            (OUT / f'{cid}.svg').write_text(svg(body.strip(), grid), encoding='utf-8', newline='\n')
            stems, icons = matching(cid)
            catalog.append({'id': cid, 'name': title, 'group': GROUPS[group], 'tags': [t.strip() for t in tags.split(',')], 'hue': hue,
                            'stems': stems, 'icons': icons})
    stale = sorted(p.name for p in OUT.glob('*.svg') if p.stem not in seen)
    if stale:
        raise SystemExit(f'remove covers no batch draws any more: {", ".join(stale)}')
    unknown = sorted(set(MATCH) - seen)
    if unknown:
        raise SystemExit(f'matching.py names covers no batch draws: {", ".join(unknown)}')
    catalog.sort(key=lambda c: (list(GROUPS.values()).index(c['group']), c['id']))
    (OUT / 'catalog.json').write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    print(f'{len(catalog)} covers -> {OUT}')


if __name__ == '__main__':
    main()
