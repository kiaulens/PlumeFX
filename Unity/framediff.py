# Image difference from video frame to video frame (debugging: jumps/twitches in the ground cloud)
# Usage: python framediff.py <view> [from_s] [to_s]  -> mean change per frame, spikes (> 3x median)
import os, sys, glob
from PIL import Image, ImageChops, ImageStat
view = sys.argv[1]
t0 = float(sys.argv[2]) if len(sys.argv) > 2 else 0.0
t1 = float(sys.argv[3]) if len(sys.argv) > 3 else 1e9
fps = 30.0
d = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'Preview', 'frames')
files = sorted(glob.glob(os.path.join(d, view + '_*.png')))
diffs = []
prev = None
for f in files:
    k = int(os.path.basename(f).split('_')[1].split('.')[0])
    t = k / fps
    if t < t0 or t > t1:
        continue
    im = Image.open(f).convert('L').resize((320, 213))
    if prev is not None:
        diffs.append((t, ImageStat.Stat(ImageChops.difference(im, prev)).mean[0]))
    prev = im
if not diffs:
    print('no frames'); sys.exit()
vals = sorted(v for _, v in diffs)
med = vals[len(vals) // 2]
spikes = [(t, v) for t, v in diffs if v > 3 * med and v > 0.5]
print('frames=%d median=%.3f max=%.3f spikes(>3x median)=%d' % (len(diffs), med, vals[-1], len(spikes)))
for t, v in spikes[:12]:
    print('  t=%.2f diff=%.2f' % (t, v))
