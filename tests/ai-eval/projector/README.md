# Evaluace AI nad stránkami s požadavky (Projector v1)

> Spouštěč (`<eval-runner>`, interní prototyp pro načtení zálohy, hledání a chat přes API) není součástí veřejného repozitáře. Sady, jejich načítání (`tests/ai-eval/*EvalSet.cs`) a pravidla níže platí; příkazy se spouštěčem popisují postup správce a bez něj je nelze spustit.

Měří, jestli požadavky ve stránkách nasazení
najde hledání, čtou je odpovědi AI a správně je citují. Používá evaluační spouštěč (`<eval-runner>`): načtení přes zápisové API
serveru, AI index bge-m3 a profil chatu přes administraci, stejně jako sada `tests/ai-eval/relevance`. Spouští se **jen ručně**, nikdy v CI ani
v `tests/*.ps1`. Data jsou vymyšlená (vymyšlená nasazení), proto je sada v repozitáři.

| Soubor | Co je |
|---|---|
| `fixtures.mjs` | 12 stránek jako Git záloha pro `load-backup`: čtyři stránky nasazení s bloky požadavků a souhrnem, katalog (včetně modulu „Telekonference přes Azure (ACS)“ se závislostmi) a 7 obyčejných stránek (rušivé: obecný postup obnovy SSL, VPN, SMS brána, Keepass při nástupu, přehled strukturovaných požadavků). Varianta `with` (bloky jako navrženo) a `without` (stejné stránky bez jediného bloku a souhrnu = stav před funkcí). |
| `cases.json` | 14 případů: 13 českých otázek ve formátu relevanční sady + `facts` / `forbidden` / `expectAll` / `allowRefusal`: 10 nad nasazeními a katalogem (včetně „Je v nasazení něco hotové?“, kde se čeká „0 %“, ne vymyšlený stav) a 3 kontrolní (dvě z obyčejných stránek, jedna nezodpověditelná) a 1 zápisový scénář (viz níže). |
| `score.mjs`, `score.test.mjs` | Hodnocení odpovědi (fakta, zakázané texty, citace, abstence); `node --test tests/ai-eval/projector/score.test.mjs`. |
| `summarize.mjs` | Tabulka výsledků z reportů `agent-relevance.mjs` / `mcp-agent.mjs` (odpověď se přepočítá z uloženého textu podle aktuálních `cases.json`). |
| `chunk-retrieval.mjs` | Nejbližší úryvky indexu (vektor bge-m3) a zda je mezi nimi úryvek se všemi fakty. |
| `mcp-agent.mjs` | Malý smyčkový agent nad `/mcp` (OpenAI-kompatibilní chat): co vidí vnější asistent s `atlas_*` nástroji a co s `projector_*`. |

## Zápisový scénář (`write: true`)

Případ `dubovice-copy-telekonference`: „Založ mi projekt podle Dubovic a přidej tam navíc ještě telekonference.“ Agent má najít stránku „Dubovice — nasazení“,
přes `projector_get_catalog` (s `query`) najít modul telekonferencí a zavolat `projector_create_deployment` s `moduleKeys`. Hodnotí se volání nástrojů, ne zdroje:
`expectCalls` (názvy nástrojů, každý musí zaznít), `expectArgs` (`[{ "tool", "arg", "contains" }]`: některé volání nástroje má argument nebo jeho prvek, jehož text obsahuje `contains` bez ohledu na velikost písmen a diakritiku; moduleKey je kotva nadpisu modulu, proto „telekonference“)
a `facts` v odpovědi. V reportu má každý případ `toolCalls: [{ name, input }]` vedle `calls` (jen názvy). Pravidlo: `--tools deploy` (token s rozsahem `read` + `draft`,
úroveň uživatele `draft`) spouští **jen** případy s `write: true`, ostatní sady (`atlas`, `projector`) je přeskočí. Zápis vytvoří koncept stránky, spouštějte jen na lokální instanci.

```powershell
node tests/ai-eval/projector/mcp-agent.mjs --url http://127.0.0.1:5171 --chat http://127.0.0.1:8080/v1 --model qwen3.8-flash-next-iq3_s --tools deploy --map $scratch/map-with.json --out $scratch/d.json
```

## Metriky

- **Vyhledání**: `relevance` (SQL, produkční funkce): Hit@1 / Hit@5 / MRR stránky lexikálně a hybridně, shoda sekce. Hledání `/api/search` po zapnutí AI.
- **Zdroje a citace** (`agent-relevance.mjs`): očekávaná stránka mezi zdroji a mezi citovanými (u více stránek všechny, `expectAll`).
- **Odpověď** (`answerOk`): každý fakt z `facts` v textu (alternativy `a|b`, bez ohledu na velikost písmen a diakritiku), žádný z `forbidden`; u nezodpověditelné otázky nesmí odpověď nic citovat.
- Obojí pro `assistant.agent` (výchozí) i `search.answer` (`--origin search`).

## Spuštění

1. Prázdná PostgreSQL s pgvectorem a dvě databáze (`eval_with`, `eval_without`); LM Studio s `text-embedding-bge-m3` na `:1234`; lokální Gateway (OpenAI-kompatibilní server na `:8080`) a/nebo LM Studio s `google/gemma-4-26b-a4b`.
2. Dvě instance serveru z větve (`dotnet exec Atlas.Server.dll --urls http://127.0.0.1:5171` a `:5172`), `ASPNETCORE_ENVIRONMENT=Development`, `Modules__Projector__Enabled=true`, `ConnectionStrings__atlas-db`, vlastní datové adresáře a `Setup__BootstrapToken` (aspoň 32 znaků, stejný jako v souboru pro `--bootstrap`).
3. Fixture a načtení (index bge-m3 se postaví a aktivuje):

   ```powershell
   node tests/ai-eval/projector/fixtures.mjs with $scratch/backup-with
   node tests/ai-eval/projector/fixtures.mjs without $scratch/backup-without
   <eval-runner> -- load-backup --url http://127.0.0.1:5171 --source $scratch/backup-with --bootstrap $scratch/boot-with --password-file $scratch/pw-with --map $scratch/map-with.json
   ```

4. Chatový profil (klíč jen ze souboru; lokální Gateway a LM Studio berou jakýkoli):

   ```powershell
   # Gateway
   <eval-runner> -- gateway-chat --url http://127.0.0.1:5171 --password-file $scratch/pw-with --gateway-url http://127.0.0.1:8080/v1 --key-file $scratch/local.key
   # LM Studio Gemma (bez přemýšlení)
   <eval-runner> -- gateway-chat --url http://127.0.0.1:5171 --password-file $scratch/pw-with --gateway-url http://127.0.0.1:1234/v1 --key-file $scratch/local.key --provider openai-compatible --model google/gemma-4-26b-a4b
   ```

5. Hledání a odpovědi:

   ```powershell
   <eval-runner> -- relevance --cases tests/ai-eval/projector/cases.json --map $scratch/map-with.json   # ATLAS_EVAL_DB
   $env:ATLAS_EVAL_USER = 'eval-admin'; $env:ATLAS_EVAL_PASSWORD = (Get-Content $scratch/pw-with)
   node <eval-runner>/agent-relevance.mjs --url http://127.0.0.1:5171 --cases tests/ai-eval/projector/cases.json --map $scratch/map-with.json --origin assistant --label x --out $scratch/r.json
   node tests/ai-eval/projector/mcp-agent.mjs --url http://127.0.0.1:5171 --chat http://127.0.0.1:8080/v1 --model qwen3.8-flash-next-iq3_s --tools projector --map $scratch/map-with.json --out $scratch/m.json
   ```

6. Po běhu zastavit servery, Gateway, `lms unload`, databázový kontejner.
