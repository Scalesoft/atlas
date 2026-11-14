# Falešný poskytovatel AI

`node tests/browser/fake-ai/server.mjs` spustí OpenAI kompatibilní server na portu `PORT` (výchozí 1234). Nikdy nevolá skutečný model. `node --test tests/browser/fake-ai/server.test.mjs` ověří jeho HTTP kontrakt.

Embeddingy jsou deterministické trigramové vektory. `POST /__meaning` přijímá `{ rules: [{ contains, as, near? }] }` pro simulaci významové shody. Chatový test spojení vrací `{ ok: true }`; požadavek s bloky `<zdroj n="…">` v poslední uživatelské zprávě vrací první věty prvních dvou zdrojů a jejich čísla. Funguje nativní JSON schéma i schéma v promptu.

`POST /__mode` nastavuje nezávisle `chat`, `embeddings` a `models`: `healthy`, `slow`, `timeout`, `error`, `invalid-json`. Hodnota může být řetězec nebo `{ mode, delayMs }`. Pole `mode` nastaví všechny profily. Pole `answer` nastaví `answerable`, `unanswerable`, `invalid-citation` nebo `hold`.

Režim `hold` pozastaví odpověď se zdroji. `GET /__requests` umožní počkat, až požadavek dorazí; pak lze změnit režim chatu a zavolat `POST /__release`. Pozastavený požadavek vyhodnotí aktuální režim, takže lze spolehlivě vyvolat výpadek uprostřed odpovědi. Zrušený klient se ihned uvolní. Kontrolní test spojení ani embeddingy na uvolnění nečekají.

`POST /__reset` obnoví zdravý stav, odstraní pravidla, vynuluje počitadla a uvolní čekající požadavky. `GET /__requests` vrací jen počty, včetně `byPath['/v1/chat/completions']`; lze jím ověřit, že pokračování do Asistenta znovu nevolá model. Obsah, hlavičky a dotazy se neukládají ani nelogují.

Pracovní schémata vracejí deterministický návrh textu/osnovy, existující povolené štítky a prázdné seznamy pravopisných problémů či automatických záměrů. Transformace zachovává neprůhledné zástupce komponent. Shrnutí vrací tři citované věty. Tyto odpovědi ověřují aplikační tok; neměří jazykovou kvalitu modelu.
