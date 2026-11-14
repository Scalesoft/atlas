# Kontakty

Volitelný vstup `@atlas/markdown/plugins/contacts`. Základní definice dat
a schéma jsou synchronní; formuláře, adresář a styly se načtou s runtime při
prvním zobrazení. Plugin vyžaduje profil `rich`.

```ts
import { createContactsPlugin, insertContact, insertInlineContact } from '@atlas/markdown/plugins/contacts'

const contacts = createContactsPlugin({
  directory: {
    search: (query, { signal }) => companyDirectory.search(query, signal),
    get: (id, { signal }) => companyDirectory.get(id, signal),
    // Optional. Only a verified internal roster match produces an internal identity.
    resolveInternal: (contact, { signal }) => employeeDirectory.resolve(contact, { signal }),
  },
})
// Register contacts in the controller's plugins array before creating its view.
controller.dispatch(insertContact({
  name: 'Jana Nováková', title: 'Projektová manažerka', organization: 'Firma',
  phones: ['+420 123 456 789'], emails: ['jana@example.cz'],
}))
controller.dispatch(insertInlineContact({ name: 'Petr Novák', emails: ['petr@example.cz'], kind: 'auto' }))
```

Adresář je volitelný. Hostitelská služba vrací `MarkdownContact`, jehož
`directoryId` identifikuje propojenou vizitku. Oprávnění a síť řeší hostitel.
`objectId` (volitelné, GUID malými písmeny) je ID objektu kontaktu v adresáři hostitele, aby hostitel poznal, kde se kontakt používá; neplatná hodnota se zahodí.
Vyhledávání se otevírá akcí „Přidat z adresáře“ v kontextové liště nad vybraným
blokem a probíhá až po stisknutí „Vyhledat“. Zavření panelu ruší hledání. Dokument uchovává snímek
údajů; „Aktualizovat z adresáře“ jej výslovně nahradí. Automatická synchronizace
údajů neprobíhá. Odstraněný kontakt nebo chyba ponechá uloženou vizitku. Služba
dostane `AbortSignal`; opožděné výsledky po zavření nebo nahrazení bloku se ignorují.

Kontakty obsahují jméno, uživatelské jméno, pozici, firmu, oddělení, fotografii
a web přes HTTP(S), adresu a až 20 telefonů a e-mailů. Uživatelské jméno
(`username`, např. firemní login) je jednořádkové, nejvýše 256 znaků; vizitka
ho zobrazuje s popiskem a do vCard se neexportuje, protože vCard 4.0 pro něj
nemá přenosnou vlastnost. Každý kontakt je samostatný blok (karty jdou ve stránce jedna za druhou a mezi ně jde psát text i nadpisy); nový kontakt z bloku („Přidat kontakt“, „Přidat z adresáře“, výběr několika lidí) se vloží jako další blok hned za něj; v hostiteli, jehož blok nesnese souseda (editor uvnitř prvku `data-md-single-block`, blok na tabuli), se přidá do seznamu toho bloku. Data bloku jsou kvůli starším stránkám dál seznam (1 až 100 položek). V bloku lze
kontakty přidávat, upravovat a odebírat přes kontextovou lištu a ukotvené panely;
samotná karta obsahuje fotografii nebo iniciály, údaje a stažení vizitky. Správa
se do obsahu dokumentu nevkládá. Inline kontakt otevře podrobnosti v ukotveném
čtenářském panelu; vCard je uvnitř panelu, nikoli jako samostatná ikona ve větě.
Readonly i samostatný renderer zachovávají odkazy a stažení kontaktů.

## Interní zmínky a externí vizitky

`MarkdownContact.kind` může být `auto`, `internal` nebo `external`. Chybějící
hodnota znamená `auto`. Toto pole určuje pouze prezentaci kontaktu v textu;
nevyjadřuje oprávnění, ověření účtu ani online přítomnost. Ruční volba je
v nastavení kontaktu a přežije výslovnou aktualizaci údajů z adresáře.

Interní kontakt se v textu vykreslí jako malá zmínka se jménem a 18px avatarem,
barvou inspirovanou avatary spolupráce. Externí kontakt zachovává vizitkovou
podobu. Obě varianty otevřou telefon, e-mail a vCard v panelu. Blokový seznam
zachovává plné kontaktní karty.

Volitelná služba `directory.resolveInternal(contact, { signal })` vrací
`MarkdownInternalContactUser | null`, tedy `{ id: string, name?: string,
photo?: string }` při jednoznačné shodě s interním uživatelem. Běžné
`directoryId` **nikdy samo neznamená interního uživatele**: v adresáři mohou
být také zákazníci a dodavatelé. Resolver nevolá obecné `get` jako náhradu
ověření. Oprávnění, identifikátory a případnou komunikaci s backendem vlastní
hostitelská aplikace; tento repozitář nemá obecný endpoint interního adresáře.

Automatické dohledání proběhne jen pro inline kontakt v režimu `auto`.
Výsledek mění pouze prezentaci, nevytváří transakci, undo krok ani nezapisuje
do dokumentu. Při `null` se použije externí podoba; chyba ponechá dosavadní
podobu. Odstranění či nahrazení uzlu ruší `AbortSignal` a pozdní výsledek se
ignoruje. Resolver může fungovat také v readonly a statickém rendereru.

Hostitel může výslovně uložit `internalUser` jako snímek známé identity spolu
s kontaktem. Použije se před dohledáním nebo bez resolveru. `external` vždy
vynutí vizitku i u tohoto snímku; `internal` vynutí drobnou zmínku i bez něj.
Úprava kontaktu zachovává snímek, běžné vykreslení jej nepřepisuje a vCard
neexportuje interní identifikátor. Chybějící resolver a chybějící snímek
zachovají pro starší kontakty externí vzhled.

`serializeVCard(contact | contacts)` vytváří UTF-8 vCard 4.0 podle
[RFC 6350](https://www.rfc-editor.org/rfc/rfc6350.html), včetně CRLF, escapování
a zalamování po nejvýše 75 bajtech bez rozdělení vícebajtových znaků. Tlačítko
„Uložit kontakt“ stáhne `.vcf`; import dokončuje aplikace uživatele.
Relativní adresy fotografie a webu zůstávají v dokumentu, ale nevstupují do
URI položek vCard. QR přenos tato verze neobsahuje.

Pro skládání dokumentů jsou dostupné `contactNode(contact | contacts)` a
`inlineContactNode(contact)`. U ručně sestaveného rich dokumentu uveďte
`plugins: [{ id: 'contacts', version: 2 }]`. Plugin má migraci z verze 1,
která zachová původní kontakty; starší klient neotevře novou verzi bez znalosti
nových údajů. Data uzlu nadále mají tvar
`{ version: 1, contacts: MarkdownContact[] }`; inline varianta obsahuje právě
jeden kontakt. Markdown export poskytuje čitelný text a označuje ztrátovost.
# Typing suggestions

`createContactsPlugin({ directory, suggestions: true })` enables `@name` search using the same `ContactDirectoryService` as the existing contact forms. It is off by default. Use `suggestions: { debounceMs: 200, limit: 20 }` to adjust the shared search limits. Empty queries show a hint without enumerating the directory. The optional `directory.cached(query)` answers synchronously what the host already knows (its lexicon): those rows show at once instead of the loading state, and the directory's answer replaces them. New queries, readonly and destroyed views cancel requests and reject late results.

Selections insert the existing `contactInline` node through the same normalization, retaining explicit `internalUser` and `directoryId`. Generic directory membership never implies an internal account. Existing reader resolution can decorate the selected contact without changing its saved data. Email addresses, URL marks and code do not activate the picker. Czech and multiword names are supported. Enter/click creates one isolated undo operation; Escape and Tab close without insertion.

The list keeps the directory's order of match quality; among equally good matches the people the page's context names come first (on the page, a role the words before the `@` name, picked lately in this tab). Every explicit pick (`@`, „Je to …“ at a written name, the actions menu, an unresolved chip) calls the optional `directory.learn(query, contact)` with the text it was picked for, so the host may teach the directory contact that text as an alias. A person from `known` may carry the directory's `aliases`: those of three letters or more are underlined and offered like the name (a three-letter one only as learned, „MMu“, never inside an e-mail), and never become a chip by themselves.

Supply an application-authorized directory implementation for production; the gallery is a synthetic roster, not an enterprise directory. Recreate the controller/plugin configuration when replacing the service so outstanding work and permission-sensitive state are disposed.

# Živý odkaz na kontakt a role na stránce

Snapshot má klíč `role` (jeden řádek, nejvýše 400 znaků): místní role nebo poznámka u odkazu („PM projektu“). Píše se hned za `name` (YAML i inline `role=`), v kartě a ve čtečce je pod jménem a do adresářového kontaktu se nikdy nepropisuje.

Čtyři volitelné hostitelské volby drží plugin nezávislý na backendu:

- `live(objectId, { signal }) → LiveContact`: kontakt s `objectId` se nejdřív vykreslí ze snapshotu a po odpovědi se překreslí, jen když se něco liší (krátké prolnutí podle `--md-motion-base`). `{ state: 'ok', contact }` ukáže živá pole (role, `kind` a `internalUser` zůstávají ze snapshotu), `hidden` ponechá snapshot, `trashed` a `missing` ukážou ztlumené jméno se štítkem „nedostupný kontakt“ bez odkazů na telefon a e-mail a bez vizitky. Chyba nebo zrušení signálu ponechá snapshot. vCard stahuje to, co je vidět.
- `link(contact, { signal, insert? }) → MarkdownContact | null`: v editovatelném dokumentu se jednou za vykreslení zavolá pro kontakty bez `objectId`. Vrácené snapshoty (s `objectId`) se zapíšou do uzlu; po zrušení signálu se nezapíše nic a chyba nechá snapshot a tiše to oznámí ve stavovém řádku. S `insert: { editor, anchor }` ho plugin volá, když člověk kontakt právě vkládá (`@`, formulář kontaktu, „Přidat z adresáře“), i pro kontakt s `objectId`: hostitel se smí zeptat na shodu v adresáři a říct, že kontakt už na stránce je; `null` znamená „zpět“ a nevloží se nic (u `@` se vrátí napsaný text, formulář se otevře znovu).
- `directoryUrl(objectId) → string`: formulář propojeného kontaktu mění jen místní roli a zobrazení; ostatní údaje se mění v adresáři a formulář na něj odkáže.
- `editLinked(contact, { editor, anchor, signal, save }) → cleanup`: úprava kontaktu s `objectId` místo formuláře role a zobrazení. Hostitel otevře u `anchor` vlastní editor kontaktu v adresáři i místních údajů (role, uživatelské jméno, zobrazení) a `save` zavolá jednou při zavření s novým snapshotem (zápis vizitku překreslí).

V upravitelném dokumentu otevře klik na vizitku (mimo odkazy a stažení) a klik na zmínku v textu úpravu toho kontaktu: propojeného přes `editLinked`, jinak formulář kontaktu. Vizitka si blok vybere sama (`data-md-no-select`), aby fokus zůstal v otevřeném panelu. Čtenářský panel se zmínkou je pro čtení.
- `remove(contact, { signal }) → boolean`: zeptá se před tlačítkem „Odebrat“ ve správě kontaktů, jen u kontaktu s `objectId`. Odebere se jen při `true`.
