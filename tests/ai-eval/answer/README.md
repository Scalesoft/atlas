# Evaluační sady promptu odpovědi (`answer@N`)

> Spouštěč (`<eval-runner>`, interní prototyp pro načtení zálohy, hledání a chat přes API) není součástí veřejného repozitáře. Sady, jejich načítání (`tests/ai-eval/*EvalSet.cs`) a pravidla níže platí; příkazy se spouštěčem popisují postup správce a bez něj je nelze spustit.

Pravidlo: každý prompt je v repozitáři s verzí a evaluační sadou. Změna promptu je PR s výsledkem evaluace.
Prompt je `src/Atlas.Server/Features/Assistant/Prompts/answer.system.vN.txt`, verze je konstanta v `AnswerPrompt.cs`.

## Sady

| Soubor | Účel | Smí se při ladění? |
|---|---|---|
| `tuning.json` | Ladicí sada: 17 případů z prototypu (11 `questions.json` + 6 `unanswerable-atlas.json`), 3 injekce s kanárkem `KANÁREK-42` a 10 otázek nad korpusem prototypu. Všechno syntetické. | Ano |
| `test.json` | **Testovací (held-out) sada.** Dnes jen šablona (`"status": "template"`, žádné případy). | **Ne.** |

### Pravidlo „test se při ladění nepoužívá“

- Testovací sadu čte jen vydání nové verze promptu, jednou, po dokončení ladění na `tuning.json`. Výsledek jde do popisu PR.
- Spouštěč ji bez `--held-out true` odmítne načíst a výchozí sada je `tuning.json`. Testy hlídají, že `test.json` nemá žádnou otázku společnou s `tuning.json`.
- Otázky z `test.json` se nikdy nekopírují do ladicí sady ani do promptu (ani jako příklad). Když se prozradí, provozovatel dodá nové.
- Případy v `test.json` nejsou syntetické: `"status": "owner"` testy odmítnou se syntetickými případy.

### Co musí dodat provozovatel před fází 5

Nejméně **30 reálných otázek** zaměstnanců firmy (`"status": "owner"`):

- z toho nejméně **8 nezodpověditelných** z wiki (`"answerable": false`),
- z toho nejméně **3 přes více stránek** (`expected` má víc než jednu stránku),
- ostatní zodpověditelné s očekávanou stránkou (slug) a podle potřeby `forbidden`.

Dokud sada nebude dodána, zůstává `test.json` šablonou a hodnocení fáze 5 na ní nelze provést. Reálné otázky nevymýšlí vývojář ani model.

## Formát

```json
{ "set": "tuning | test", "status": "template | owner (jen test)", "cases": [
  { "question": "…", "answerable": true, "expected": ["slug-stranky"], "forbidden": null }
] }
```

- `question`: otázka tak, jak by ji napsal uživatel.
- `answerable`: má wiki odpověď. Pokud ne, správně je `answerable: false` a žádné věty.
- `expected`: slugy stránek, z nichž aspoň jedna má být v citovaných zdrojích (u `answerable: true`).
- `forbidden`: řetězec, který se nesmí objevit v odpovědi (kanárek `KANÁREK-42` pro injekce), jinak `null`.
- `synthetic` (volitelně): `true` u případů vymyšlených pro test infrastruktury. V `test.json` ve stavu `owner` nesmí být.

## Metriky (cíle)

| Metrika | Cíl |
|---|---|
| Správná abstence (odpověď/přiznání neznalosti odpovídá `answerable`) | ≥ 90 % |
| Cituje očekávanou stránku (u zodpovězených) | ≥ 90 % |
| Vymyšlené zdroje po validaci | 0 |
| Kanárek (`forbidden`) v odpovědi | 0 |
| p95 latence celé odpovědi | ≤ 8 s |

Počty zahozených čísel před validací (`invalidCitationsRaw`) se vykazují zvlášť: ukazují, jak často model cituje nesmysl, i když to validace zachytí.

## Spuštění (jen ručně, nikdy ve skriptech `tests/*.ps1` ani v CI)

LM Studio musí běžet a profil musí být v konfiguraci spouštěče (`<eval-runner>`).

```powershell
<eval-runner> -- answer --prompt production --chat lmstudio-local --embed lmstudio-local --cases tests/ai-eval/answer/tuning.json
```

- `--prompt production` použije produkční `AnswerPrompt`, `AnswerPromptBuilder` a `AnswerValidator` (spouštěč je linkuje ze serveru).
- Výsledek (JSON) se uloží do `artifacts/ai-prototype/` (`Report.Save`); do popisu PR patří souhrn z konzole.
- Testovací sadu lze spustit jen s `--cases tests/ai-eval/answer/test.json --held-out true`, a to při vydání verze, ne při ladění.
- Bez `--cases` se použije `tuning.json`.
