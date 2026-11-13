"""Builds the dark and light Aspirin wordmarks (icon and name) as SVG, with the name drawn as outlines from Inter Display.

Usage: python make_wordmark.py <font.ttf> <logo.svg> <out dir>
"""
import re
import sys

from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.recordingPen import DecomposingRecordingPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont

font_path, logo_path, out_dir = sys.argv[1], sys.argv[2], sys.argv[3]
font = TTFont(font_path)
glyphs = font.getGlyphSet()
cmap = font.getBestCmap()
units = font['head'].unitsPerEm
cap_height = font['OS/2'].sCapHeight

TEXT = 'Aspirin'
ICON = 256             # the icon fills a 256 x 256 square on the left
GAP = 40               # space between the icon and the name
CAP = 128              # cap height of the name, in the wordmark's units
TRACKING = -0.01       # letter spacing, as a fraction of the em
DOT_SCALE = 1.12       # the design's dots are a little larger than Inter's
RIGHT_PADDING = 4      # keeps the last letter's edge inside the image
GREEN = '#22c995'      # the dot over the last i
INK = {'dark': '#1b2130', 'light': '#eef3fb'}  # dark text for light backgrounds, light text for dark ones

scale = CAP / cap_height
baseline = ICON / 2 + CAP / 2


def contours(glyph_name):
    """Returns the glyph's outline split into closed contours, each a list of pen operations."""
    recording = DecomposingRecordingPen(glyphs)
    glyphs[glyph_name].draw(recording)
    result, current = [], []
    for op, args in recording.value:
        current.append((op, args))
        if op in ('closePath', 'endPath'):
            result.append(current)
            current = []
    return result


def replay(ops, pen):
    for op, args in ops:
        getattr(pen, op)(*args)


def to_path(ops, x):
    pen = SVGPathPen(glyphs, ntos=lambda v: f'{v:.1f}'.rstrip('0').rstrip('.'))
    replay(ops, TransformPen(pen, (scale, 0, 0, -scale, x, baseline)))
    return pen.getCommands()


def bounds(ops):
    pen = BoundsPen(glyphs)
    replay(ops, pen)
    return pen.bounds


ink, dots = [], []
x = ICON + GAP
for char in TEXT:
    name = cmap[ord(char)]
    parts = contours(name)
    if char == 'i':
        # The dot is the contour whose bottom edge is highest. It becomes a circle: green over the last i, matching
        # the design, and the color of the text over any other.
        dot = max(parts, key=lambda c: bounds(c)[1])
        parts = [c for c in parts if c is not dot]
        x_min, y_min, x_max, y_max = bounds(dot)
        radius = max(x_max - x_min, y_max - y_min) / 2 * scale * DOT_SCALE
        cx = x + (x_min + x_max) / 2 * scale
        cy = baseline - (y_min + y_max) / 2 * scale
        dots.append((cx, cy, radius))
    ink.append(''.join(to_path(c, x) for c in parts))
    x += (glyphs[name].width + TRACKING * units) * scale

width = round(x - TRACKING * units * scale + RIGHT_PADDING)

# The icon's markup, without its outer svg element.
logo = open(logo_path, encoding='utf-8').read()
icon = re.search(r'<svg[^>]*>(.*)</svg>', logo, re.S).group(1)
icon = re.sub(r'\s*<!--.*?-->', '', icon, flags=re.S).strip()

for variant, color in INK.items():
    circles = ''.join(
        f'  <circle cx="{cx:.1f}" cy="{cy:.1f}" r="{r:.1f}" fill="{GREEN if i == len(dots) - 1 else color}" />\n'
        for i, (cx, cy, r) in enumerate(dots))
    svg = (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {ICON}" role="img" aria-label="Aspirin">\n'
        f'  {icon}\n'
        f'  <path d="{"".join(ink)}" fill="{color}" />\n'
        f'{circles}'
        '</svg>\n'
    )
    with open(f'{out_dir}/wordmark-{variant}.svg', 'w', encoding='utf-8', newline='\n') as f:
        f.write(svg)
