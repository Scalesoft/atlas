# Jak pracovat s asistentem a návrhy

Atlas pomáhá hledat v publikované wiki, shrnovat stránky a připravovat návrhy úprav. Dostupné akce závisí na nastavení správce a vašich oprávněních. Běžné hledání, psaní, ukládání i publikování fungují také bez AI.

## Najít odpověď

Otevřete společné hledání (Ctrl+K) a napište dotaz. Režim Hledání / Zeptat se zvolíte tlačítkem; vaše slova i výběr textu zůstanou zachované. Tab a Shift+Tab přesouvají fokus obvyklým směrem. Přepnutí samo model nespouští; v režimu Zeptat se odpověď vyžádá Enter, z hledání také Ctrl+Enter nebo tlačítko pro odpověď z wiki. Nejprve se objeví podklady, potom ověřené věty s čísly zdrojů. Citaci lze otevřít i klávesnicí a zkontrolovat původní stránku.

Běžná odpověď je stručná. Pro delší vysvětlení napište například „popiš podrobně celý postup krok za krokem“; asistent může odpovědět až dvanácti citovanými větami podle dostupných podkladů. Delší odpověď nezaručuje úplné pokrytí všech stránek wiki. Na jednotlivé kroky můžete navázat další otázkou.

„Pokračovat v asistentovi“ přenese stejnou konverzaci do panelu. Tam můžete položit navazující otázku a zúžit hledání na prostor nebo stránku. Své uložené konverzace najdete v profilu; můžete je jednotlivě smazat. Nejsou sdílené s kolegy.

Odpověď „Bez podkladů“ znamená, že Atlas nemá použitelnou odpověď z dostupných zdrojů. Zkuste jiná slova nebo nahlaste chybějící obsah. Hodnocení palcem a důvod pomáhají správcům najít slabá místa. I odpověď se správnou citací může být neúplná; rozhodující je zdroj.

## Číst stránku

„Shrnout“ vytvoří stručný přehled publikovaného obsahu s citacemi. U dlouhé stránky Atlas přizná, že shrnutí nepokrývá celý dokument. Vybraný text lze vysvětlit v asistentovi nebo vyhledat jeho další výskyty. Tyto akce nejsou dostupné ve veřejném sdílení a historických verzích.

Když se změní publikace nebo přístupová práva, starší odpověď může být označena jako zastaralá nebo přestane být dostupná. Atlas znovu kontroluje zdroje také při otevření historie.

## Psát s návrhy

V předvolbách profilu zvolíte asistenci zapnutou, pouze pravidla nebo vypnutou. Pravidla umějí nabídnout užitečné vložení i bez modelu. Modelové návrhy se objeví pouze tehdy, když je povolil správce.

U výběru textu lze požádat o opravu, zkrácení, přeformulování, překlad nebo vlastní úpravu. Nejdříve uvidíte náhled. Dokument se změní až tlačítkem „Použít“ a změnu lze vrátit jedním Zpět. Pokud jste původní výběr mezitím změnili, starý návrh se nepoužije. Návrh můžete upravit nebo zahodit.

Na prázdné stránce lze připravit osnovu. Před publikací může Atlas nabídnout pravopisné připomínky a popis změny. Ručně napsaný popis nepřepisuje automaticky. Navržené štítky se nejprve přidají do místního návrhu a uloží se běžným uložením. Čekání na model neblokuje Uložit ani Publikovat.

Hesla patří do správy hesel, nikoli do běžného textu. Atlas rozpoznaný citlivý text odmítne poslat modelu a obsah vložených komponent nahrazuje neprůhlednými zástupci.

## Pro správce

Administrace → AI obsahuje profily, vypínače funkcí, index a přehledy Využití/Kvalita. Nové modelové úlohy nad rozepsaným textem jsou výchozí vypnuté. Použití cloudu pro pracovní text vyžaduje samostatné povolení. Profil uvádí, zda model běží ve firmě nebo v cloudu.

Pro Chat a Rychlé úlohy lze vybrat Gateway (vlastní bránu kompatibilní s OpenAI, která obsluhuje jeden načtený model), zadat adresu jejího API (obvykle s `/v1`) a název modelu. Klíč je volitelný podle nastavení brány. Embeddingy dál používají LM Studio nebo OpenAI. Změny se projeví až po výslovném uložení; uložený klíč se při změně poskytovatele nebo původu adresy zahodí a případný nový klíč je potřeba zadat znovu.

Gateway podporuje JSON schéma předané v API pomocí zadání a ověření výstupu. Nastavení Nativní JSON schéma může zůstat zapnuté; Atlas odpověď dále ověřuje podle vlastních pravidel.

Využití ukazuje počty, latence a známé tokeny/cenu. Neznámá hodnota neznamená nulu. Kvalita zobrazuje souhrnná hodnocení, anonymní počty přijetí návrhů a odpovědi bez podkladů v dochovaných konverzacích. Nezobrazuje texty otázek, odpovědí ani soukromé poznámky.

Ladicí ověření spustí šest syntetických případů proti uloženému profilu Chat a spotřebuje tokeny. Ověřuje strukturu, citace a abstenci; není to měření skutečné kvality celé wiki. Běh lze zrušit.

Správce prostoru najde možné duplicity v nastavení prostoru → AI. Může otevřít obě stránky a potvrdit „Není duplicita“. Mezery ve znalostech umožňují vytvořit chybějící stránku nebo označit problém za vyřešený.
