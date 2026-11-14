from cover_lib import *

C = {}   # id -> (grid, body)
M = {}   # id -> (name, group, tags, hue)


def add(cid, grid, name, tags, hue, *parts, group='IT'):
    C[cid] = (grid, '\n'.join(parts))
    M[cid] = (name, group, tags, hue)


# 1 cloud
add('it-cloud', True, 'Cloud a hosting', 'cloud, hosting, Azure, AWS, škálování', 210,
    cloud(90, 110, 0.55, WH, G9, '5 5'),
    dash('M196 88h74'), arrowhead(272, 88, 0),
    cloud(330, 150, 1.0, GF),
    p('M380 130V86M368 98l12-12 12 12', sw=3), p('M430 86v44M418 118l12 12 12-12', sw=3),
    p('M348 150v26M416 150v26M484 150v26', col=G9),
    r(320, 176, 56, 26, 6), r(388, 176, 56, 26, 6), r(456, 176, 56, 26, 6),
    fc(334, 189, 3.5, D5), fc(402, 189, 3.5, D5), fc(470, 189, 3.5, G9),
    fr(344, 186, 24, 5), fr(412, 186, 24, 5), fr(480, 186, 24, 5))

# 2 network
ports = ''.join(fr(264 + i * 22, 100, 14, 12, WH, 2) for i in range(8))
add('it-network', False, 'Sítě a konektivita', 'síť, LAN, Wi-Fi, router, switch, kabeláž', 230,
    c(110, 100, 46, GF), '<ellipse cx="110" cy="100" rx="20" ry="46" fill="none" stroke="#3a3a3a" stroke-width="2"/>',
    p('M64 100h92M70 78h80M70 122h80'),
    dash('M156 108h94'),
    p('M294 70v20M380 70v20M310 136v24M390 136v24', col=G9),
    r(262, 24, 64, 42, 4), p('M254 70h80', sw=3), fr(272, 34, 30, 6, GB), fr(272, 46, 44, 6, GE),
    r(350, 22, 60, 42, 4), p('M380 64v6M368 72h24', sw=3), fr(360, 32, 40, 6, GB),
    r(250, 84, 200, 52, 8, D5), ports, fc(436, 127, 3, WH),
    p('M450 108h50'),
    r(500, 90, 90, 38, 8, GF), p('M515 90l-8-26M575 90l8-26', sw=3), fc(507, 64, 4, K), fc(583, 64, 4, K),
    fc(520, 109, 4, D5), fc(536, 109, 4, G9), fr(552, 106, 26, 6),
    r(282, 160, 56, 30, 6), r(362, 160, 56, 30, 6), fc(296, 175, 3.5, D5), fc(376, 175, 3.5, D5),
    fr(306, 172, 22, 5), fr(386, 172, 22, 5))

# 3 containers
def container(x, y, w, h, f, rib):
    ribs = ''.join(f'M{x + i} {y + 6}v{h - 12}' for i in range(12, w - 6, 12))
    return r(x, y, w, h, 4, f) + p(ribs, col=rib, sw=2)

add('it-containers', True, 'Kontejnery a Docker', 'Docker, kontejnery, image, registry, Podman', 200,
    hexagon(90, 70, 22, GF), hexagon(123, 89, 22, D5), hexagon(90, 108, 22, WH), hexagon(156, 70, 22, WH),
    p('M423 0v30M423 30l-37 22M423 30l37 22', col=K),
    container(290, 136, 86, 40, WH, GB), container(380, 136, 86, 40, GF, G9), container(470, 136, 86, 40, WH, GB),
    container(335, 94, 86, 40, GF, G9), container(425, 94, 86, 40, WH, GB),
    container(380, 52, 86, 40, D5, WH))

# 4 cluster / orchestration
nodes = []
spokes = []
for a in range(0, 360, 60):
    x = 420 + 78 * math.cos(math.radians(a)); y = 105 + 66 * math.sin(math.radians(a))
    spokes.append(f'M420 105L{n(x)} {n(y)}')
    nodes.append(c(x, y, 19) + fr(x - 6, y - 6, 12, 12, GB, 2))
add('it-cluster', False, 'Cluster a orchestrace', 'Kubernetes, cluster, uzly, orchestrace, škálování', 250,
    r(40, 40, 160, 110, 10),
    fc(60, 64, 6, D5), fr(74, 61, 90, 6), fc(60, 88, 6, D5), fr(74, 85, 70, 6), fc(60, 112, 6, G9), fr(74, 109, 100, 6),
    fr(56, 130, 50, 8, GE),
    '<ellipse cx="420" cy="105" rx="78" ry="66" fill="none" stroke="#9a9a9a" stroke-width="2" stroke-dasharray="5 5"/>',
    p(''.join(spokes), col=G9), *nodes,
    c(420, 105, 32, D5), c(420, 105, 18, 'none', 2, WH), fc(420, 105, 5, WH))

# 5 backup
add('it-backup', True, 'Zálohy a obnova', 'zálohy, obnova, disaster recovery, archiv, snapshot', 160,
    r(62, 52, 126, 24, 6, GF), r(70, 76, 110, 74, 6), fr(105, 92, 40, 8, G9),
    dash('M190 110h104'),
    r(300, 56, 150, 108, 12, GF), c(372, 110, 38), c(372, 110, 8, D5),
    p('M432 74l-34 30', sw=3), fc(432, 74, 5, K), fc(430, 148, 4, D5),
    p('M560 110a40 40 0 1 1-11.7-28.3', sw=4), p('M546 70L548 82L536 80', sw=3),
    p('M520 90v20l14 10', sw=3))

# 6 incident
add('it-incident', False, 'Incidenty a výpadky', 'incident, výpadek, eskalace, postmortem, krizový plán', 25,
    r(50, 50, 150, 72, 10), p('M95 66l18 32H77z', f=D5, sw=2), p('M95 77v10', sw=2.5, col=WH), fc(95, 92, 1.8, WH),
    fr(124, 70, 60, 7, D5), fr(124, 84, 48, 6), fr(124, 98, 56, 6),
    p('M390 20v14M332 42l10 10M448 42l-10 10M312 92h14M454 92h14', sw=3, col=G9),
    p('M350 130V92a40 40 0 0 1 80 0v38z', f=GF), p('M370 112V94a20 20 0 0 1 20-20'),
    r(334, 130, 112, 24, 6, D5), fr(350, 139, 80, 6, WH),
    r(480, 40, 116, 138, 10), p('M498 60v100', col=GD, sw=2),
    c(498, 64, 6, D5), fr(512, 61, 64, 6, G9), c(498, 96, 6, G9), fr(512, 93, 50, 6),
    c(498, 128, 6), fr(512, 125, 58, 6), c(498, 156, 6), fr(512, 153, 40, 6, GE))

# 7 logs
rows = []
lv = [GB, GB, G9, D5, GB, G9, GB, GB]
mw = [120, 90, 140, 110, 80, 130, 100, 70]
for i in range(8):
    y = 60 + i * 17
    rows.append(fr(258, y, 40, 6, GD) + fr(306, y - 1, 28, 8, lv[i]) + fr(342, y, mw[i], 6, GB))
add('it-logs', True, 'Logy a audit', 'logy, auditní stopa, události, záznamy, diagnostika', 75,
    p('M60 46h120l-44 50v44l-32 14V96z', f=GF),
    fc(90, 30, 4, G9), fc(112, 24, 4, D5), fc(134, 32, 4, GB),
    win(240, 22, 330, 176), fr(242, 107, 326, 16, GF, 0), *rows,
    c(536, 150, 28, WH, 3), p('M556 170l24 24', sw=7), fr(522, 144, 28, 5, D5), fr(522, 154, 18, 5))

# 8 performance
ticks = []
for a in range(180, 361, 30):
    ra = math.radians(a)
    ticks.append(f'M{n(420 + 84 * math.cos(ra))} {n(150 + 84 * math.sin(ra))}L{n(420 + 94 * math.cos(ra))} {n(150 + 94 * math.sin(ra))}')
add('it-performance', False, 'Výkon a optimalizace', 'výkon, rychlost, latence, optimalizace, zátěžové testy', 95,
    p('M60 60h30M50 80h40M64 100h22', col=G9, sw=3),
    p('M150 28l-46 74h34l-14 58 56-82h-36z', f=D5),
    p('M324 150a96 96 0 0 1 192 0z', f=GF),
    p(''.join(ticks), sw=3),
    p('M346 150a74 74 0 0 1 148 0', col=GE, sw=14),
    p('M346 150a74 74 0 0 1 138.1-37', col=D5, sw=14),
    p('M420 150L469 109', sw=4), fc(420, 150, 8, K),
    fr(540, 60, 60, 8, D5), fr(540, 76, 44, 6), fr(540, 90, 52, 6))

# 9 storage
def folder(x, y, f=GF):
    return p(f'M{x} {y}h20l6 7h24v27h-50z', f=f)

bays = ''.join(r(316, y, 228, 26, 5, GF) + fc(530, y + 13, 4, D5 if i != 2 else G9) + fr(330, y + 10, 90 - i * 10, 6, GB)
               for i, y in enumerate((48, 82, 116, 150)))
add('it-storage', True, 'Úložiště a soubory', 'úložiště, disky, NAS, sdílené složky, soubory', 45,
    p('M60 64v75M60 93h30M60 139h30', col=G9),
    folder(40, 30, D5), folder(90, 76), folder(90, 122, WH),
    dash('M150 100h150'),
    r(300, 30, 260, 168, 12), bays,
    fr(440, 162, 90, 8, GE), fr(440, 162, 64, 8, D5))

# 10 web hosting / domains
add('it-web', False, 'Web a domény', 'web, domény, DNS, hosting, certifikáty, URL', 185,
    r(70, 70, 110, 34, 6), r(70, 112, 110, 34, 6),
    fc(86, 87, 3.5, D5), fc(86, 129, 3.5, D5), fr(98, 84, 60, 6), fr(98, 126, 48, 6),
    dash('M180 87c40 0 50-30 70-30'),
    c(430, 110, 80, GF),
    '<ellipse cx="430" cy="110" rx="36" ry="80" fill="none" stroke="#3a3a3a" stroke-width="2"/>',
    p('M350 110h160M361 70h138M361 150h138M430 30v160'),
    r(250, 40, 180, 34, 17), fr(268, 51, 12, 12, D5, 3), fr(290, 54, 110, 6, GB),
    sparkle(560, 160, 12, G9), sparkle(530, 40, 8, GB))

# 11 email
add('it-email', False, 'E-mail a pošta', 'e-mail, pošta, Exchange, schránky, newslettery', 330,
    p('M120 70h50M100 86h56M200 140h50M190 156h40', col=GB, sw=3),
    '<g transform="rotate(-10 180 70)">' + r(150, 50, 64, 42, 6, GF) + p('M152 54l30 22 30-22') + '</g>',
    '<g transform="rotate(8 100 140)">' + r(70, 124, 54, 36, 6) + p('M72 128l25 18 25-18') + '</g>',
    r(290, 46, 200, 134, 10), p('M294 52l96 74 96-74'), p('M294 176l70-60M486 176l-70-60', col=G9),
    c(488, 50, 16, D5), fc(488, 50, 5, WH),
    r(520, 84, 80, 100, 8, GF), fr(532, 98, 56, 7, D5), fr(532, 114, 44, 6), fr(532, 130, 50, 6), fr(532, 146, 36, 6))

# 12 identity & access
add('it-iam', False, 'Identity a přístupy', 'identity, účty, role, oprávnění, SSO, Entra ID', 290,
    c(70, 80, 24, GF), person(70, 80, 0.9), c(110, 80, 24), person(110, 80, 0.9, D5), c(150, 80, 24, GB), person(150, 80, 0.9),
    fr(60, 120, 64, 8, GE), fr(130, 120, 40, 8, G9),
    r(260, 40, 220, 140, 14), p('M260 74V54a14 14 0 0 1 14-14h192a14 14 0 0 1 14 14v20z', f=D5), fr(350, 52, 40, 8, WH),
    c(310, 120, 30, GF), person(310, 120, 1.4),
    fr(358, 98, 96, 9, D5), fr(358, 116, 80, 7), fr(358, 132, 64, 7), fr(358, 152, 40, 8, GE),
    c(530, 140, 22, GF), c(530, 140, 7), p('M552 140h48M584 140v14M596 140v10', sw=5))

# 13 certificates & keys
add('it-certificates', False, 'Certifikáty a klíče', 'certifikáty, šifrování, klíče, TLS, podpisy', 50,
    c(120, 76, 28, GF), c(120, 76, 9), p('M144 90l52 30M178 110l-6 12M190 117l-6 12', sw=6),
    r(250, 30, 240, 150, 8), r(262, 42, 216, 126, 4, 'none', 2, G9),
    fr(300, 62, 140, 10, D5), fr(290, 84, 160, 6), fr(310, 98, 120, 6), fr(330, 140, 70, 4, G9),
    p('M456 176l-10 34 14-8 10 10 4-30zM484 176l10 34-14-8-10 10-4-30z', f=D5),
    c(470, 160, 26, D5), sparkle(470, 160, 12, WH))

# 14 firewall
bricks = []
for row in range(8):
    y = 30 + row * 20
    off = 0 if row % 2 == 0 else -20
    x = 330 + off
    while x < 470:
        x0 = max(x, 330); x1 = min(x + 40, 470)
        bricks.append(r(x0, y, x1 - x0, 20, 2, GF if (row + (x // 40)) % 3 == 0 else WH))
        x += 40
add('it-firewall', False, 'Firewall a ochrana sítě', 'firewall, ochrana sítě, pravidla, filtrování, DDoS', 15,
    fr(116, 62, 16, 16, D5, 3), fr(116, 102, 16, 16, G9, 3), fr(116, 142, 16, 16, GB, 3),
    p('M140 70h170M140 110h170M140 150h170', col=G9), arrowhead(312, 70, 0, 8, G9), arrowhead(312, 150, 0, 8, G9),
    *bricks,
    cross(318, 70, 10, D5), cross(318, 150, 10, D5),
    p('M480 110h90'), arrowhead(572, 110, 0), check(592, 110, 12))

# 15 VPN
add('it-vpn', True, 'VPN a vzdálený přístup', 'VPN, vzdálený přístup, home office, tunel, zabezpečené připojení', 175,
    r(56, 60, 100, 66, 6), fr(66, 70, 80, 46, GF, 3), p('M40 134h132l-8 10H48z', f=GF),
    r(474, 60, 100, 66, 6), fr(484, 70, 80, 46, GF, 3), p('M458 134h132l-8 10H466z', f=GF),
    r(176, 90, 284, 40, 20, GF), p('M200 110h236', col=D5, sw=6, dash='10 10'),
    c(318, 110, 26, D5), p('M310 106v-6a8 8 0 0 1 16 0v6', sw=3, col=WH), fr(306, 104, 24, 17, WH, 3), fc(318, 112, 2.5, D5))

# 16 hardware workstation
add('it-hardware', False, 'Hardware a pracoviště', 'hardware, počítače, monitory, periferie, vybavení', 120,
    p('M64 140h36l-4 30H68z', f=D5),
    p('M82 140c-2-20-16-26-24-26 2 14 10 22 24 26zM82 140c2-24 16-32 26-32-2 18-12 28-26 32z', f=GF),
    r(150, 40, 70, 140, 8, GF), fr(162, 56, 46, 8, G9), fr(162, 72, 46, 8, GB), c(185, 150, 8), fc(185, 150, 3, D5),
    r(270, 24, 220, 136, 10), fr(282, 36, 196, 104, GF, 4),
    fr(296, 50, 80, 8, D5), fr(296, 66, 120, 6), fr(296, 80, 100, 6), r(296, 96, 70, 32, 4, WH, 1.5), r(376, 96, 88, 32, 4, WH, 1.5),
    p('M380 160v18M346 186h68', sw=3),
    r(512, 128, 34, 50, 17), p('M529 128v16'))

# 17 devices / MDM
add('it-devices', False, 'Zařízení a správa', 'zařízení, notebooky, mobily, MDM, Intune, inventář', 300,
    gear(96, 80, 22),
    dash('M130 92c30 0 40 10 66 10'),
    r(250, 36, 190, 120, 8), fr(262, 48, 166, 96, GE, 3), fr(276, 62, 70, 8, D5), fr(276, 78, 110, 6), fr(276, 92, 90, 6),
    p('M226 160h238l-12 14H238z', f=GF),
    r(196, 76, 58, 104, 10), fr(214, 84, 22, 4, G9), fr(204, 96, 42, 52, GF, 4), fr(210, 104, 30, 6, D5),
    r(460, 46, 100, 130, 12), fr(470, 58, 80, 106, GF, 4), fr(480, 70, 50, 7, D5), fr(480, 86, 60, 6), r(480, 102, 60, 40, 4, WH, 1.5),
    check(556, 50, 11))

# 18 mobile app
add('it-mobile', True, 'Mobilní aplikace', 'mobilní aplikace, iOS, Android, Flutter, React Native', 345,
    r(150, 50, 150, 90, 10), p('M170 70l-10 10 10 10M190 70l10 10-10 10', sw=2.5),
    fr(214, 68, 66, 7, D5), fr(214, 84, 50, 6), fr(170, 108, 90, 6), fr(170, 122, 60, 6, G9),
    r(340, 16, 112, 196, 18), fr(378, 26, 36, 6, G9),
    fr(354, 44, 84, 10, D5), fr(354, 62, 84, 50, GF, 8),
    p('M354 104l20-22 14 14 10-8 40 26', f='none', col=G9), fc(420, 76, 5, GB),
    fr(354, 122, 60, 7), fr(354, 136, 76, 7), fr(354, 150, 40, 7, G9),
    r(480, 44, 56, 28, 14, D5), fc(522, 58, 10, WH),
    r(470, 100, 124, 48, 10), fc(492, 124, 10, GB), fr(510, 114, 70, 7, D5), fr(510, 128, 50, 6))

# 19 frontend / web app
add('it-frontend', False, 'Webový frontend', 'frontend, web, Vue, React, komponenty, CSS', 265,
    c(80, 60, 14, D5), fr(104, 56, 60, 8), c(80, 100, 14, G9), fr(104, 96, 44, 8), c(80, 140, 14, GE), fr(104, 136, 52, 8),
    win(220, 20, 370, 180), fr(300, 27, 200, 13, GF),
    fr(240, 62, 200, 62, GF, 6), p('M246 118l50-36 30 22 24-14 84 28', f='none', col=G9), fc(410, 80, 7, GB),
    fr(458, 66, 110, 8, D5), fr(458, 82, 90, 6), fr(458, 104, 80, 20, D5, 10),
    r(240, 140, 104, 50, 6, WH, 1.5), r(354, 140, 104, 50, 6, WH, 1.5), r(468, 140, 104, 50, 6, WH, 1.5),
    fr(252, 152, 60, 6), fr(366, 152, 50, 6), fr(480, 152, 64, 6),
    p('M520 110v30l8-8 6 14 6-3-6-13h11z', f=WH))

# 20 UX / UI design
add('it-design', False, 'UX a návrh rozhraní', 'UX, UI, design, Figma, prototypy, wireframy', 320,
    p('M120 40l30 50-30 60-30-60z', f=GF), p('M120 92v44'), c(120, 92, 6),
    fr(70, 160, 100, 8, GE),
    r(250, 30, 200, 150, 4),
    *[r(x - 5, y - 5, 10, 10, 1) for x, y in ((250, 30), (450, 30), (250, 180), (450, 180))],
    p('M270 150L300 60M430 70L380 160', col=G9), c(300, 60, 5), c(380, 160, 5),
    p('M270 150C300 60 380 160 430 70', sw=3),
    r(265, 145, 10, 10, 1, D5), r(425, 65, 10, 10, 1, D5),
    '<g transform="rotate(-14 538 170)">' + r(520, 40, 36, 130, 8, GF) + '</g>',
    '<g transform="rotate(0 538 170)">' + r(520, 40, 36, 130, 8, G9) + '</g>',
    '<g transform="rotate(14 538 170)">' + r(520, 40, 36, 130, 8, D5) + fc(538, 60, 6, WH) + '</g>')

# 21 testing / QA
rows = []
for i, y in enumerate((50, 78, 106, 134, 162)):
    rows.append((cross(282, y, 9) if i == 2 else check(282, y, 9)) + fr(300, y - 3, [110, 90, 120, 80, 100][i], 6, GB))
add('it-testing', True, 'Testování a kvalita', 'testy, QA, automatizace testů, regrese, kvalita', 140,
    p('M84 40h38M134 40h38', sw=3),
    p('M90 40v96a13 13 0 0 0 26 0V40', f=WH), p('M92 100v36a11 11 0 0 0 22 0v-36z', f=D5, col=None),
    p('M140 40v96a13 13 0 0 0 26 0V40', f=WH), p('M142 80v56a11 11 0 0 0 22 0V80z', f=G9, col=None),
    c(104, 84, 4), c(152, 64, 3),
    r(260, 24, 220, 172, 10), *rows,
    c(546, 84, 40, GF), p('M526 84l14 14 26-28', sw=6))
