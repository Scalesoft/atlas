from cover_lib import *

C = {}
M = {}


def add(cid, grid, name, tags, hue, *parts, group='IT'):
    C[cid] = (grid, '\n'.join(parts))
    M[cid] = (name, group, tags, hue)


def cylinder(cx, top, rx, ry, h, f=WH, cap=GF):
    return (p(f'M{cx - rx} {top}v{h}a{rx} {ry} 0 0 0 {2 * rx} 0V{top}', f=f) +
            f'<ellipse cx="{cx}" cy="{top}" rx="{rx}" ry="{ry}" fill="{cap}" stroke="{K}" stroke-width="2"/>')


# 22 bugs / issue tracking
add('it-bugs', False, 'Chyby a issue tracking', 'chyby, bugy, issue tracker, YouTrack, Jira, tikety', 5,
    r(50, 40, 160, 120, 10),
    fr(64, 56, 30, 10, D5), fr(102, 57, 90, 7), fr(52, 76, 156, 22, GF, 0), fr(64, 82, 30, 10, G9), fr(102, 83, 70, 7, D5),
    fr(64, 108, 30, 10, GB), fr(102, 109, 84, 7), fr(64, 134, 30, 10, GE), fr(102, 135, 60, 7),
    p('M410 32l-10-16M430 32l10-16', sw=2.5), fc(400, 16, 3, K), fc(440, 16, 3, K),
    p('M380 90h-26M380 112h-30M386 134l-22 16M460 90h26M460 112h30M454 134l22 16', sw=3),
    '<ellipse cx="420" cy="108" rx="40" ry="52" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/>',
    c(420, 46, 18, D5), p('M420 62v96'),
    fc(404, 92, 7, D5), fc(438, 120, 7, D5), fc(402, 132, 4, G9), fc(440, 86, 4, G9),
    p('M530 46v96'), p('M530 50h46l-10 16 10 16h-46z', f=D5))

# 23 data pipeline / ETL
add('it-data-pipeline', True, 'Datové toky a ETL', 'ETL, datové toky, import, transformace, sklad dat', 290,
    r(50, 30, 76, 36, 8), fr(62, 44, 40, 8, D5), r(50, 84, 76, 36, 8, GF), fr(62, 98, 50, 8, G9), r(50, 138, 76, 36, 8), fr(62, 152, 30, 8),
    p('M126 48c60 0 70 52 140 52M126 102h140M126 156c60 0 70-56 140-56', col=G9),
    fc(180, 64, 4, D5), fc(200, 102, 4, D5), fc(184, 136, 4, D5),
    gear(300, 100, 24),
    p('M340 100h92'), arrowhead(434, 100, 0), fc(372, 100, 4, D5), fc(396, 100, 4, G9),
    cylinder(510, 50, 62, 14, 104),
    p('M448 86a62 14 0 0 0 124 0M448 120a62 14 0 0 0 124 0'),
    fr(470, 98, 60, 6), fr(470, 132, 44, 6, D5))

# 24 analytics / BI
add('it-analytics', False, 'Analytika a reporty', 'analytika, BI, Power BI, reporty, KPI, dashboardy', 35,
    c(110, 90, 46, GF), p('M84 114l22-22 12 12 24-26M128 78h14v14', sw=4),
    r(230, 20, 380, 180, 12), p('M230 46h380'), fr(246, 29, 80, 8, D5), fr(334, 29, 40, 8),
    c(310, 118, 50, GF),
    p('M310 118L310 68A50 50 0 0 1 357.6 133.5z', f=D5), p('M310 118L357.6 133.5A50 50 0 0 1 301.3 167.2z', f=G9),
    p('M384 176h100M384 176V66', col=G9),
    fr(394, 126, 16, 50, GB, 2), fr(418, 100, 16, 76, G9, 2), fr(442, 114, 16, 62, GB, 2), fr(466, 78, 16, 98, D5, 2),
    r(500, 60, 96, 46, 8, GF), fr(512, 72, 40, 7, G9), fr(512, 86, 64, 10, D5),
    r(500, 118, 96, 46, 8), fr(512, 130, 40, 7, G9), fr(512, 144, 52, 10, GB))

# 25 AI / ML
layers = [(300, [60, 110, 160]), (380, [40, 86, 132, 178]), (460, [40, 86, 132, 178]), (540, [80, 140])]
links = []
for (x1, ys1), (x2, ys2) in zip(layers, layers[1:]):
    for y1 in ys1:
        for y2 in ys2:
            links.append(f'M{x1} {y1}L{x2} {y2}')
nodes = []
for li, (x, ys) in enumerate(layers):
    for yi, y in enumerate(ys):
        nodes.append(c(x, y, 11, D5 if (li + yi) % 3 == 0 else (GF if (li + yi) % 3 == 1 else WH)))
add('it-ai-ml', False, 'AI a strojové učení', 'AI, strojové učení, modely, neuronové sítě, data science', 275,
    sparkle(120, 92, 36, D5), sparkle(178, 46, 14, G9), fc(176, 136, 6, GB), fc(70, 40, 4, G9),
    p(''.join(links), col=GB, sw=1.5), *nodes)

# 26 LLM / assistant
add('it-llm', True, 'AI asistenti a LLM', 'LLM, chatbot, asistent, prompty, RAG, Claude, GPT', 260,
    r(60, 50, 90, 110, 6, GF), r(80, 40, 90, 110, 6), fr(94, 56, 50, 7, D5), fr(94, 72, 60, 6), fr(94, 86, 44, 6), fr(94, 100, 56, 6),
    dash('M178 100h50'), arrowhead(232, 100, 0, 8, G8),
    r(240, 24, 300, 172, 12),
    fr(380, 40, 144, 30, GF, 10), fr(392, 51, 100, 7, G9),
    sparkle(266, 92, 12, D5), fr(286, 84, 140, 7, D5), fr(286, 98, 200, 6), fr(286, 112, 170, 6), fr(286, 126, 120, 6),
    r(256, 150, 268, 32, 16, GF), fr(272, 163, 120, 6), c(506, 166, 11, D5, 0), p('M501 166h10M507 161l5 5-5 5', sw=2, col=WH),
    sparkle(580, 118, 20, D5), sparkle(566, 168, 8, G9))

# 27 git / branches
add('it-git', False, 'Git a repozitáře', 'git, větve, merge, pull requesty, GitHub, GitLab', 20,
    p('M40 140h560', sw=3),
    p('M140 140c0-50 20-70 60-70h200c40 0 60 20 70 70', sw=3, col=G9),
    p('M300 140c0 24 10 36 40 36h40c20 0 30-12 30-36', sw=3, col=GB),
    c(60, 140, 9), c(140, 140, 9, D5), c(240, 140, 9), c(340, 140, 9), c(480, 140, 12, D5), c(560, 140, 9),
    c(220, 70, 9, GF), c(300, 70, 9, GF), c(380, 70, 9, GF), c(360, 176, 7, GF),
    r(440, 20, 150, 46, 10), p('M462 34v20M462 34c0 10 8 12 18 12', sw=2), fc(462, 34, 4, K), fc(462, 54, 4, K), fc(480, 46, 4, K),
    fr(496, 34, 74, 7, D5), fr(496, 48, 50, 6))

# 28 code review
diff = []
for i in range(7):
    y = 54 + i * 19
    bg = GF if i in (2, 3) else (GE if i == 5 else None)
    if bg:
        diff.append(fr(252, y - 5, 306, 17, bg, 0))
    mark = 'M264 {0}h8M268 {1}v8'.format(y + 3, y - 1) if i in (2, 3) else ('M264 {0}h8'.format(y + 3) if i == 5 else '')
    if mark:
        diff.append(p(mark, sw=2))
    diff.append(fr(282, y, [120, 90, 150, 110, 70, 130, 100][i], 6, D5 if i in (2, 3) else GB))
add('it-code-review', False, 'Code review', 'code review, diff, schvalování, komentáře, pull request', 215,
    c(96, 76, 28, GF), person(96, 76, 1.1), check(120, 100, 10),
    c(160, 126, 22), person(160, 126, 0.9, D5),
    win(250, 20, 310, 180), *diff,
    p('M470 118a10 10 0 0 1 10-10h108a10 10 0 0 1 10 10v30a10 10 0 0 1-10 10h-80l-14 12v-12h-14a10 10 0 0 1-10-10z', f=WH),
    fc(490, 128, 8, G9), fr(504, 122, 70, 6, D5), fr(504, 136, 50, 6))

# 29 developer documentation
add('it-dev-docs', False, 'Vývojářská dokumentace', 'dokumentace, API reference, návody, README, SDK', 230,
    r(70, 60, 26, 100, 4, D5), r(98, 50, 24, 110, 4, GF), r(124, 70, 30, 90, 4),
    fr(76, 76, 14, 6, WH), fr(102, 66, 16, 6, G9), fr(130, 86, 18, 6, GB),
    p('M420 50c-30-14-80-16-120-6v132c40-10 90-8 120 6z', f=WH), p('M420 50c30-14 80-16 120-6v132c-40-10-90-8-120 6z', f=GF),
    p('M420 50v132'),
    fr(318, 62, 70, 8, D5), fr(318, 80, 84, 6), fr(318, 94, 76, 6), fr(318, 108, 84, 6), fr(318, 122, 60, 6), fr(318, 140, 80, 6),
    p('M448 64l-8 8 8 8M470 64l8 8-8 8', sw=2.5),
    fr(448, 92, 40, 6, D5), fr(494, 92, 28, 6, G9), fr(460, 106, 56, 6), fr(460, 120, 40, 6, G9), fr(448, 134, 30, 6, D5))

# 30 release / launch
add('it-release', False, 'Release a vydání', 'release, vydání verze, changelog, launch, nasazení', 15,
    p('M60 60h90l24 24-24 24H60z', f=GF), c(76, 84, 6), fr(96, 80, 46, 8, D5),
    fr(60, 126, 90, 6), fr(60, 140, 70, 6), fr(60, 154, 80, 6, GE),
    c(330, 176, 16, GF, 2, G9), c(300, 192, 12, GF, 2, G9), c(356, 196, 12, WH, 2, G9),
    '<g transform="translate(420 104) rotate(40)">' +
    p('M-16 40c0 22 8 36 16 46 8-10 16-24 16-46z', f=G9) +
    p('M-30 18l-20 26v16l20-10zM30 18l20 26v16l-20-10z', f=D5) +
    p('M0 -72c22 18 30 46 30 80v32h-60V8c0-34 8-62 30-80z', f=WH) +
    c(0, -12, 12, GF) + p('M-30 34h60') + '</g>',
    sparkle(530, 44, 12, G9), fc(566, 96, 4, GB), sparkle(290, 50, 8, GB))

# 31 terminal / CLI
add('it-terminal', True, 'Příkazová řádka', 'terminál, CLI, PowerShell, bash, skripty, SSH', 160,
    r(60, 54, 50, 50, 10, GF), r(118, 54, 50, 50, 10), r(86, 112, 82, 50, 10),
    p('M78 72l8 8-8 8', sw=3), p('M136 88l14-20', sw=3), fr(104, 132, 46, 8, D5),
    r(240, 24, 340, 172, 12, GF), p('M240 52V36a12 12 0 0 1 12-12h316a12 12 0 0 1 12 12v16z', f=D5),
    fc(258, 38, 4, WH), fc(272, 38, 4, GB), fc(286, 38, 4, G9),
    p('M264 72l8 6-8 6', sw=2.5), fr(282, 74, 130, 7, D5),
    fr(264, 96, 220, 6, GB), fr(264, 110, 180, 6, GB), fr(264, 124, 240, 6, G9),
    p('M264 148l8 6-8 6', sw=2.5), fr(282, 150, 60, 7, D5), fr(350, 146, 10, 16, K, 1))

# 32 automation / workflows
add('it-automation', False, 'Automatizace a workflow', 'automatizace, workflow, skripty, roboti, integrace', 110,
    c(60, 70, 16, D5), p('M55 62v16l12-8z', f=WH, col=None),
    p('M76 70h40M164 70h30'), arrowhead(116, 70, 0, 6), arrowhead(194, 70, 0, 6),
    p('M140 46l24 24-24 24-24-24z', f=GF), r(194, 54, 70, 32, 8),
    fr(206, 66, 40, 7, G9), p('M140 94v40h54', col=G9), r(194, 118, 70, 32, 8, GF), fr(206, 130, 30, 7, G9),
    dash('M264 70c40 0 50 30 80 30'),
    gear(410, 100, 38), gear(486, 152, 24, f=WH, hole=GF), gear(484, 52, 18, f=D5, hole=WH))

# 33 scheduling / cron
cells = []
for row in range(4):
    for col in range(6):
        fill = D5 if (row, col) in ((1, 2), (3, 4)) else (G9 if (row, col) == (2, 1) else GE)
        cells.append(fr(274 + col * 24, 82 + row * 24, 16, 16, fill, 3))
add('it-scheduling', False, 'Plánované úlohy', 'plánování, cron, plánované úlohy, termíny, opakování', 70,
    p('M80 40h60M80 160h60', sw=4),
    p('M96 56h28c-2 14-8 22-14 26-6-4-12-12-14-26z', f=D5, col=None),
    p('M92 156c4-14 12-20 18-22 6 2 14 8 18 22z', f=G9, col=None),
    p('M88 40c0 34 16 44 22 60-6 16-22 26-22 60M132 40c0 34-16 44-22 60 6 16 22 26 22 60'),
    r(260, 40, 170, 150, 10), p('M260 70V52a12 12 0 0 1 12-12h146a12 12 0 0 1 12 12v18z', f=D5),
    fr(286, 30, 6, 20, K, 3), fr(398, 30, 6, 20, K, 3), *cells,
    c(458, 110, 52, GF), p('M458 74v36l22 14', sw=3), fc(458, 110, 4, K),
    p('M458 44a66 66 0 0 1 62 44'), arrowhead(520, 88, 70), p('M458 176a66 66 0 0 1-62-44'), arrowhead(396, 132, 250))

# 34 messaging / queues
msgs = ''.join(r(204 + i * 54, 92, 40, 26, 4, (GF, WH, D5, WH)[i]) + p(f'M{206 + i * 54} 95l18 12 18-12', col=WH if i == 2 else K, sw=1.5) for i in range(4))
add('it-messaging', True, 'Fronty a události', 'fronty, zprávy, události, RabbitMQ, Kafka, Wolverine', 185,
    r(60, 70, 80, 70, 10, GF), fr(76, 88, 48, 8, D5), fr(76, 104, 36, 6), fr(76, 118, 44, 6),
    p('M140 105h44'), arrowhead(186, 105, 0),
    r(190, 82, 240, 46, 23), msgs,
    p('M430 105h20c20 0 20-43 30-43M450 105c20 0 20 45 30 45'),
    r(480, 36, 100, 52, 10), fr(494, 50, 56, 7, D5), fr(494, 66, 40, 6), check(562, 62, 9),
    r(480, 124, 100, 52, 10), fr(494, 138, 56, 7, D5), fr(494, 154, 40, 6), check(562, 150, 9, G9))

# 35 search / indexing
add('it-search', False, 'Vyhledávání a indexace', 'vyhledávání, indexace, fulltext, Elasticsearch, embeddings', 200,
    r(60, 60, 120, 90, 8, GF), fr(72, 50, 30, 14, GB, 4), fr(108, 50, 30, 14, G9, 4), fr(144, 50, 26, 14, GB, 4), fr(100, 118, 40, 8, D5),
    r(220, 36, 330, 46, 23), c(250, 58, 9, 'none', 2.5), p('M257 65l8 8', sw=3), fr(272, 56, 140, 7, GB),
    r(500, 44, 42, 30, 15, D5, 0), p('M514 59h14M523 54l5 5-5 5', sw=2, col=WH),
    fr(236, 104, 150, 8, D5), fr(236, 118, 90, 5, G9), fr(236, 128, 220, 6),
    fr(236, 148, 120, 8, D5), fr(236, 162, 70, 5, G9), fr(236, 172, 200, 6, GE),
    sparkle(560, 140, 14, G9))

# 36 licences / subscriptions
add('it-licenses', False, 'Licence a předplatné', 'licence, předplatné, SaaS, klíče, nákup softwaru', 55,
    p('M70 40h100v120l-10-8-10 8-10-8-10 8-10-8-10 8-10-8-10 8-10-8-10 8z', f=WH),
    fr(84, 56, 50, 8, D5), fr(84, 74, 70, 6), fr(84, 88, 60, 6), fr(84, 102, 70, 6), fr(84, 124, 40, 8, G9), check(150, 128, 9),
    p('M300 60l30-26h120l-30 26z', f=GF), p('M420 60l30-26v120l-30 26z', f=GB),
    r(300, 60, 120, 120, 4), c(360, 120, 24, GF), c(360, 120, 6), fr(316, 74, 60, 8, D5),
    r(480, 96, 120, 58, 10), c(504, 125, 10, GF), p('M514 125h18M526 125v8', sw=3),
    fc(546, 125, 3.5, K), fc(558, 125, 3.5, K), fc(570, 125, 3.5, K), fc(582, 125, 3.5, K))

# 37 IoT
add('it-iot', False, 'IoT a senzory', 'IoT, senzory, telemetrie, zařízení, měření', 145,
    p('M50 110l40-36 40 36v50H50z', f=GF), fr(80, 128, 20, 32, D5, 2), fr(62, 116, 14, 14, WH, 2),
    p('M76 62a20 20 0 0 1 28 0M68 52a32 32 0 0 1 44 0', col=G9),
    dash('M250 70c50 0 70 30 110 30M262 158c50 0 60-24 98-24M450 110h56', G8),
    p('M236 40a8 8 0 0 1 16 0v52a16 16 0 1 1-16 0z', f=WH), p('M244 70v36', sw=6, col=D5), fc(244, 112, 9, D5),
    p('M262 136c10 14 16 22 16 30a16 16 0 0 1-32 0c0-8 6-16 16-30z', f=GF),
    r(360, 80, 90, 60, 10, D5), fc(380, 110, 4, WH), fc(394, 110, 4, GB), fr(408, 107, 28, 6, WH),
    p('M420 80V52'), fc(420, 48, 5, K), p('M398 36a32 32 0 0 1 44 0M388 24a46 46 0 0 1 64 0', col=G9),
    c(538, 110, 30, GF), p('M518 110a20 20 0 0 1 40 0', col=G9, sw=3), p('M538 110l12-12', sw=3), fc(538, 110, 4, K))

# 38 migration
add('it-migration', True, 'Migrace a modernizace', 'migrace, modernizace, přechod do cloudu, legacy, upgrade', 240,
    check(64, 60, 9), fr(80, 57, 80, 6), check(64, 86, 9), fr(80, 83, 64, 6), check(64, 112, 9), fr(80, 109, 72, 6),
    c(64, 138, 9, WH), fr(80, 135, 56, 6, GE),
    r(220, 50, 90, 124, 8, GF), fr(234, 66, 62, 10, G9, 3), fr(234, 84, 62, 10, GB, 3), fr(234, 102, 62, 10, GB, 3),
    fc(242, 152, 4, G9), p('M280 120l8 14-8 12', col=G9),
    dash('M310 80c60-56 150-56 190 0', G8), arrowhead(500, 80, 55, 8, G8),
    '<g transform="rotate(-18 376 46)">' + r(362, 32, 28, 26, 4, D5) + '</g>',
    '<g transform="rotate(10 432 34)">' + r(418, 20, 28, 26, 4, WH) + '</g>',
    cloud(470, 172, 0.62, GF), check(560, 156, 12))

# 39 on-call
add('it-on-call', False, 'Pohotovost a služby', 'pohotovost, on-call, služby, rozpis, eskalace', 285,
    c(66, 56, 14, GF), person(66, 56, 0.7), fr(90, 52, 90, 8, D5),
    c(66, 96, 14, WH), person(66, 96, 0.7), fr(90, 92, 60, 8, G9),
    c(66, 136, 14, GB), person(66, 136, 0.7), fr(90, 132, 76, 8, GB),
    p('M310 76a40 40 0 0 0 0 50M296 62a60 60 0 0 0 0 78M460 76a40 40 0 0 1 0 50M474 62a60 60 0 0 1 0 78', col=G9, sw=3),
    r(330, 30, 110, 176, 16), fr(366, 40, 38, 6, G9), fr(342, 56, 86, 100, GF, 6),
    p('M385 72l16 28h-32z', f=D5), fr(356, 112, 58, 7, D5), fr(356, 126, 44, 6), fr(366, 168, 38, 16, D5, 8),
    p('M520 90a34 34 0 1 0 30 50 28 28 0 1 1-30-50z', f=GF), sparkle(574, 72, 9, G9), fc(500, 66, 3, GB))

# 40 tracing / observability
spans = [(250, 300, D5), (262, 140, G9), (278, 60, GB), (408, 120, G9), (420, 40, GB), (470, 70, D5)]
add('it-tracing', False, 'Observabilita a tracing', 'observabilita, tracing, OpenTelemetry, metriky, profilování', 205,
    '<g transform="rotate(-25 110 100)">' + r(60, 84, 110, 28, 6, GF) + r(160, 80, 22, 36, 4, D5) + r(46, 90, 16, 16, 3) + '</g>',
    p('M110 112l-24 54M110 112l24 54M110 112v56', sw=2.5),
    r(230, 24, 370, 172, 12), p('M230 50h370'), fr(246, 33, 70, 8, D5),
    p(''.join(f'M{x} 58v6' for x in range(250, 590, 40)), col=GB),
    *[fr(x, 76 + i * 18, w, 11, col, 3) for i, (x, w, col) in enumerate(spans)],
    p('M256 87v10M268 105v10M414 141v10', col=GB))

# 41 configuration / settings
add('it-config', True, 'Konfigurace a nastavení', 'konfigurace, nastavení, parametry, feature flags, prostředí', 175,
    p('M70 40v120M110 40v120M150 40v120', col=GB, sw=3),
    r(58, 70, 24, 14, 4, D5), r(98, 120, 24, 14, 4), r(138, 54, 24, 14, 4, GF),
    r(260, 24, 250, 172, 12),
    fr(278, 46, 100, 8, D5), r(452, 40, 40, 20, 10, D5, 0), fc(482, 50, 7, WH),
    fr(278, 78, 80, 8, G9), r(452, 72, 40, 20, 10, GE, 0), fc(462, 82, 7, WH), c(462, 82, 7, 'none', 1.5, GB),
    fr(278, 112, 100, 6, GE), fr(278, 112, 64, 6, D5), c(342, 115, 8),
    fr(278, 140, 100, 6, GE), fr(278, 140, 30, 6, G9), c(308, 143, 8),
    r(278, 160, 140, 24, 6, GF), p('M398 168l6 6 6-6', sw=2),
    gear(566, 150, 26, f=GF), gear(590, 96, 12, f=WH))

# 42 virtualization
def vm(x):
    return (r(x, 44, 92, 72, 6) + p(f'M{x} 60h92') + fc(x + 9, 52, 2.5, D5) + fc(x + 17, 52, 2.5, G9) +
            fr(x + 10, 70, 50, 6, D5) + fr(x + 10, 84, 70, 5) + fr(x + 10, 96, 56, 5))
layers3 = ''.join(p(f'M110 {y}l60 22-60 22-60-22z', f=f) for y, f in ((98, GB), (74, WH), (50, GF)))
add('it-virtualization', False, 'Virtualizace', 'virtualizace, VM, Hyper-V, VMware, hypervisor', 255,
    layers3,
    r(250, 20, 330, 180, 14, GF), vm(268), vm(370), vm(472),
    fr(268, 130, 296, 18, D5, 4), fr(282, 136, 80, 6, WH),
    r(268, 160, 90, 24, 4), r(370, 160, 90, 24, 4), r(472, 160, 92, 24, 4),
    fc(282, 172, 3.5, D5), fc(384, 172, 3.5, D5), fc(486, 172, 3.5, D5))
