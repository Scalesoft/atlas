# Mapa znalostí — specifikace 5.1

Názvy v UI: funkce **Mapa znalostí** (en *Knowledge map*), hledání v Kontaktech s nápovědou **„Kdo umí…“** (*Who knows…*),
sekce v kartě osoby **„Umí“** (*Knows*). Kód zůstává `skills` / `Skill…`.

Stav: **schváleno a postaveno pro 5.1** (rozhodnutí vlastníka v § 10).

Cíl: vědět, na koho se obrátit. Kdokoli zapíše téma nebo technologii, ohodnotí sebe nebo kolegu, a člověk i AI se pak zeptá
„kdo v projektu Orion dělal informované souhlasy?“ a dostane „Pavel Horák – vývojář; konfiguračně je umí konzultant Tomáš Kolář“.
Minimalisticky: nic, co už Atlas má, se nestaví znovu.

## 1. Co se znovu používá

| Potřeba | Existující část | Rozšíření |
|---|---|---|
| Téma | štítek `Tag` (sdílený katalog, `NormalizedName` unikátní, `PageTag` na stránkách) | aliasy, sloučení, vazby (§ 2.1) |
| Osoba | kontakt `atlas.contact` (jedna entita; každý účet má svůj kontakt, `AccountContacts`) | nic; role = pole kontaktu `title` (Funkce), organizace = `organization` |
| Duplicitní osoby | `atlas_merge_contacts` | při sloučení kontaktů se přesunou i hodnocení (§ 2.2) |
| Stránky k tématu | `PageTag`, `ListTaggedPages`, hledání | nic |
| Kdo na stránce pracoval | `PageRevision.AuthorId`, zmínky kontaktů `PageObjectMention` | nic, jen čtení (§ 4) |
| Práva | čtení stránek a kontaktů (`AccessSql`), vyloučení prostorů z AI | nic |
| AI | MCP nástroje + `[AssistantDescription]` = i AI chat v aplikaci; `/api/v1` | 5 nástrojů (§ 6) |
| Graf | ECharts (dynamický import, jako renderer `a-chart`) | force graf kdo–co |

Nová je jen tabulka hodnocení a dvě malé tabulky k tématům.

## 2. Model

### 2.1 Téma = štítek

Téma je existující štítek. Štítek „.NET“ na stránce a téma „.NET“ v profilu je totéž, takže stránky k tématu jsou zdarma.

- **Alias** `TagAlias(TagId, Name, NormalizedName)`: jiný zápis téhož („dotnet“, „C Sharp“). `NormalizedName` je unikátní
  napříč štítky i aliasy. Kdo napíše alias, dostane jeho štítek — při psaní tématu i při štítkování stránky (`PageTagStore.ResolveAsync`).
- **Sloučení** B → A: B dostane `Tag.MergedIntoId = A`, jeho název se stane aliasem A, hodnocení se přesunou na A (při kolizi zůstane
  vyšší úroveň a novější poznámka), aktuální přiřazení B ke stránkám se uzavřou a otevřou pro A (historie `PageTag` zůstane).
  B se už nenabízí. Slučovat smí každý přihlášený (komunitní údržba); sloučení nejde vrátit voláním
  — proto ho UI i MCP potvrzují.
- **Vazby** `TagRelation(TagId, ParentId, Kind)`: `part_of` „je součástí“ (C# je součástí .NET, směrové, bez cyklů) a `related`
  „souvisí“ (jeden řádek, menší id první). Kdo hledá „.NET“, vidí i lidi s „C#“ (užší témata, jedna úroveň), označené „přes C#“.

### 2.2 Hodnocení osoba–téma

`SkillRating(ContactId, TagId, RaterId, Level, Visibility, Note, UpdatedAt)`, unikátní (ContactId, TagId, RaterId).

- **Škála 1–5** s popisky: 1 Základy · 2 Pracuji s tím · 3 Samostatně · 4 Do hloubky · 5 Učím ostatní.
- **Sebehodnocení** = hodnotitel je účet, jehož kontakt je hodnocený.
- **Hodnocení od ostatních** = kolega hodnotí kontakt (i externí osobu, např. konzultanta dodavatele).
- **Poznámka „jak“** (nepovinná, jeden řádek inline Markdownu, do 200 znaků): „konfiguračně“, „vývoj integrace“, „Orion 2025“.
  Z ní AI skládá „konfiguračně je umí …“.
- **Viditelnost** každého hodnocení: `private` (jen pro mě — evidence pro sebe), `shared` (vstupuje do anonymního souhrnu),
  `signed` (souhrn + moje jméno u hodnocení, „doporučuji“ jako na LinkedIn). Sebehodnocení je `private` nebo `shared`.
- Sloučení dvou kontaktů (`atlas_merge_contacts`) přesune hodnocení na výsledný kontakt; kolize jako u sloučení témat.
- Kontakt v koši nebo bez práva čtení se nezobrazuje nikde, ani v souhrnu.

### 2.3 Souhrn a anonymita

Čtenář R u osoby P a tématu T vidí:

| Údaj | Kdy |
|---|---|
| sebehodnocení P | je `shared`, nebo R = P |
| průměr a počet hodnocení od ostatních (`shared` + `signed`) | počet ≥ **K = 3**; pod prahem jen „hodnotili 1–2 kolegové“ bez čísla |
| jména a úrovně podepsaných (`signed`) | vždy |
| moje vlastní hodnocení P | vždy (i `private`) |
| kdo dal kolik anonymně | jen **správce wiki** (rozhodnutí vlastníka); ostatní ani P ne |

**Skóre** pro řazení: průměr viditelných čísel (sdílené sebehodnocení a průměr ostatních nad prahem, ten s vahou počtu). Hodnocení
pod prahem do skóre nevstupují — jinak by šla dopočítat. Na serveru se souhrn počítá v jednom SQL; klient nikdy nedostane jednotlivá
anonymní hodnocení.

## 3. Deduplikace a shlukování

Témata se budou množit a každý je napíše trochu jinak; proto se duplicitám brání **při psaní**, ne úklidem potom.

**Bez AI (vždy):**
- **složený klíč** názvu: malá písmena, bez diakritiky (`unaccent`), bez mezer a interpunkce kromě `#` a `+` („C Sharp“, „c-sharp“ →
  `csharp`; „C#“ zůstane `c#`, „C++“ `c++`). Nové téma se stejným složeným klíčem jako existující téma nebo alias se nezaloží,
  použije se existující;
- našeptávač nabízí existující téma první, založení nového až poslední;
- aliasy a sloučení (§ 2.1);
- **návrh duplicit**: složený název obsahuje druhý, nebo trigramová podobnost (`pg_trgm`, už nainstalované) ≥ 0,4; nabízí se nad
  výsledky „kdo umí X“ jako „Možná totéž: …“ s tlačítkem Sloučit (a v `atlas_find_experts`).
- Tentýž složený klíč platí i pro štítkování stránek (`PageTagStore.ResolveAsync`): „c sharp“ na stránce je existující štítek „C Sharp“.

**S AI (volitelné zlepšení, fáze 2):** podobnost témat podle průměrného embeddingu stránek, které štítek nesou (žádné nové volání
modelu, jen data indexu AI). Když index AI chybí nebo AI nemůže odpovídat, sekce se nezobrazí — nic nehlásí chybu.

## 4. Důkazy z obsahu

Pro osobu P (kontakt; účet podle `directoryId`) a téma T (včetně aliasů, sloučených a podřízených témat): čtenářem čitelné stránky
s aktuálním štítkem T, na nichž je P autorem revize nebo je zmíněný kontakt (`PageObjectMention`). Výsledek: počet a nejnovějších
5 stránek.

- U osoby: „Navržená témata z vaší práce“ — témata stránek, kde jsem autorem a ještě je nemám ohodnocená. Jen **návrh** s tlačítkem
  Přidat; nikdy se nezapíše hodnocení samo.
- U tématu: lidé, kteří k tématu psali, ale nemají hodnocení, ve skupině „Pracovali na tom“ bez hvězdiček.

## 5. Dotaz „kdo v projektu Orion dělal X“

Dotaz = **téma** + volitelný **kontext** + volitelná **role**.

- Téma se najde podle názvu, aliasu nebo sloučení („informované souhlasy“ → štítek).
- Kontext („Orion“) se rozloží na: prostor stejného názvu, štítek stejného názvu, a organizaci kontaktu. Omezí důkazy (stránky
  v prostoru / se štítkem) a u osob z organizace je zvýrazní; hodnocení s poznámkou obsahující kontext se řadí výš.
- Role = pole kontaktu `title` (Funkce): „vývojář“, „konzultant“. Lze jí filtrovat.
- Výstup na osobu: jméno, role, organizace, skóre, poznámky „jak“ (sdílené a podepsané), důkazy v kontextu. Z toho AI složí
  „Pavel Horák – vývojář; konfiguračně je umí konzultant Tomáš Kolář“.
- Řazení: skóre, pak počet důkazů v kontextu. Osoba jen s důkazy (bez hodnocení) je ve výsledku označená „pracoval/a na tom“.

## 6. MCP nástroje a API

AI parita: každý nástroj odpovídá endpointu `/api/skills/...` a `/api/v1/skills/...` (čtení s rozsahem Čtení, zápis Zápis).
Nástroje respektují vyloučení prostorů z AI (důkazy z nich se nevrací).

| Nástroj | Endpoint | Co dělá |
|---|---|---|
| `atlas_find_experts` (čtení) | `GET /skills/experts?topic&context&role` | dotaz § 5 |
| `atlas_get_skills` (čtení) | `GET /contacts/{contactId}/skills` | co osoba umí (souhrny, vlastní hodnocení, podepsaná hodnocení, důkazy, u sebe návrhy) |
| `atlas_rate_skill` (zápis) | `PUT /contacts/{contactId}/skills` | hodnocení jménem vlastníka tokenu: téma názvem (založí štítek), úroveň 1–5 nebo `null` = smazat, viditelnost, poznámka |
| `atlas_merge_topics` (zápis, `confirm`) | `POST /skills/topics/{id}/merge` | sloučení B → A po potvrzení člověkem |
| `atlas_link_topics` (zápis) | `PUT /skills/topics/{id}/parents` | vazba `kind` part_of (je součástí) nebo related (souvisí), i smazání |
| — (čtení) | `GET /skills/graph?spaceId` | graf kdo–co: uzly lidé a témata, hrany s úrovní (pod prahem bez čísla) a vazby témat |

Našeptávač témat: `GET /skills/topics?q` (název, alias, podobný zápis; existující první).

## 7. UI — v kartě osoby a v Kontaktech, žádná nová obrazovka

Osoby už Atlas má: **kontakt** je jedna entita sdílená napříč wiki a prostory. Znalosti se ukazují tam, kde člověka už vidíte, a téma
i graf žijí v **Knihovna → Kontakty** (stávající přepínač Adresář / Celá wiki / **Mapa znalostí**), takže nepřibyl žádný navigační vzor.

1. **Karta osoby po kliku** (čtecí karta pluginu kontaktů, `readerSkills`): pod jménem řádek **Umí** — témata jako štítky s pěti tečkami,
   nejlepší první, nejvýš 6 a +N; dočte se po kartě, místo drží skryté. Téma vede na jeho dashboard (`#/library/contacts?q=<téma>`).
   **Ohodnotit** / **Přidat znalost** otevře panel hodnocení (`rateSkills`). Menu akcí čipu (Ctrl+. / Ctrl+Space / pravé tlačítko) má **Umí…**.
2. **Panel kontaktu pisatele** (`PageContactEditor`): stejný řádek (`ContactSkillLine`), Ohodnotit rozbalí panel pod řádkem (InlineReveal).
   **Detail kontaktu v doku** (`ContentDetail`) ukazuje místo čipů **pruhy znalostí** (`SkillBars`): pruh na téma, délka = úroveň, seskupené
   podle širšího tématu (shluky, .NET nad C#), zbytek „Další“. Pruhy místo radaru: čtou se při libovolném počtu témat i na 375 px.
3. **Panel hodnocení** (`SkillRatePanel`, pole tématu `TopicField`): existující téma (i alias a podobný zápis) první a předvybrané, Enter,
   „Nové téma …“ až pod podobnými; pak 1–5 a Enter uloží. Viditelnost (vlastní: Všichni / Jen já; kolega: Anonymně / S mým jménem / Jen já),
   poznámka „jak“, na vlastní kartě „Z vaší práce“.
4. **Dashboard tématu** = hledání tématu v Adresáři (`SkillExperts`): kdo téma umí (seřazeno podle skóre, role, organizace, proč), **rozložení
   úrovní** (malý sloupcový graf 1–5), **vazby** Je součástí / Části / Souvisí s odebráním a přidáním (+ Vazba: druh a `TopicField`),
   **stránky k tématu** (výpis štítku stránek), **Přidat sebe** a **Přidat lidi** (víc kontaktů najednou přes `SearchableTags`, lidé
   organizací tématu první, jedna úroveň), „Možná totéž → Sloučit“. Hláška „žádný kontakt“ se nezobrazí, když téma lidi našlo.
5. **Graf kdo–co** (`SkillGraph`, třetí volba přepínače v Kontaktech): lidé ↔ témata (síla čáry = úroveň, kterou čtenář smí vidět),
   témata ↔ témata (je součástí plně, souvisí čárkovaně). Filtry organizace (z lidí grafu), prostor (témata jeho stránek, `GET /skills/graph?spaceId`)
   a text tématu (téma, jeho sousedé a jejich lidé). Klik na téma otevře jeho dashboard, na osobu její kartu pod grafem (ContactCard + pruhy).
   ECharts s dynamickým importem jako `HistoryActivityChart` (renderer editoru `a-chart` bere jen tabulku). Graf/Seznam: seznam je týž graf
   pro klávesnici; pod 680 px místa (telefon) je jen seznam. Graf drží nejvýš 60 témat, která zná nejvíc lidí (poznámka pod ním).
6. **AI**: chat i MCP (`atlas_find_experts`, `atlas_link_topics` s `kind`, § 5, 6).

Vzhled z existujících tokenů a štítků stránek (témata fialově jako štítek, světlý i tmavý režim), skeleton s odkladem, stagger, fokus
viditelný, texty cs + en. Ověřeno na 1440 px (světlý i tmavý) a 375 px, jako správce i jako běžný uživatel bez práv správce.

## 7a. Stovky témat a lidí

Témat budou stovky (u jednoho člověka i desítky až stovky). Nic se proto neukazuje celé najednou; vždy nejdřív to podstatné a zbytek
na jedno kliknutí nebo přes filtr. Zátěžová ukázka je v galerii (`#/gallery/skills`, „Stovky témat“: 240 témat jedné osoby ve 24 skupinách,
téma se 40 lidmi, wiki s 320 tématy a 90 lidmi).

| Místo | Co se ukáže | Zbytek |
|---|---|---|
| Řádek **Umí** v kartě osoby / panelu kontaktu | 6 nejlepších témat | +N (vede na celý přehled) |
| **Pruhy znalostí** osoby (dok, osoba v grafu) | prvních 6 skupin (shluky podle „je součástí“), v každé 5 nejlepších | „Zobrazit další (N)“ ve skupině, „Dalších N skupin“, od 20 témat **filtr** (otevře všechny shody) |
| **Dashboard tématu** | 12 karet lidí (nejlepší první; server vrací nejvýš 50) | „Zobrazit další (N)“ po 12 |
| **Graf kdo–co** | nejvýš 60 témat, která zná nejvíc lidí, s jejich lidmi; nad 80 uzlů hustší rozložení a jména lidí jen při najetí | filtr tématu, organizace a prostoru; poznámka „Ukazují se témata, která zná nejvíc lidí“; **Seznam** ukáže totéž jako řádky (u tématu 8 lidí a +N) |
| Našeptávač témat | 5 nejbližších (přesný zápis, alias, začátek, podobný) | psaním dál |

Skupiny, karty a řádky mimo obraz se nevykreslují (`content-visibility`), takže i velký seznam drží rychlost. Proti množení témat
pomáhá hlavně § 3: jiný zápis se spojí sám, podobné nabídne „Možná totéž → Sloučit“, a vazba „je součástí“ z nich dělá shluky.

**Karty lidí** v dashboardu jsou stejně výrazné jako kontakt na tabuli (rámeček, stín, celá karta kontaktu s fotkou, funkcí a organizací),
pod nimi úroveň slovy s tečkami, Ohodnotit a proč (kdo to potvrdil, jak, kolik stránek).

## 8. Migrace

Jedna migrace: `TagAlias`, `Tag.MergedIntoId`, `TagRelation`, `SkillRating`. Žádný převod dat (tabulky jsou nové); test spustí migraci
nad existujícími štítky a stránkami a ověří, že nic nezměnila.

## 9. Mimo rozsah (5.1)

- AI podobnost témat přes embeddingy (fáze 2, § 3).
- Samostatná obrazovka nebo trasa pro téma či graf (vše v Kontaktech).
- Automatické hodnocení z obsahu (záměrně nikdy, jen návrhy).
- Notifikace „někdo vás ohodnotil“, žádosti o doporučení, historie hodnocení v čase.
- Import znalostí z HR systémů / LinkedIn.
- Rozdělení sloučeného tématu (jen ručně novým tématem).

## 10. Rozhodnutí vlastníka (2026-11-23)

1. **UI**: žádná samostatná obrazovka ani rozšířená karta kontaktu („moc to tam cpeš“; dashboard a graf později v Kontaktech, bod 7); znalosti v kartě, která se otevře klikem na osobu, načítané
   postupně. Osoby = kontakty sdílené napříč prostory.
2. **Hodnocení od kolegů**: ano; průměr až od **3** sdílených hodnocení; **správce wiki vidí všechna** sdílená hodnocení se jmény
   hodnotitelů (soukromá `private` nevidí nikdo kromě autora).
3. **Koho lze hodnotit**: všechny kontakty, i externí bez účtu.
4. **Slučování témat**: smí každý přihlášený (s potvrzením).
5. **Škála** 1–5 s popisky (navrženo, nerozporováno).
6. **Rychlé připsání znalosti** (doplněno týž den), vždy z klávesnice: první návrh předvybraný, Enter ho vezme, 1–5 nastaví úroveň, Enter
   uloží; nic nového, jen existující pickery, čipy a menu akcí:
   - **Karta osoby / panel kontaktu / dok** → Ohodnotit (`SkillRatePanel`): jedno pole tématu (existující témata a aliasy první, podobné
     nad „Nové téma …“, takže nové téma vznikne až potvrzením řádku, který jako jediný zbývá), pak 1–5 a Enter.
   - **Čip osoby ve stránce** → menu akcí **Ctrl+.** (sdílené menu záměrů editoru, řádek pluginu kontaktů) → **Umí…** otevře týž panel.
   - **Dashboard tématu** (Kontakty) → **Přidat sebe**, když tam čtenář chybí (panel s předvyplněným tématem, fokus na úrovni), a
     **Přidat lidi** (víc kontaktů najednou, jedna úroveň); u každého člověka Ohodnotit s předvyplněným tématem.
   - **Návrhy z důkazů**: „Z vaší práce“ v panelu na vlastní kartě, klik/Enter vloží téma, uloží až potvrzení. Nikdy automaticky.
   - **AI a MCP**: `atlas_rate_skill` („zapiš, že Pavel Horák umí informované souhlasy 4/5“; contactId z `atlas_find_experts`
     nebo `atlas_list_entities`), jen na pokyn uživatele.

7. **Před sloučením do 5.1** (týž den): dashboard tématu, graf vazeb kdo–co s filtry a seznamem na telefonu, vizualizace znalostí osoby
   a vazby „souvisí“ / „je součástí“ nastavitelné z UI — vše v existujících místech (§ 7).

Zbývá k rozhodnutí později: poznámka „jak“ je zatím obyčejné jednořádkové pole (ne inline Markdown editor stránky), audit sloučení témat
se zapisuje jen u `/api/v1` (`topic.merge`), ne u webu.
