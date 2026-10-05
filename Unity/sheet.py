# Contact sheet of the preview stills: rows = views, columns = times.
# Usage: python sheet.py [times, e.g. 00.5,03.0] [views, e.g. side,chase]
import os, sys, glob
from PIL import Image, ImageDraw
d = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'Preview', 'shots')
views = sys.argv[2].split(',') if len(sys.argv) > 2 else ['side', 'chase', 'top', 'below', 'near']
times = sorted({f.split('_')[1][:-5] for f in os.listdir(d) if f.endswith('.png')})
sel = sys.argv[1].split(',') if len(sys.argv) > 1 else times
tw, th = 360, 240
sheet = Image.new('RGB', (tw * len(sel), th * len(views)), (0, 0, 0))
dr = ImageDraw.Draw(sheet)
for r, v in enumerate(views):
    for c, t in enumerate(sel):
        p = os.path.join(d, f'{v}_{t}s.png')
        if not os.path.exists(p): continue
        im = Image.open(p).convert('RGB').resize((tw, th), Image.LANCZOS)
        sheet.paste(im, (c * tw, r * th))
        dr.text((c * tw + 6, r * th + 4), f'{v} {t}s', fill=(255, 255, 0))
out = os.path.join(os.path.dirname(d), 'sheet.png')
sheet.save(out)
print(out)
