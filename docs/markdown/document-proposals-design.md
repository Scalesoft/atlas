# Návrhy celého dokumentu

Návrh architektury z 5. 9. 2026 podle posledního upřesnění uživatele. Nahrazuje schvalování jednotlivých operací i předchozí úvahu o úplném vynechání schvalování.

## Implementovaný rozsah

První verze nabízí samostatné API a galerii `?story=proposals`: návrh celého rich dokumentu, průběžně ukládané checkpointy a předložení jedním kliknutím, přijetí nebo odmítnutí celku, zachovanou historii a kontrolu konkurenčních revizí. Pravidla jsou ve službě `DocumentProposalService`, úložiště za veřejným `IProposalDocumentStore`. Git je jedna vyměnitelná implementace. [Kontrakt úložiště a zapojení](document-proposals-storage.md).

UI i API podporují výhradně celý dokument. UI nenabízí částečné schválení; API nepodporovaný režim ani neznámý výběr bloků nepřevede na přijetí celku. Rozhodovat smí samostatně autorizovaný schvalovatel. Výchozí vývojový host tuto roli dává uživateli `reviewer`; produkční host musí dodat vlastní autentizaci a review policy.

Tato první verze používá lokální editaci návrhu a serverové checkpointy s kontrolou revize. Souběžné uložení do stejného návrhu vrátí konflikt a UI uchová místní obsah. Nejde zatím o živé psaní do společné Yjs relace. Proposaly mají oddělený API/storage namespace a nepřepisují existující live dokumenty. Následující části o Yjs relacích, stabilních blocích a merge popisují cílový stav, nikoli hotovou integraci.

## Budoucí volitelný režim přijetí částí

Výchozí režim zůstává celý dokument. Částečné přijetí lze doplnit až se stabilní identitou bloků a společnou implementací UI, serverové validace i testů. Výběr se musí vztahovat ke konkrétní revizi a jejímu základu. Server vytvoří novou schválenou revizi jen z posouzených změn, zbytek zachová v návrhu a výsledek znovu předloží k posouzení. Přesun se nesmí zaměnit za smazání/vložení; závislé změny a nejednoznačné konflikty musí být explicitně vyřešeny. Nyní není přidaný přepínač ani obecný framework strategií.

## Pravidla

Dokument má jednu aktuální schválenou revizi. Nový proposal vzniká z této revize a představuje celý dokument: obsah, formátování i pořadí bloků. Pamatuje si základovou revizi a má vlastní historii a vlastní prostor pro živou spolupráci. Současně může existovat více proposalů nad stejným základem.

Další editace rozpracovaného proposalu pokračuje v něm. Nový nezávislý proposal vždy začíná z aktuální schválené verze. Uživatel musí v UI jasně vidět, který proposal upravuje.

- Odmítnutí uzavře celý proposal. Schválený dokument ani ostatní proposaly se nemění. Historie odmítnutého návrhu zůstává dostupná podle pravidel uchování.
- Pokud Bob upravuje Alicin proposal, jeho změny jsou součástí téhož návrhu a případné odmítnutí se vztahuje i na ně. Pokud pracuje ve vlastním proposalu, odmítnutí Alicina návrhu se jej netýká.
- Schvaluje se konkrétní neměnná revize proposalu. Změnil-li se od prohlédnutí obsah návrhu, schvalování musí vyžádat nové posouzení.
- Schválení proběhne atomicky na serveru s kontrolou oprávnění a očekávané schválené revize i očekávané revize návrhu. Opakovaný požadavek je idempotentní.
- Přijatý návrh vytvoří novou schválenou revizi; starší schválené revize se nepřepisují. Přijatý a odmítnutý proposal se již přímo neupravují.

## Konkurenční návrhy

Ze schválené V1 vzniknou A a B. A je přijato a vytvoří V2. B nyní vychází ze staršího základu. Nelze jej schválit prostým přepsáním celého dokumentu: tím by zmizely již přijaté změny A.

První verze může bezpečně povolit přijetí pouze nad aktuálním základem. U zastaralého návrhu vyžádá aktualizaci podle aktuální schválené verze a nové posouzení. Třícestné slučování lze přidat jako oddělenou službu nad stejnými revizemi; do té doby nesmí UI naznačovat, že je automatický merge dostupný.

## Inspirace Gitem: třícestné sloučení

Git porovnává oba slučované stavy s jejich společným předkem a při neřešitelných konfliktech požaduje zásah uživatele: [Git — Basic Branching and Merging](https://git-scm.com/book/en/v2/Git-Branching-Basic-Branching-and-Merging), [git merge](https://git-scm.com/docs/git-merge).

Pro náš editor jsou vstupy společný základ návrhu, aktuální schválená revize a posuzovaná revize návrhu. Výsledkem je nová kandidátní revize a případný seznam konfliktů; schválená verze se samotným výpočtem merge nemění. Reviewer musí vidět právě výsledný kandidát vůči aktuální schválené revizi.

Slučování má pracovat se strukturovaným obsahem a stabilními ID bloků. Markdown je u rich dokumentů ztrátová projekce, nikoli vhodný univerzální merge formát. Git poskytuje vzor verzování a slučování; nepředpokládáme, že jeho řádkový merge sám vyřeší přesuny, tabulky a pluginové uzly.

Navržená konzervativní pravidla:

| Změny proti společnému základu | Výsledek |
| --- | --- |
| Každá strana upraví jiný blok | Automatické sloučení, pokud nejsou v konfliktu strukturální vztahy. |
| Jedna strana přesune B, druhá upraví text B | Zachovat přesun i aktuální text podle stabilního ID. |
| Obě strany změní stejný text různě | Konflikt, pokud textový třícestný merge nedokáže změny bezpečně sloučit. |
| Jedna strana smaže B, druhá B upraví nebo přesune | Explicitní konflikt; automaticky nevyhazovat obsah ani blok neoživovat. |
| Obě strany přesunou stejný blok jinam | Konflikt umístění. |
| Pluginový obsah nemá známou merge strategii | Konzervativně konflikt při rozdílných souběžných změnách. |

Výsledná struktura se musí validovat proti profilu a schématu pluginů. Úspěšná syntaktická kombinace sama nezaručuje smysluplný obsah, proto i automaticky sloučený kandidát podléhá posouzení.

## Historie a minimální datový model

- `Document`: odkaz na aktuální schválenou revizi.
- `Proposal`: dokument, základová revize, aktuální revize návrhu, stav.
- `Revision`: neměnný stav dokumentu, rodičovské revize, autor a čas. Merge uchová původ z obou stran.

Každý proposal má vlastní Yjs relaci a technický log pro obnovu práce. Psaní se v uživatelské historii seskupuje; keystroke není samostatný schvalovaný commit. Neměnná revize vzniká minimálně při předložení ke schválení, aktualizaci návrhu a přijetí. Lokální Undo je oddělené od dlouhodobých revizí i od rozhodnutí schválit/odmítnout.

Hranice relací se musí uplatnit při autorizaci, ukládání, transportu i presence; aktualizace jednoho proposalu nesmějí proniknout do jiného. Již uložené snapshoty a logy se nesmějí automaticky přeznačit za nový formát.

## Zvažovaná varianta úložiště: Git

Uživatel navrhl skutečný Git repozitář pod dokumentovým úložištěm a následně požadoval vyměnitelný provider. Referenční `GitProposalDocumentStore` již ukládá neměnné revize v jednom bare repozitáři na dokument. Schválený obsah a proposaly jsou samostatné JSON položky jednoho stromu. Jediná reference `refs/heads/history` publikuje celý strom atomicky pomocí compare-and-swap; historie konkrétního proposalu se čte podle jeho položky. Nepotřebujeme transakci několika větví ani sdílený checkout. Následující úvahy o skutečných větvích a merge zůstávají budoucí možností, ne povinným kontraktem úložiště. Výkonnost ve velkém provozu zatím není ověřená.

- Živá spolupráce dál používá Yjs a trvale ukládané aktualizace v rámci jednoho proposalu. Git commity tvoří pojmenovatelné revize/checkpointy a předložení či přijetí návrhu; nevytváří se commit na každý stisk klávesy.
- Schválený stav a jednotlivé proposaly mají vlastní Git reference. Repository scope (dokument, wiki či projekt) je potřeba zvolit podle požadované izolace; proposal dokumentu nesmí omylem přijmout jiné změny celého projektu.
- Pro rich obsah se uchovává kanonický strukturovaný dokument se stabilními ID bloků a verzí schématu. Markdown je generovaný čitelný export. Binární stav Yjs a jeho log mají navázaný checkpoint pro bezpečné obnovení stejné relace; textový export tuto identitu nenahrazuje.
- Práva, stav schvalování a UI zůstávají odpovědností aplikace. Metadata commitu sama nejsou ověřením oprávnění autora či schvalovatele.
- Server nepřepíná jednu sdílenou pracovní kopii mezi uživateli. Zápisy pracují nad objekty/refy repozitáře nebo izolovaným pracovním prostorem.
- Změna schválené reference musí kontrolovat její očekávaný původní commit. Git `update-ref` tuto kontrolu podporuje. Uložení rozhodnutí a změna reference navíc potřebují idempotentní postup s obnovou po pádu; Git automaticky nevytváří společnou transakci s databází aplikace.
- Odmítnuté návrhy musí zůstat dosažitelné přes archivní reference podle retenční politiky. Smazání větve s očekáváním, že historie zůstane navždy dostupná, není vhodné.

Git umí běžný třícestný textový merge i vlastní merge driver. Samotná textová reprezentace JSON/Markdownu nezaručuje správné sloučení pořadí bloků, tabulek nebo pluginových struktur. Pro dokumentové konflikty proto stále platí výše uvedená konzervativní pravidla; Markdown export se po merge znovu vygeneruje z kanonického dokumentu.

Před nahrazením stávajícího store ověřit malý prototyp: dvě konkurenční větve, přesun proti editaci, odmítnutí se zachovanou historií, současná schválení, pád mezi uložením objektu a posunem reference a vazbu snapshotu na log živých změn. Git úložiště samo neopravuje současnou chybu delete/copy přesunu v živém editoru.

Zdroje: [Git objects](https://git-scm.com/book/en/v2/Git-Internals-Git-Objects), [update-ref](https://git-scm.com/docs/git-update-ref), [merge drivers](https://git-scm.com/docs/gitattributes#_defining_a_custom_merge_driver).

## Zbývající oprava živých přesunů

Rozhodnutí z 8. 9. 2026: přesuny bloků zůstávají běžnou editací (smazání a vložení) i uvnitř živé relace. Známé hranice popisuje [collaborative-block-moves-and-review.md](collaborative-block-moves-and-review.md). Identita bloků se nezavádí.

## Zásadní ověření

Izolace dvou proposalů; odmítnutí A při psaní v B; přijetí přesně posouzené revize; editace během schvalování; dvě současná přijetí nad V1; zastaralý základ; opakované přijetí po výpadku odpovědi; práva čtenáře a schvalovatele; obnova po restartu; merge přesunu proti editaci, smazání proti editaci a konflikt pořadí; zachování rich/pluginových dat; živé přesuny při offline psaní ve stejném proposalu.
