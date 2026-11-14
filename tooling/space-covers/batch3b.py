from cover_lib import *

C = {}
M = {}


def add(cid, name, tags, hue, *parts, group='Firma', grid=False):
    C[cid] = (grid, '\n'.join(parts))
    M[cid] = (name, group, tags, hue)


def mountain(x, y, w, h, col=G9):
    return p(f'M{x} {y + h}l{w * 0.25} {-h * 0.6} {w * 0.15} {h * 0.35} {w * 0.12} {-h * 0.15} {w * 0.48} {h * 0.4}', col=col)


# 21 internal communication
add('ops-communication', 'Interní komunikace', 'interní komunikace, novinky, intranet, oznámení, zpravodaj', 190,
    '<g transform="rotate(-4 130 90)">' + r(70, 40, 120, 100, 6, GF) + fr(86, 62, 70, 8, D5) + fr(86, 78, 86, 6) + fr(86, 92, 76, 6) + fr(86, 106, 60, 6) + '</g>',
    c(130, 44, 8, D5),
    r(250, 20, 230, 180, 12),
    c(274, 44, 12, GF), person(274, 45, 0.6), fr(294, 38, 80, 7, D5), fr(294, 50, 50, 5),
    fr(262, 66, 206, 50, GF, 6), mountain(268, 72, 190, 40),
    p('M268 132c-4-3-6-5-6-7a3 3 0 0 1 6-1 3 3 0 0 1 6 1c0 2-2 4-6 7z', f=D5, col=None), fr(282, 126, 30, 5),
    c(274, 160, 12, GF), person(274, 161, 0.6), fr(294, 154, 100, 7, G9), fr(294, 166, 70, 5),
    p('M524 114h40l-6-10V86a14 14 0 0 0-28 0v18z', f=GF), p('M538 120a6 6 0 0 0 12 0'), c(560, 74, 8, D5, 2, WH),
    p('M512 76c-6 10-6 22 0 32M576 76c6 10 6 22 0 32', col=G9))

# 22 feedback / NPS
add('ops-feedback', 'Zpětná vazba', 'zpětná vazba, hodnocení, NPS, ankety, spokojenost', 50,
    bubble(50, 36, 160, 56, WH), fr(66, 52, 100, 7, D5), fr(66, 66, 70, 6),
    bubble(80, 110, 130, 44, GF, 'right'),
    *[p(f'M{x} 136c-5-3-8-6-8-9a4 4 0 0 1 8-1 4 4 0 0 1 8 1c0 3-3 6-8 9z', f=D5, col=None) for x in (104, 126, 148)],
    *[star(300 + i * 54, 62, 22, D5 if i < 4 else GF, 2) for i in range(5)],
    c(338, 140, 26), fc(330, 134, 3, K), fc(346, 134, 3, K), p('M326 146c6 8 18 8 24 0', sw=2.5),
    c(418, 140, 26, GF), fc(410, 134, 3, K), fc(426, 134, 3, K), p('M406 150h24', sw=2.5),
    c(498, 140, 26), fc(490, 134, 3, K), fc(506, 134, 3, K), p('M486 154c6-8 18-8 24 0', sw=2.5))

# 23 customer journey
add('ops-customer-journey', 'Zákaznická cesta', 'zákazníci, zákaznická zkušenost, customer success, péče, retence', 15,
    dash('M60 150C130 150 130 70 200 70S270 150 360 150 450 70 520 70'),
    c(60, 150, 24), person(60, 151, 0.9),
    c(200, 70, 24, GF), p('M186 62h5l4 14h14l4-10h-19', sw=2), fc(197, 82, 2.5, K), fc(208, 82, 2.5, K),
    c(360, 150, 24), r(348, 140, 24, 16, 4, GF), p('M352 156l-2 6 8-6', f=GF),
    c(520, 70, 26, D5), p('M520 82c-8-5-12-9-12-13a6 6 0 0 1 12-2 6 6 0 0 1 12 2c0 4-4 8-12 13z', f=WH, col=None),
    sparkle(560, 120, 10, G9), sparkle(270, 40, 8, GB), fc(440, 170, 4, GB))

# 24 CRM / contacts
add('ops-crm', 'CRM a kontakty', 'CRM, kontakty, adresář, klienti, příležitosti', 255,
    p('M80 50c-10 10-10 30 10 54s44 34 56 24l8-8-20-20-10 8c-10-4-22-16-26-26l8-10-20-20z', f=GF),
    p('M150 60a30 30 0 0 1 24 24M152 40a50 50 0 0 1 42 42', col=G9, sw=2.5),
    *[r(470, 40 + i * 28, 18, 22, 4, (D5, GF, WH, GF, WH)[i]) for i in range(5)],
    r(300, 24, 176, 176, 10, GF), r(300, 24, 22, 176, 6, D5),
    fr(340, 44, 90, 9, G9), fr(340, 62, 70, 6),
    r(380, 100, 186, 88, 10), c(414, 136, 20, GF), person(414, 137, 0.95),
    fr(446, 118, 90, 8, D5), fr(446, 134, 70, 6), fr(446, 148, 80, 6), fr(446, 164, 40, 8, GE))

# 25 quotes / offers
add('ops-quotes', 'Nabídky a poptávky', 'nabídky, poptávky, ceník, kalkulace, obchodní případy', 85,
    c(110, 92, 32, GF), p('M98 106l24-28', sw=3), c(100, 82, 5), c(120, 102, 5),
    fc(70, 150, 4, G9), sparkle(170, 46, 9, GB),
    r(270, 24, 160, 176, 8), fr(288, 42, 70, 9, D5), fr(288, 60, 40, 6, G9),
    *[fr(288, y, 80, 6) + fr(390, y, 24, 6, G9) for y in (84, 100, 116, 132)],
    p('M286 150h128'), fr(288, 162, 40, 9, G9), fr(374, 162, 40, 9, D5),
    p('M430 40c20 0 30 10 40 22', col=G9),
    p('M470 60h60l16 18-16 18h-60z', f=GF), c(532, 78, 5), fr(482, 74, 36, 8, D5),
    dash('M470 170c30 0 50-10 66-24', G8),
    p('M590 128l-60 22 28 6z', f=WH), p('M590 128l-32 28 4 18z', f=GF))

# 26 product management
add('ops-product', 'Produktový management', 'produkt, backlog, požadavky, priority, roadmapa produktu', 300,
    fr(50, 50, 40, 12, D5), fr(98, 52, 80, 8), fr(50, 76, 40, 12, G9), fr(98, 78, 64, 8),
    fr(50, 102, 40, 12, GB), fr(98, 104, 72, 8), fr(50, 128, 40, 12, GE), fr(98, 130, 50, 8),
    '<g transform="rotate(-8 270 68)">' + r(240, 40, 60, 56, 4, GF) + fr(250, 54, 36, 6, D5) + fr(250, 66, 28, 5) + '</g>',
    '<g transform="rotate(6 280 138)">' + r(250, 110, 60, 56, 4) + fr(260, 124, 36, 6, G9) + fr(260, 136, 30, 5) + '</g>',
    p('M420 40l70 30-70 30-70-30z', f=GF), p('M350 70v74l70 30v-74z', f=WH), p('M490 70v74l-70 30v-74z', f=GB),
    p('M384 55l70 30', col=G9, sw=8), p('M372 112l30 13', col=G9, sw=3),
    '<g transform="rotate(5 550 88)">' + r(520, 60, 62, 58, 4) + fr(520, 60, 62, 12, D5, 0) + fr(530, 82, 40, 6) + fr(530, 94, 30, 5) + '</g>',
    sparkle(540, 160, 10, G9))

# 27 research / analysis
add('ops-research', 'Výzkum a analýzy', 'výzkum, analýzy, průzkumy, persony, rozhovory', 230,
    r(60, 40, 110, 130, 6), *[c(80 + i * 18, 40, 5, GF) for i in range(5)],
    fr(76, 62, 70, 7, D5), fr(76, 78, 80, 5), fr(76, 90, 60, 5), fr(76, 102, 74, 5), fr(76, 114, 50, 5),
    '<g transform="rotate(30 170 140)">' + r(160, 90, 14, 70, 3, GF) + p('M160 160l7 14 7-14z', f=WH) + '</g>',
    r(250, 30, 150, 156, 10), c(325, 70, 26, GF), person(325, 71, 1.15),
    fr(280, 108, 90, 8, D5), fr(290, 124, 70, 6), fr(268, 146, 40, 12, GE), fr(314, 146, 50, 12, GB), fr(270, 164, 60, 12, GE),
    p('M430 176h150M430 176V60', col=G9),
    fr(442, 136, 18, 40, GB, 2), fr(468, 110, 18, 66, G9, 2), fr(494, 124, 18, 52, GB, 2), fr(520, 90, 18, 86, D5, 2), fr(546, 116, 18, 60, GB, 2),
    c(500, 100, 34, 'none', 4), p('M524 124l26 26', sw=8))

# 28 content
add('ops-content', 'Obsah a texty', 'obsah, copywriting, články, redakce, publikace', 20,
    r(60, 40, 130, 120, 10), p('M60 68V52a12 12 0 0 1 12-12h106a12 12 0 0 1 12 12v16z', f=D5),
    *[fr(72 + col * 22, 80 + row * 22, 16, 16, D5 if (row, col) in ((0, 2), (2, 4)) else (G9 if (row, col) == (1, 1) else GE), 3) for row in range(3) for col in range(5)],
    r(260, 24, 220, 176, 8), fr(278, 40, 120, 10, D5),
    fr(276, 60, 188, 52, GF, 6), mountain(282, 66, 170, 42),
    fr(278, 126, 180, 6), fr(278, 140, 160, 6), fr(278, 154, 172, 6), fr(278, 168, 110, 6),
    '<g transform="translate(520 110) rotate(35)">' + r(-10, -70, 20, 106, 3, GF) + r(-10, -84, 20, 14, 3, D5) +
    p('M-10 36l10 24 10-24z', f=WH) + p('M-3 52l3 8 3-8z', f=K, col=None) + '</g>')

# 29 brand
add('ops-brand', 'Značka a vizuální styl', 'značka, brand, logo, vizuální styl, grafický manuál', 320,
    fr(60, 52, 120, 16, D5, 4), fr(60, 80, 90, 10, G9, 4), fr(60, 100, 110, 6), fr(60, 112, 96, 6), fr(60, 124, 104, 6),
    p('M56 150h130', col=GD), p('M56 160h130', col=GD),
    p('M300 100h220M410 20v170', col=GD, dash='4 4'),
    c(380, 100, 50, 'none', 2, G9, '4 4'), c(440, 100, 50, 'none', 2, G9, '4 4'),
    c(410, 100, 34, D5), p('M396 112c0-18 10-28 28-30-2 18-12 28-28 30z', f=WH, col=None),
    r(540, 46, 36, 36, 8, D5), r(540, 92, 36, 36, 8, G9), r(540, 138, 36, 36, 8, GF))

# 30 org chart
add('ops-org-chart', 'Organizační struktura', 'organizační struktura, oddělení, vedení, role, odpovědnosti', 175,
    p('M110 40l64 116H46z', f=GF), p('M80 96h60M64 126h92', col=G9), fc(110, 72, 6, D5),
    p('M420 60v15M300 75h240M300 75v15M420 75v15M540 75v15M300 130v10M250 140h72M250 140v10M322 140v10M540 130v10M490 140h72M490 140v10M562 140v10', col=G9),
    r(370, 20, 100, 40, 8, D5), c(392, 40, 10, WH, 0), person(392, 41, 0.5, D5), fr(408, 36, 46, 7, WH),
    *[r(x, 90, 100, 40, 8) + c(x + 22, 110, 10, GF) + person(x + 22, 111, 0.5) + fr(x + 38, 106, 46, 7, G9) for x in (250, 370, 490)],
    *[r(x - 26, 150, 52, 30, 6, GF) + fr(x - 16, 162, 32, 6) for x in (250, 322, 490, 562)])

# 31 sales pipeline
add('ops-pipeline', 'Obchodní pipeline', 'pipeline, příležitosti, prodejní fáze, forecast, konverze', 35,
    r(50, 50, 150, 96, 10), fr(66, 66, 80, 8, D5), fr(66, 82, 60, 6), fr(66, 108, 118, 8, GE), fr(66, 108, 80, 8, D5), fr(150, 124, 34, 8, G9),
    p('M250 30L330 44V176L250 190z', f=WH), p('M334 44L414 58V162L334 176z', f=GF),
    p('M418 58L498 72V148L418 162z', f=GB), p('M502 72L582 86V134L502 148z', f=D5),
    *[c(x, y, 7, WH) for x, y in ((276, 70), (304, 92), (274, 118), (302, 142), (282, 164))],
    *[c(x, y, 7, WH) for x, y in ((362, 86), (388, 112), (360, 140))],
    c(446, 100, 7, WH), c(470, 124, 7, WH), c(542, 110, 9, WH))

# 32 partners / suppliers
add('ops-partners', 'Partneři a dodavatelé', 'partneři, dodavatelé, spolupráce, smlouvy, outsourcing', 125,
    r(70, 60, 70, 104, 4, GF), *[fr(82 + i * 18, 74 + j * 22, 10, 12, WH, 2) for i in range(3) for j in range(3)], fr(96, 140, 18, 24, D5, 2),
    dash('M140 110h150'), dash('M500 112h30'),
    p('M300 60h100v34a17 17 0 1 1 0 32v34H300z', f=GB),
    p('M400 60h100v100H400v-34a17 17 0 1 0 0-32z', f=WH),
    fr(318, 140, 40, 8, D5), fr(440, 140, 40, 8, G9),
    r(530, 84, 64, 96, 4), *[fr(540 + i * 18, 96 + j * 22, 10, 12, GF, 2) for i in range(3) for j in range(3)])

# 33 tenders
add('ops-tenders', 'Výběrová řízení', 'výběrová řízení, tendry, zakázky, nabídky, hodnocení', 270,
    *[r(60, 44 + i * 30, 14, 14, 3) + fr(84, 47 + i * 30, (90, 70, 84, 60)[i], 7, GB) for i in range(4)],
    p('M63 51l4 4 6-7M63 81l4 4 6-7', sw=2),
    r(276, 46, 150, 150, 6, GF), r(290, 36, 150, 150, 6), fr(306, 56, 80, 9, D5), fr(306, 76, 110, 6), fr(306, 90, 96, 6),
    fr(306, 140, 110, 6), fr(306, 154, 80, 6),
    fr(358, 36, 16, 150, D5, 0), c(366, 112, 20, D5, 2, WH), star(366, 112, 10, WH),
    r(476, 172, 110, 16, 4, GF),
    '<g transform="rotate(-35 530 100)">' + r(524, 98, 12, 76, 4, GF) + r(492, 70, 76, 34, 6, D5) + fr(500, 70, 6, 34, G9, 0) + fr(554, 70, 6, 34, G9, 0) + '</g>')

# 34 management reporting
add('ops-mgmt-reporting', 'Reporting pro vedení', 'reporting, vedení, výsledky, měsíční report, board', 220,
    p('M60 160l50-50 30 30 64-72', sw=6), arrowhead(206, 66, -48, 14, K, 6),
    r(270, 24, 170, 176, 8), fr(288, 42, 90, 10, D5), fr(288, 60, 60, 6),
    c(355, 128, 36, 'none', 16, GE), p('M355 92a36 36 0 0 1 34.2 47.1', col=D5, sw=16),
    r(470, 40, 120, 54, 8), p('M486 76l12-14 12 14', sw=3), fr(520, 56, 54, 9, D5), fr(520, 72, 40, 6),
    r(470, 106, 120, 54, 8, GF), p('M486 124l12 14 12-14', sw=3), fr(520, 122, 54, 9, G9), fr(520, 138, 40, 6))

# 35 KPI scorecard
rows = []
for i in range(4):
    y = 74 + i * 28
    rows.append(fr(266, y, 110, 7, GB) + fr(392, y, 60, 7, G9) + fc(484, y + 3, 7, (D5, GE, G9, D5)[i]) +
                p(f'M520 {y + 7}l8-8 8 8' if i != 1 else f'M520 {y - 1}l8 8 8-8', sw=2.5))
add('ops-kpi', 'KPI a výsledky', 'KPI, metriky, scorecard, výkonnost, cíle', 140,
    p('M90 50h60v30a30 30 0 0 1-60 0z', f=GF), p('M90 58H76a14 14 0 0 0 14 22M150 58h14a14 14 0 0 1-14 22', sw=2.5),
    p('M120 110v20', sw=3), r(96, 130, 48, 14, 4, D5), star(120, 74, 12, D5),
    r(250, 30, 320, 160, 10), p('M250 58V42a12 12 0 0 1 12-12h296a12 12 0 0 1 12 12v16z', f=D5),
    fr(266, 40, 60, 8, WH), fr(392, 40, 40, 8, WH), *rows)

# 36 time tracking
cells = ''.join(fr(212 + i * 28, 72 + j * 20, 20, 12, (D5 if (i + j) % 4 == 0 else (GB if (i + j) % 3 == 0 else GE)), 3) for i in range(5) for j in range(5))
ticks = ''.join(f'M{n(450 + 50 * math.cos(math.radians(a)))} {n(116 + 50 * math.sin(math.radians(a)))}L{n(450 + 56 * math.cos(math.radians(a)))} {n(116 + 56 * math.sin(math.radians(a)))}' for a in range(0, 360, 30))
add('ops-time-tracking', 'Výkazy a docházka', 'výkazy práce, docházka, timesheety, čas, projekty', 60,
    r(200, 34, 160, 150, 8), fr(212, 46, 70, 9, D5), cells,
    r(440, 36, 20, 14, 3, D5), p('M450 50v6', sw=3), '<g transform="rotate(40 450 116)">' + r(444, 44, 12, 14, 3, GF) + '</g>',
    c(450, 116, 62, GF), p(ticks, sw=2.5), p('M450 116l26-30', sw=4), p('M450 116v-36', sw=2, col=G9), fc(450, 116, 6, K),
    sparkle(90, 80, 14, GB), fc(130, 130, 5, G9))

# 37 vacation
add('ops-vacation', 'Dovolená a absence', 'dovolená, absence, volno, nemoc, plánování směn', 55,
    r(60, 40, 140, 120, 10), p('M60 68V52a12 12 0 0 1 12-12h116a12 12 0 0 1 12 12v16z', f=D5),
    *[fr(74 + i * 24, 82 + j * 24, 16, 16, GE, 3) for i in range(5) for j in range(3)],
    p('M100 110l12 12M112 110l-12 12M124 110l12 12M136 110l-12 12M148 110l12 12M160 110l-12 12', sw=2.5),
    '<ellipse cx="400" cy="200" rx="100" ry="18" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/>',
    p('M398 196c-2-50 6-90 24-124', sw=8, col=D5),
    p('M422 72c-20-22-52-22-72-4 24-4 48 0 72 4zM422 72c22-22 54-20 72 0-24-6-48-4-72 0zM422 72c-8-26-30-40-56-38 22 8 40 20 56 38zM422 72c10-26 32-38 58-34-22 8-42 18-58 34z', f=GF),
    c(520, 62, 22, GF), p(''.join(f'M{n(520 + 30 * math.cos(math.radians(a)))} {n(62 + 30 * math.sin(math.radians(a)))}L{n(520 + 40 * math.cos(math.radians(a)))} {n(62 + 40 * math.sin(math.radians(a)))}' for a in range(0, 360, 45)), sw=3, col=G9),
    p('M470 170c10-8 20-8 30 0s20 8 30 0 20-8 30 0', col=G9, sw=2.5))

# 38 benefits
add('ops-benefits', 'Benefity', 'benefity, odměny, stravenky, příspěvky, zaměstnanecké výhody', 345,
    r(60, 60, 140, 80, 10), r(70, 70, 120, 60, 6, 'none', 1.5, G9, '4 3'),
    p('M130 116c-12-8-18-14-18-20a8 8 0 0 1 18-3 8 8 0 0 1 18 3c0 6-6 12-18 20z', f=D5, col=None),
    sparkle(210, 50, 10, G9), sparkle(230, 150, 7, GB),
    p('M400 70c-20-30-50-20-30 0zM400 70c20-30 50-20 30 0z', f=D5),
    r(330, 92, 140, 100, 6, GF), r(320, 70, 160, 26, 6), fr(392, 70, 16, 122, D5, 0),
    p('M518 96c-6-8 6-12 0-20M538 96c-6-8 6-12 0-20', col=G9, sw=2.5),
    p('M500 108h60v40a24 24 0 0 1-24 24h-12a24 24 0 0 1-24-24z', f=WH), p('M560 118h8a12 12 0 0 1 0 24h-8', sw=2.5))

# 39 safety (BOZP)
add('ops-safety', 'BOZP a bezpečnost práce', 'BOZP, bezpečnost práce, školení BOZP, ochranné pomůcky, první pomoc', 30,
    p('M134 52c20 0 26 20 22 40', sw=3), p('M104 56h30l8-8', sw=3),
    r(90, 70, 44, 100, 16, GF), fr(104, 56, 16, 14, D5, 3), fr(98, 100, 28, 26, WH, 4), fr(104, 108, 16, 4, G9),
    p('M340 130a80 72 0 0 1 160 0z', f=GF), p('M420 58v72M384 68v62M456 68v62', col=G9),
    r(318, 126, 204, 16, 8, D5),
    p('M544 100v-10h28v10', sw=3), r(518, 100, 80, 62, 8), p('M552 116h12v10h10v12h-10v10h-12v-10h-10v-12h10z', f=D5))

# 40 sustainability
arcs = []
for k in range(3):
    a0 = -90 + 120 * k + 12
    a1 = a0 + 92
    x0, y0 = 130 + 46 * math.cos(math.radians(a0)), 100 + 46 * math.sin(math.radians(a0))
    x1, y1 = 130 + 46 * math.cos(math.radians(a1)), 100 + 46 * math.sin(math.radians(a1))
    arcs.append(p(f'M{n(x0)} {n(y0)}A46 46 0 0 1 {n(x1)} {n(y1)}', sw=7, col=D5) + arrowhead(x1, y1, a1 + 90, 13, D5, 7))
add('ops-sustainability', 'Udržitelnost a ESG', 'udržitelnost, ESG, ekologie, recyklace, uhlíková stopa', 130,
    *arcs,
    p('M420 196c-60-40-72-120 8-172 72 58 52 140-8 172z', f=GF),
    p('M420 196c0-50 4-100 8-160M421 140l-24-18M422 110l26-20M424 80l-18-14', sw=2),
    p('M520 150h50l-6 40h-38z', f=D5), p('M545 150v-40'),
    p('M545 124c-16-2-24-12-26-24 14 2 24 10 26 24zM545 114c14-4 22-16 22-28-14 4-22 14-22 28z', f=GF))
