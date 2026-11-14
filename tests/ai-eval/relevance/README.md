# Relevance hledání a odpovědí nad skutečnou wiki

> Spouštěč (`<eval-runner>`, interní prototyp pro načtení zálohy, hledání a chat přes API) není součástí veřejného repozitáře. Sady, jejich načítání (`tests/ai-eval/*EvalSet.cs`) a pravidla níže platí; příkazy se spouštěčem popisují postup správce a bez něj je nelze spustit.

Sada nad skutečnou wiki měří, jestli hledání a agent najdou správnou stránku (a sekci) ve **skutečném obsahu produkční wiki**, ne v 15stránkovém syntetickém seedu sad `answer/` a `agent/`. Slouží k ladění a posouzení změn hledání (vektorová a lexikální váha, reranker, české tvary slov, rozklad identifikátorů, přepis dotazu) čísly místo dojmů.

- **Sada ani podrobné výsledky nejsou v repozitáři.** Dotazy, očekávané stránky a sekce jmenují skutečné stránky firemní wiki, a repozitář je veřejný. Sada (`wiki.json`) a podrobné výsledky zůstávají mimo Git; sem patří jen kód, tento popis a anonymizovaný souhrn čísel. Zdrojem obsahu je Git záloha Atlasu (jen ke čtení, commit v `provenance` sady). Cesty stránek jsou cesty v záloze (`prostor/stránka/podstránka`, bez `.md`).
- Stav `wiki-derived`, sada `tuning`: dotazy napsala vývojářská session podle názvů, nadpisů a textu zálohy a každou očekávanou stránku ověřila v načteném korpusu; 2 dotazy jsou z hlášení provozovatele, 2 z logu nenalezených hledání (`SearchQueryLog`). Nejde o pozorovaný provoz zaměstnanců, ladit na ní se smí; held-out sada zůstává věcí provozovatele (`answer/test.json`, `agent/test.json`).

## Formát případu

```json
{ "id": "q-…", "category": "question-en", "ask": true, "query": "<česká otázka nad anglickou stránkou>",
  "expected": ["prostor/stranka"], "section": "<nadpis sekce>" }
```

- `category`: `title` (název, i bez diakritiky), `identifier` (operationId, cesta API, název nástroje, chybový kód), `inflection` (české tvary: smlouva/smlouvy/smlouvou), `question-en` (česká otázka nad anglickou dokumentací), `paraphrase` (jinými slovy), `unanswerable` (wiki odpověď nemá; `expected` prázdné).
- `expected`: stránky, z nichž kterákoli je správná odpověď. `section`: nadpis, kam má výsledek vést, kde na tom záleží (u stránky s OpenAPI nadpis úryvku operace, např. `GET /v1/…`); jinak `null`.
- `ask`: případ se položí i jako tah agenta nad celou wiki.
- Tvar sady (jedinečná id a dotazy, kategorie, cesty, nezodpověditelné jen jako tah agenta) kontroluje `RelevanceEvalSets.Load` při každém načtení; repozitářový test na ni být nemůže, sada v repozitáři není.

## Metriky

| Metrika | Co měří |
|---|---|
| Hit@1, Hit@5 | podíl dotazů, kde je očekávaná stránka první / mezi prvními pěti |
| MRR | průměr 1/pořadí první očekávané stránky v prvních 20 (0 = nenalezeno) |
| Sekce | u dotazů se `section`: první očekávaný zásah v top 5 vede na tu sekci (nadpis lexikální shody nebo nadpis úryvku) |
| Agent: ve zdrojích / citovaná | očekávaná stránka je mezi zdroji odpovědi / mezi citovanými zdroji |
| Agent: p50/p95 | čas celého tahu (příprava + dokončení přes NDJSON) |

Hledání se měří dvakrát: **lexikálně** (`atlas_search_published_filtered`, co hledání ukáže bez AI) a **hybridně** (`atlas_search_hybrid_published` s vektorem dotazu z téhož bge-m3 jako index). Obojí jsou produkční SQL funkce s produkčními argumenty (`SearchQueryParameters`, `SearchText` jsou do prototypu linkované ze serveru) a nastavením transakce jako `SearchSql.PrepareAsync`.

**Kalibrace** přepočítá fúzi `atlas_search_hybrid` v C# nad stejnými kandidáty (lexikální pořadí a skóre z `atlas_search_hybrid_candidates`, 400 nejbližších úryvků přesně) pro mřížku vah. Runner nejdřív ověří, že C# s dnešními konstantami dá u každého dotazu stejné pořadí očekávané stránky jako SQL; počet rozdílů vypíše (rozdíly v konci čistě významových zásahů jsou HNSW proti přesnému pořadí). Polovinové MRR (sudé/liché případy) ukazuje, jestli zisk nestojí na pár dotazech. Konstanty v migracích se kalibrací nemění.

## Spuštění (jen ručně, nikdy ve skriptech `tests/*.ps1` ani v CI)

1. Prázdná databáze s pgvectorem (podman) a lokální Atlas z větve, kterou měříte (`dotnet exec Atlas.Server.dll`, `ConnectionStrings__atlas-db`, port mimo vyloučené rozsahy Windows, např. 5161). LM Studio s `text-embedding-bge-m3` na `http://127.0.0.1:1234/v1`.
2. Načtení zálohy přes API instance (setup, prostory, stránky z editorového JSON rodiče napřed, publikace, AI index bge-m3 a aktivace). Obrázky a odkazy na přílohy se vynechají (záloha nemá soubory příloh), opakované značky se sloučí; stránky, které server odmítne, se vypíší a přeskočí. Heslo správce instance se vygeneruje do `--password-file` mimo Git.

   ```powershell
   <eval-runner> -- load-backup --url http://localhost:5161 --source <záloha-wiki> --bootstrap <server>/.data/security/bootstrap-token.txt --password-file <scratch>/eval-admin.txt
   ```

   Mapa cest zálohy na nová id (`page-map.json`) vzniká při načtení; pro víc instancí dejte každé vlastní (`--map` u `load-backup`, `relevance` i `relevance-agent`).
3. Hledání (jen čtení databáze):

   ```powershell
   $env:ATLAS_RELEVANCE_SET = "<wiki.json>"   # nebo --cases
   $env:ATLAS_EVAL_DB = "Host=127.0.0.1;Port=<port>;Username=…;Password=…;Database=…"
   <eval-runner> -- relevance
   ```

4. Agent nad celou wiki (Gateway). Klíč jen v souboru mimo všechny repozitáře (`--key-file`), nikdy na příkazové řádce, v logu ani v Gitu. Latence se uvádí ze vzdálené Gateway; běh nad lokální Gateway se v reportu označí.

   ```powershell
   <eval-runner> -- gateway-chat --gateway-url <adresa Gateway>/v1 --password-file <scratch>/eval-admin.txt
   <eval-runner> -- relevance-agent --password-file <scratch>/eval-admin.txt
   ```

Reporty (JSON, s dotazy a názvy stránek) jdou do ignorovaného `artifacts/ai-prototype/`. Anonymizovaný souhrn (čísla po kategoriích, kalibrace, doporučení; bez dotazů a názvů stránek) patří do popisu PR změny hledání.
