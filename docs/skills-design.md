# Kdo co umí (znalosti lidí) — specifikace 5.1

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
- **Vazby** `TagRelation(TagId, ParentId)`: „je součástí“ (C# je součástí .NET, směrové, bez cyklů). Kdo hledá „.NET“, vidí i lidi
  s „C#“ (užší témata, jedna úroveň), označené „přes C#“. Vazbu „souvisí“ 5.1 nemá (podobná témata dává § 3).

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
| `atlas_link_topics` (zápis) | `PUT /skills/topics/{id}/parents` | vazba „je součástí“, i smazání (vazba „souvisí“ v 5.1 není) |

Našeptávač témat: `GET /skills/topics?q` (název, alias, podobný zápis; existující první).

## 7. UI — uvnitř karty osoby, žádná nová obrazovka

Osoby už Atlas má: **kontakt** je jedna entita sdílená napříč wiki a prostory. Znalosti se proto ukazují tam, kde člověka už vidíte,
a jen na vyžádání (klik); karta kontaktu ani adresář se nezahlcují.

1. **Karta osoby po kliku** (čtecí karta pluginu kontaktů, `readerSkills`): hned pod jménem řádek **Umí** — témata jako štítky s pěti
   tečkami úrovně, nejlepší první, nejvýš 6 a +N. Karta se ukáže hned, znalosti se dočtou z `GET /contacts/{id}/skills`, místo drží
   skryté, takže nic neposkočí. Téma vede na „kdo další to umí“ (`#/library/contacts?q=<téma>`). **Ohodnotit** / **Přidat znalost**
   otevře panel hodnocení u téže osoby (`rateSkills`).
2. **Panel kontaktu pisatele** (`PageContactEditor`) a **detail kontaktu v doku** (`ContentDetail`): stejný řádek (`ContactSkillLine`);
   Ohodnotit tu panel rozbalí pod řádkem (InlineReveal), protože druhý popover ukotvený v panelu by se s ním zavřel.
3. **Panel hodnocení** (`SkillRatePanel`): témata osoby s vaší úrovní (klik je vyplní k úpravě/odebrání), pole tématu — existující téma
   (i podle aliasu a podobného zápisu) první a předvybrané, Enter ho vezme, „Nové téma …“ až poslední —, úroveň 1–5 (OptionPills),
   kdo to uvidí (vlastní: Všichni / Jen já; kolega: Anonymně / S mým jménem / Jen já), poznámka „jak“, Uložit. Na vlastní kartě i
   „Z vaší práce“: témata stránek, které jste psali (jen návrh).
4. **Kdo umí X** = hledání v **Knihovna → Kontakty** (`SkillExperts`): nad kontakty nalezenými podle údajů stojí lidé, kteří téma umí,
   seřazení podle skóre, s důvodem (úroveň, kdo to říká, jak, počet stránek). Řádek tématu ukazuje jiné zápisy, širší téma a
   **Možná totéž → Sloučit** (potvrzovací dialog). Hláška „žádný kontakt“ se nezobrazí, když téma lidi našlo.
5. **AI**: chat i MCP (`atlas_find_experts`, § 5, 6).

Graf kdo–co, dashboard a samostatná obrazovka: **mimo 5.1**. Vzhled z existujících tokenů a štítků stránek; rozložení na 375 px bez
vodorovného posuvníku (ověřeno), texty cs + en.

## 8. Migrace

Jedna migrace: `TagAlias`, `Tag.MergedIntoId`, `TagRelation`, `SkillRating`. Žádný převod dat (tabulky jsou nové); test spustí migraci
nad existujícími štítky a stránkami a ověří, že nic nezměnila.

## 9. Mimo rozsah (5.1)

- AI podobnost témat přes embeddingy (fáze 2, § 3).
- Automatické hodnocení z obsahu (záměrně nikdy, jen návrhy).
- Notifikace „někdo vás ohodnotil“, žádosti o doporučení, historie hodnocení v čase.
- Import znalostí z HR systémů / LinkedIn.
- Rozdělení sloučeného tématu (jen ručně novým tématem).

## 10. Rozhodnutí vlastníka (2026-11-23)

1. **UI**: žádný dashboard ani rozšířená karta kontaktu („moc to tam cpeš“); znalosti v kartě, která se otevře klikem na osobu, načítané
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
   - **Kdo umí X** (Kontakty) → **Přidat sebe**, když tam čtenář chybí (panel s předvyplněným tématem, fokus na úrovni); u každého
     člověka Ohodnotit s předvyplněným tématem. „Přidat lidi“ hromadně: ne (dashboard tématu v 5.1 není); jinou osobu přidá kdokoli
     z její karty.
   - **Návrhy z důkazů**: „Z vaší práce“ v panelu na vlastní kartě, klik/Enter vloží téma, uloží až potvrzení. Nikdy automaticky.
   - **AI a MCP**: `atlas_rate_skill` („zapiš, že Pavel Horák umí informované souhlasy 4/5“; contactId z `atlas_find_experts`
     nebo `atlas_list_entities`), jen na pokyn uživatele.

Zbývá k rozhodnutí později: poznámka „jak“ je zatím obyčejné jednořádkové pole (ne inline Markdown editor stránky), audit sloučení témat
se zapisuje jen u `/api/v1` (`topic.merge`), ne u webu.
