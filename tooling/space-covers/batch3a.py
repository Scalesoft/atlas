from cover_lib import *

C = {}
M = {}


def add(cid, name, tags, hue, *parts, group='Firma', grid=False):
    C[cid] = (grid, '\n'.join(parts))
    M[cid] = (name, group, tags, hue)


def pcard(x, y, f=WH):
    return (r(x, y, 80, 64, 10, f) + c(x + 24, y + 32, 16, GF if f == WH else WH) + person(x + 24, y + 33, 0.8) +
            fr(x + 46, y + 24, 26, 6, D5) + fr(x + 46, y + 36, 20, 5))


HEART = 'M{x} {y}c-36-24-54-42-54-62 0-14 11-26 25-26 12 0 22 7 29 17 7-10 17-17 29-17 14 0 25 12 25 26 0 20-18 38-54 62z'

# 1 HR
add('ops-hr', 'Lidé a HR', 'HR, personalistika, zaměstnanci, týmy, kariéra', 350,
    p(HEART.format(x=120, y=146), f=GF), person(120, 96, 1.3),
    pcard(300, 30), pcard(390, 30, GF), pcard(480, 30), pcard(300, 104), pcard(390, 104), pcard(480, 104, GF),
    p('M456 34c-5-3-8-6-8-9a4 4 0 0 1 8-1 4 4 0 0 1 8 1c0 3-3 6-8 9z', f=D5, col=None))

# 2 onboarding
add('ops-onboarding', 'Nástup a onboarding', 'onboarding, nástup, první den, zaškolení, checklist', 160,
    r(60, 30, 150, 70, 10), c(90, 65, 18, GF), person(90, 66, 0.85), fr(118, 52, 70, 8, D5), fr(118, 68, 50, 6), sparkle(196, 40, 9, G9),
    dash('M70 176c70 0 90-44 140-46s90-48 140-50 100-28 170-30'),
    check(110, 170, 12), check(210, 130, 12), c(350, 80, 13, WH, 3), fc(350, 80, 5, D5), c(470, 54, 10),
    p('M540 72V18', sw=3), p('M540 20h40l-10 12 10 12h-40z', f=D5))

# 3 recruitment
add('ops-recruitment', 'Nábor a výběr', 'nábor, výběrová řízení, kandidáti, pohovory, CV', 30,
    c(70, 100, 20), person(70, 101, 0.9), c(120, 100, 20, GF), person(120, 101, 0.9), c(170, 100, 20), person(170, 101, 0.9, G9),
    p('M200 100h56'), arrowhead(258, 100, 0),
    r(280, 24, 170, 176, 8), r(296, 40, 50, 56, 6, GF), person(321, 70, 1.1),
    fr(358, 46, 74, 8, D5), fr(358, 62, 56, 6), fr(358, 76, 64, 6),
    fr(296, 112, 136, 6), fr(296, 126, 110, 6), fr(296, 140, 124, 6), fr(296, 160, 50, 8, G9),
    c(452, 96, 36, 'none', 4), p('M478 122l30 30', sw=9),
    star(530, 66, 11), star(556, 66, 11), star(582, 66, 11, GB))

# 4 training
add('ops-training', 'Školení a vzdělávání', 'školení, vzdělávání, kurzy, e-learning, certifikace', 210,
    p('M80 90v30c0 12 18 20 40 20s40-8 40-20V90', f=GF),
    p('M120 48l72 28-72 28-72-28z', f=D5), p('M192 76v40', sw=2), fc(192, 120, 5, K),
    p('M300 160l-20 46M460 160l20 46M380 160v40', sw=3),
    r(260, 20, 240, 140, 8),
    p('M284 140h190M284 140V40', col=G9), fr(296, 104, 18, 36, GB, 2), fr(322, 86, 18, 54, G9, 2), fr(348, 96, 18, 44, GB, 2), fr(374, 64, 18, 76, D5, 2),
    p('M410 120l20-26 18 10 22-40', sw=2.5), fr(410, 40, 60, 7, D5),
    r(522, 160, 76, 18, 3, GF), r(528, 142, 66, 18, 3), r(518, 124, 72, 18, 3, D5))

# 5 meetings
add('ops-meetings', 'Porady a schůzky', 'porady, schůzky, zápisy, agenda, meeting', 45,
    bubble(60, 34, 140, 46, GF), fr(76, 50, 80, 7, D5), fr(76, 62, 56, 6),
    bubble(96, 100, 120, 42, WH, 'right'), fr(112, 114, 70, 7, G9), fr(112, 126, 50, 6),
    r(320, 30, 40, 22, 8), r(390, 30, 40, 22, 8), r(460, 30, 40, 22, 8),
    r(320, 168, 40, 22, 8), r(390, 168, 40, 22, 8), r(460, 168, 40, 22, 8),
    r(264, 88, 22, 44, 8), r(534, 88, 22, 44, 8),
    r(290, 58, 240, 104, 52, GF),
    r(330, 80, 44, 30, 4), fr(338, 88, 28, 5), r(420, 110, 50, 30, 4), fr(428, 118, 30, 5, D5),
    c(470, 82, 9), c(350, 132, 9))

# 6 decisions
add('ops-decisions', 'Rozhodnutí a hlasování', 'rozhodnutí, hlasování, schvalování, varianty, ADR', 260,
    r(98, 40, 54, 64, 4), p('M112 72l8 8 16-18', sw=3),
    r(70, 90, 110, 80, 6, GF), fr(96, 90, 58, 6, K, 2), fr(100, 130, 50, 8, G9),
    fr(414, 24, 12, 180, D5, 3),
    p('M430 44h110l20 22-20 22H430z', f=WH), fr(444, 60, 70, 8, D5),
    p('M410 104H300l-20 22 20 22h110z', f=GF), fr(312, 120, 76, 8, G9),
    p('M430 160h90l16 16-16 16h-90z', f=WH), fr(444, 172, 50, 7))

# 7 strategy
add('ops-strategy', 'Strategie a cíle', 'strategie, vize, cíle, směřování, plánování', 140,
    c(110, 100, 48, GF), c(110, 100, 36), p('M110 64l10 36h-20z', f=D5), p('M110 136l10-36h-20z', f=WH), fc(110, 100, 4, K),
    p('M110 46v6M110 148v6M56 100h6M158 100h6', sw=3),
    p('M420 196l90-100 90 100z', f=WH),
    p('M240 196L380 44l140 152z', f=GF),
    p('M380 44l-28 30 14-6 14 12 14-12 14 6z', f=WH),
    p('M380 44V10', sw=3), p('M380 12h32l-8 9 8 9h-32z', f=D5),
    dash('M300 196l40-36-22-20 42-28-12-26'))

# 8 roadmap
add('ops-roadmap', 'Roadmapa a milníky', 'roadmapa, milníky, harmonogram, plán, termíny', 200,
    p('M40 120h536', sw=3), arrowhead(578, 120, 0, 10),
    p('M100 84v26M340 84v26M220 130v24M460 130v24', col=G9),
    r(60, 40, 80, 44, 8, GF), fr(72, 54, 50, 7, D5), fr(72, 68, 36, 5),
    r(300, 40, 80, 44, 8), fr(312, 54, 50, 7, G9), fr(312, 68, 36, 5),
    r(180, 154, 80, 40, 8), fr(192, 166, 50, 7, D5), r(420, 154, 80, 40, 8), fr(432, 166, 44, 7, GB),
    p('M100 108l12 12-12 12-12-12z', f=D5), p('M220 108l12 12-12 12-12-12z', f=D5),
    p('M340 108l12 12-12 12-12-12z', f=WH), p('M460 108l12 12-12 12-12-12z', f=WH),
    p('M550 120V62', sw=3), p('M550 64h30l-8 9 8 9h-30z', f=D5))

# 9 budget / savings
add('ops-budget', 'Rozpočet a úspory', 'rozpočet, úspory, náklady, plánování financí, cashflow', 340,
    r(60, 70, 140, 64, 6, GF), r(50, 86, 140, 64, 6), c(120, 118, 16, GF), fr(64, 112, 20, 12, GE, 3), fr(156, 112, 20, 12, GE, 3),
    r(360, 164, 24, 30, 6, GF), r(450, 164, 24, 30, 6, GF),
    p('M326 112c-16-6-22 8-12 14s12-10 0-16', sw=2.5),
    p('M462 66l16-24 10 30z', f=GF),
    '<ellipse cx="420" cy="122" rx="94" ry="62" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/>',
    '<ellipse cx="514" cy="122" rx="14" ry="19" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>',
    fc(510, 116, 2.5, K), fc(510, 128, 2.5, K), fc(482, 100, 4, K),
    fr(396, 60, 48, 8, K, 4),
    c(420, 28, 15, D5), c(420, 28, 8, 'none', 2, WH))

# 10 invoicing
btns = ''.join(fr(486 + i * 22, 98 + j * 22, 16, 14, D5 if (i, j) == (3, 2) else GE, 3) for i in range(4) for j in range(3))
add('ops-invoicing', 'Fakturace', 'fakturace, faktury, platby, pohledávky, účetní doklady', 120,
    c(100, 86, 34, GF), c(100, 86, 24), fr(88, 82, 24, 8, D5),
    c(160, 134, 24), c(160, 134, 15, 'none', 2, G9),
    '<g transform="rotate(-8 340 120)">' + r(270, 36, 140, 166, 8, GF) + '</g>',
    r(300, 24, 140, 170, 8), fr(318, 42, 60, 9, D5), fr(318, 64, 100, 6), fr(318, 78, 84, 6), fr(318, 92, 96, 6),
    p('M316 114h108'), fr(318, 126, 40, 9, G9), fr(380, 126, 40, 9, K),
    c(410, 168, 16, 'none', 2.5, D5, '4 3'), p('M403 168l5 5 9-10', sw=2.5, col=D5),
    r(470, 40, 112, 150, 12), fr(484, 54, 84, 30, GF, 6), fr(512, 66, 48, 8, D5), btns)

# 11 payroll
add('ops-payroll', 'Mzdy a odměny', 'mzdy, výplaty, odměny, výplatní pásky, bonusy', 100,
    r(60, 70, 130, 90, 14, GF), r(130, 96, 70, 40, 10), fc(150, 116, 6, D5),
    r(300, 30, 160, 76, 6, GF), c(380, 62, 16), fr(316, 44, 24, 10, GE, 3), fr(420, 44, 24, 10, GE, 3),
    r(280, 80, 200, 104, 10), p('M282 84l98 56 98-56'),
    r(500, 50, 90, 96, 10), p('M500 76V62a12 12 0 0 1 12-12h66a12 12 0 0 1 12 12v14z', f=D5),
    fr(522, 92, 46, 26, GF, 4), fr(530, 100, 30, 10, D5, 3), check(570, 134, 10))

# 12 procurement
add('ops-procurement', 'Nákup a objednávky', 'nákup, objednávky, dodavatelé, poptávky, e-shop', 70,
    r(60, 40, 140, 130, 8), fr(76, 56, 70, 8, D5),
    r(76, 78, 12, 12, 2), fr(96, 81, 80, 6), r(76, 100, 12, 12, 2), fr(96, 103, 64, 6), r(76, 122, 12, 12, 2), fr(96, 125, 72, 6),
    p('M78 84l3 3 5-6M78 106l3 3 5-6', sw=2),
    p('M90 154c8-12 14-12 14 0s10-10 16-2 10 4 16-2', sw=2),
    r(346, 34, 50, 40, 4, WH), r(402, 22, 60, 52, 4, D5), r(466, 40, 40, 34, 4, GF),
    p('M260 40h28l30 120h176', sw=3),
    p('M300 74h236l-24 76H322z', f=GF),
    p('M330 98h188M338 122h170M380 76v72M440 76v72', col=G9),
    c(350, 180, 12, D5), c(480, 180, 12, D5))

# 13 warehouse
add('ops-warehouse', 'Sklad a zásoby', 'sklad, zásoby, inventura, čárové kódy, expedice', 40,
    *[fr(x, 60, w, 70, K, 0) for x, w in ((60, 4), (68, 2), (74, 6), (84, 2), (90, 4), (98, 6), (108, 2), (114, 4), (122, 6), (132, 2), (138, 4))],
    dash('M146 96h44', G9), p('M150 30h50a10 10 0 0 1 10 10v20a10 10 0 0 1-10 10h-24l-10 40h-20l8-40h-4z', f=GF),
    r(270, 36, 50, 32, 3, GF), r(326, 46, 40, 22, 3), r(410, 30, 60, 38, 3, GF), r(478, 44, 46, 24, 3),
    r(276, 96, 60, 32, 3), r(344, 106, 40, 22, 3, GF), r(420, 92, 50, 36, 3, GF), r(478, 104, 50, 24, 3),
    r(280, 160, 44, 28, 3, GF), r(420, 156, 70, 32, 3),
    p('M260 20v180M400 20v180M540 20v180M250 70h300M250 130h300M250 190h300', sw=4),
    fr(282, 46, 26, 6, G9), fr(424, 40, 30, 6, D5), fr(430, 102, 30, 6, D5))

# 14 quality
add('ops-quality', 'Kvalita a ISO', 'kvalita, ISO, audity, procesy, neshody, zlepšování', 150,
    r(240, 40, 120, 140, 10), check(262, 66, 9), fr(278, 63, 60, 6), check(262, 94, 9), fr(278, 91, 50, 6),
    check(262, 122, 9), fr(278, 119, 66, 6), c(262, 150, 9), fr(278, 147, 44, 6, GE),
    p('M392 132l-18 64 26-12 16 20 10-62zM448 132l18 64-26-12-16 20-10-62z', f=D5),
    c(420, 90, 50, GF), c(420, 90, 36), star(420, 90, 22),
    gear(550, 146, 18), c(550, 146, 38, 'none', 3), p('M578 174l18 18', sw=7),
    sparkle(130, 70, 18, G9), sparkle(166, 120, 9, GB), fc(100, 130, 5, D5))

# 15 risk
cells = []
shade = [WH, GF, GB, G9, D5]
for row in range(4):
    for col in range(4):
        cells.append(r(282 + col * 46, 30 + row * 40, 40, 34, 4, shade[min(4, max(0, col + (3 - row) - 1))], 1.5))
add('ops-risk', 'Rizika a kontinuita', 'rizika, řízení rizik, kontinuita, BCP, pojištění', 20,
    p('M60 110a60 60 0 0 1 120 0c-10-8-20-8-30 0-10-8-20-8-30 0-10-8-20-8-30 0-10-8-20-8-30 0z', f=GF),
    p('M120 50v-8M120 110v50c0 10-14 10-14 0', sw=3),
    p('M270 196V22M270 196h200', sw=2.5), arrowhead(270, 20, -90), arrowhead(472, 196, 0),
    *cells,
    p('M540 52l50 88H490z', f=GF), p('M540 80v28', sw=4), fc(540, 122, 4, K))

# 16 GDPR / data protection
stars = ''.join(star(420 + 96 * math.cos(math.radians(a)), 112 + 80 * math.sin(math.radians(a)), 7, G9) for a in range(0, 360, 30))
add('ops-gdpr', 'GDPR a osobní údaje', 'GDPR, osobní údaje, ochrana soukromí, souhlasy, DPO', 230,
    r(50, 50, 150, 96, 10), c(84, 84, 18, GF), person(84, 85, 0.85), fr(112, 74, 70, 7, D5),
    *[fc(116 + i * 12, 96, 4, K) for i in range(5)], fr(66, 118, 120, 8, GE),
    stars,
    p('M420 42l56 18v40c0 38-24 62-56 72-32-10-56-34-56-72V60z', f=GF),
    r(398, 70, 44, 56, 4), fr(406, 80, 28, 5, D5), fr(406, 90, 22, 5), fr(406, 100, 26, 5),
    c(440, 130, 13, D5), p('M435 129v-4a5 5 0 0 1 10 0v4', sw=2, col=WH), fr(433, 128, 14, 9, WH, 2))

# 17 policies / handbook
add('ops-policies', 'Směrnice a pravidla', 'směrnice, pravidla, interní předpisy, příručka, postupy', 280,
    c(70, 60, 10, D5), fr(90, 56, 80, 7), c(70, 92, 10, G9), fr(90, 88, 64, 7), c(70, 124, 10, GB), fr(90, 120, 74, 7),
    r(460, 46, 26, 28, 4, D5), r(460, 82, 26, 28, 4, G9), r(460, 118, 26, 28, 4, GF),
    r(270, 24, 196, 176, 10, GF),
    r(300, 30, 160, 166, 4), fr(318, 50, 90, 10, D5), fr(318, 74, 120, 6), fr(318, 88, 104, 6), fr(318, 102, 116, 6), fr(318, 124, 90, 6), fr(318, 138, 110, 6),
    c(286, 60, 6), c(286, 112, 6), c(286, 164, 6),
    r(526, 92, 60, 18, 4, GF), fr(548, 56, 16, 36, GF, 3), r(548, 56, 16, 36, 3, GF), c(556, 44, 14, D5),
    r(520, 120, 72, 30, 4, 'none', 2, G9, '4 3'))

# 18 office / facility
add('ops-office', 'Kancelář a facility', 'kancelář, facility, vybavení, rezervace místností, údržba', 90,
    r(60, 30, 120, 90, 6, GF), p('M120 30v90M60 75h120'), fc(150, 54, 10, GB),
    p('M86 150c10-20 30-20 40 0', sw=2.5, col=G9),
    p('M264 92h28l-4 28h-20z', f=D5),
    p('M278 92c-2-18-14-24-22-24 2 12 10 20 22 24zM278 92c2-20 14-28 24-28-2 16-12 24-24 28z', f=GF),
    r(310, 40, 136, 80, 8), fr(322, 52, 112, 56, GF, 3), fr(334, 64, 60, 7, D5), fr(334, 78, 80, 6),
    p('M378 120v10', sw=3),
    fr(490, 114, 40, 8, D5, 3), p('M510 114l-14-40 30-22', sw=3), p('M520 46l30 14-14 18-26-10z', f=D5),
    r(250, 128, 310, 16, 4, GF), p('M272 144v56M538 144v56', sw=4))

# 19 travel
add('ops-travel', 'Služební cesty', 'služební cesty, cestovní náhrady, letenky, ubytování', 210,
    p('M50 60l40-14 40 14 40-14v100l-40 14-40-14-40 14z', f=GF), p('M90 46v100M130 60v100', col=G9),
    p('M110 96c-10-12-16-18-16-26a16 16 0 0 1 32 0c0 8-6 14-16 26z', f=D5), fc(110, 70, 5, WH),
    dash('M190 150c60-70 180-100 290-90'),
    r(330, 70, 150, 110, 14, GF), p('M380 70V54a10 10 0 0 1 10-10h30a10 10 0 0 1 10 10v16', sw=4),
    p('M372 72v106M438 72v106', col=G9, sw=3), c(458, 98, 9, D5), fc(350, 188, 7, K), fc(460, 188, 7, K),
    '<g transform="translate(530 58) rotate(-15)">' +
    p('M-6 -4l-14-26h10l24 26zM-6 5l-14 24h10l24-24zM-44 -5l-8-14h8l12 14z', f=D5) +
    p('M-44 -5h74c8 0 14 3 14 5s-6 5-14 5h-74z', f=WH) + '</g>')

# 20 events / teambuilding
balloon = lambda cx, cy, rx, ry, f: (f'<ellipse cx="{cx}" cy="{cy}" rx="{rx}" ry="{ry}" fill="{f}" stroke="{K}" stroke-width="2"/>' +
                                    p(f'M{cx - 5} {cy + ry + 7}l5-7 5 7z', f=f))
add('ops-events', 'Akce a teambuilding', 'firemní akce, teambuilding, oslavy, večírky, výročí', 330,
    p('M40 22c60 30 140 30 200 0', col=G9),
    *[p(f'M{x} {y}l12 0-6 15z', f=f) for x, y, f in ((70, 32, D5), (106, 38, GF), (142, 39, D5), (178, 36, GF), (210, 30, D5))],
    *[fr(x, y, 8, 8, f, 2, f' transform="rotate({a} {x + 4} {y + 4})"') for x, y, f, a in ((70, 90, G9, 20), (140, 130, D5, -15), (200, 80, GB, 40), (96, 150, GB, 10), (230, 150, G9, -30))],
    fc(170, 104, 4, D5), fc(60, 130, 4, GB),
    r(260, 60, 140, 120, 10), p('M260 88V72a12 12 0 0 1 12-12h116a12 12 0 0 1 12 12v16z', f=D5),
    star(330, 132, 30, GF, 2),
    p('M480 102c-6 30 10 50 0 90M530 120c4 26-14 44-6 72M440 134c0 24 16 36 14 58', col=G9, sw=1.5),
    balloon(440, 100, 24, 30, WH), balloon(530, 84, 26, 32, GF), balloon(480, 62, 28, 36, D5))
