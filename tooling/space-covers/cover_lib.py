"""Shared drawing helpers for the space-cover SVGs (640x220 grey line art)."""
import math

K = '#3a3a3a'; D5 = '#5a5a5a'; G8 = '#8a8a8a'; G9 = '#9a9a9a'; GB = '#bdbdbd'
GD = '#dedede'; GE = '#e6e6e6'; GF = '#f2f2f2'; WH = '#ffffff'

HEAD = ('<svg xmlns="http://www.w3.org/2000/svg" width="640" height="220" viewBox="0 0 640 220">\n'
        '<rect width="640" height="220" fill="#ffffff"/>\n')
GRID = ('<defs><pattern id="g" width="32" height="32" patternUnits="userSpaceOnUse">'
        '<path d="M32 0H0V32" fill="none" stroke="#dedede" stroke-width="1"/></pattern></defs>\n'
        '<rect width="640" height="220" fill="url(#g)"/>\n')


def n(v):
    return ('%.1f' % v).rstrip('0').rstrip('.')


def r(x, y, w, h, rx=8, f=WH, sw=2, col=K, dash=None, extra=''):
    d = f' stroke-dasharray="{dash}"' if dash else ''
    return f'<rect x="{n(x)}" y="{n(y)}" width="{n(w)}" height="{n(h)}" rx="{n(rx)}" fill="{f}" stroke="{col}" stroke-width="{sw}"{d}{extra}/>'


def fr(x, y, w, h, f=GB, rx=None, extra=''):
    rx = h / 2 if rx is None else rx
    return f'<rect x="{n(x)}" y="{n(y)}" width="{n(w)}" height="{n(h)}" rx="{n(rx)}" fill="{f}"{extra}/>'


def c(cx, cy, rad, f=WH, sw=2, col=K, dash=None):
    d = f' stroke-dasharray="{dash}"' if dash else ''
    return f'<circle cx="{n(cx)}" cy="{n(cy)}" r="{n(rad)}" fill="{f}" stroke="{col}" stroke-width="{sw}"{d}/>'


def fc(cx, cy, rad, f=K):
    return f'<circle cx="{n(cx)}" cy="{n(cy)}" r="{n(rad)}" fill="{f}"/>'


def p(d, f='none', sw=2, col=K, dash=None, extra=''):
    da = f' stroke-dasharray="{dash}"' if dash else ''
    st = f' stroke="{col}" stroke-width="{sw}" stroke-linecap="round" stroke-linejoin="round"' if col else ''
    return f'<path d="{d}" fill="{f}"{st}{da}{extra}/>'


def dash(d, col=G8, sw=2):
    return p(d, sw=sw, col=col, dash='6 6')


def win(x, y, w, h, f=WH, head=26):
    return (r(x, y, w, h, 10, f) + p(f'M{x} {y + head}h{w}') +
            fc(x + 14, y + head / 2, 4, D5) + fc(x + 27, y + head / 2, 4, G9) + fc(x + 40, y + head / 2, 4, GD))


def check(cx, cy, rad=10, f=D5, col=WH):
    s = rad / 10
    return (f'<circle cx="{n(cx)}" cy="{n(cy)}" r="{n(rad)}" fill="{f}"/>' +
            p(f'M{n(cx - 5 * s)} {n(cy)}l{n(4 * s)} {n(4 * s)} {n(7 * s)}-{n(8 * s)}', sw=2.5, col=col))


def cross(cx, cy, rad=10, f=G9, col=WH):
    s = rad / 10 * 4
    return (f'<circle cx="{n(cx)}" cy="{n(cy)}" r="{n(rad)}" fill="{f}"/>' +
            p(f'M{n(cx - s)} {n(cy - s)}l{n(2 * s)} {n(2 * s)}M{n(cx + s)} {n(cy - s)}l-{n(2 * s)} {n(2 * s)}', sw=2.5, col=col))


def hexagon(cx, cy, rad, f=WH, sw=2):
    pts = ' '.join(f'{n(cx + rad * math.cos(math.radians(a)))},{n(cy + rad * math.sin(math.radians(a)))}' for a in range(0, 360, 60))
    return f'<polygon points="{pts}" fill="{f}" stroke="{K}" stroke-width="{sw}" stroke-linejoin="round"/>'


def gear(cx, cy, rad, teeth=8, f=GF, hole=WH):
    out = []
    for i in range(teeth):
        a = math.radians(i * 360 / teeth)
        x1, y1 = cx + rad * math.cos(a), cy + rad * math.sin(a)
        x2, y2 = cx + (rad + rad * 0.32) * math.cos(a), cy + (rad + rad * 0.32) * math.sin(a)
        out.append(f'M{n(x1)} {n(y1)}L{n(x2)} {n(y2)}')
    return (p(''.join(out), sw=max(3, rad * 0.32), col=K) + c(cx, cy, rad, f) + c(cx, cy, rad * 0.36, hole))


def person(cx, cy, s=1.0, f=K):
    """Head and shoulders glyph centred at cx,cy (fits a circle of radius ~18*s)."""
    return (fc(cx, cy - 5 * s, 6 * s, f) +
            f'<path d="M{n(cx - 11 * s)} {n(cy + 11 * s)}a{n(11 * s)} {n(8 * s)} 0 0 1 {n(22 * s)} 0z" fill="{f}"/>')


def cloud(x, y, s=1.0, f=GF, col=K, dash=None):
    """Cloud whose flat bottom-left corner is at x,y; ~232*s wide, ~126*s tall."""
    d = (f'M{n(x)} {n(y)}c{n(-26 * s)} 0 {n(-44 * s)} {n(-18 * s)} {n(-44 * s)} {n(-40 * s)}'
         f'c0 {n(-22 * s)} {n(18 * s)} {n(-40 * s)} {n(42 * s)} {n(-40 * s)}'
         f'c{n(8 * s)} {n(-28 * s)} {n(34 * s)} {n(-46 * s)} {n(64 * s)} {n(-46 * s)}'
         f'c{n(30 * s)} 0 {n(54 * s)} {n(18 * s)} {n(62 * s)} {n(44 * s)}'
         f'c{n(4 * s)} {n(-1 * s)} {n(8 * s)} {n(-2 * s)} {n(12 * s)} {n(-2 * s)}'
         f'c{n(28 * s)} 0 {n(52 * s)} {n(22 * s)} {n(52 * s)} {n(44 * s)}'
         f'c0 {n(22 * s)} {n(-22 * s)} {n(40 * s)} {n(-50 * s)} {n(40 * s)}z')
    return p(d, f=f, col=col, dash=dash)


def sparkle(cx, cy, rad, f=D5):
    q = rad * 0.3
    return (f'<path d="M{n(cx)} {n(cy - rad)}L{n(cx + q)} {n(cy - q)}L{n(cx + rad)} {n(cy)}L{n(cx + q)} {n(cy + q)}'
            f'L{n(cx)} {n(cy + rad)}L{n(cx - q)} {n(cy + q)}L{n(cx - rad)} {n(cy)}L{n(cx - q)} {n(cy - q)}z" fill="{f}"/>')


def arrowhead(x, y, angle_deg, size=8, col=K, sw=2):
    a = math.radians(angle_deg)
    b1, b2 = a + math.radians(150), a - math.radians(150)
    return p(f'M{n(x + size * math.cos(b1))} {n(y + size * math.sin(b1))}L{n(x)} {n(y)}L{n(x + size * math.cos(b2))} {n(y + size * math.sin(b2))}', sw=sw, col=col)


def svg(body, grid=False):
    return HEAD + (GRID if grid else '') + body + '\n</svg>\n'


def star(cx, cy, rad, f=D5, sw=0):
    pts = []
    for i in range(10):
        a = math.radians(-90 + i * 36)
        rr = rad if i % 2 == 0 else rad * 0.45
        pts.append(f'{n(cx + rr * math.cos(a))},{n(cy + rr * math.sin(a))}')
    st = f' stroke="{K}" stroke-width="{sw}" stroke-linejoin="round"' if sw else ''
    return f'<polygon points="{" ".join(pts)}" fill="{f}"{st}/>'


def bubble(x, y, w, h, f=WH, tail='left', rx=12):
    """Speech bubble as one outline, tail at the bottom-left or bottom-right."""
    top = f'M{n(x + rx)} {n(y)}h{n(w - 2 * rx)}a{rx} {rx} 0 0 1 {rx} {rx}v{n(h - 2 * rx)}a{rx} {rx} 0 0 1 -{rx} {rx}'
    if tail == 'left':
        bottom = f'H{n(x + 34)}l-16 14v-14'
    else:
        bottom = f'H{n(x + w - 18)}v14l-16-14'
    rest = f'H{n(x + rx)}a{rx} {rx} 0 0 1 -{rx} -{rx}V{n(y + rx)}a{rx} {rx} 0 0 1 {rx} -{rx}z'
    return p(top + bottom + rest, f=f)
