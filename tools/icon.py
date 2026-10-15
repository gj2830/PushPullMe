"""The PushPullMe icon: the user's own picture (14 Oct 2026, src/PushPullMe/Assets/icoon_bron.png: the blue PUSH PULL
cube with the orange block and the pointer), taken over as it is: its light background made transparent (only what
joins the border, so the white pointer stays), cropped to a square, saved as .ico (16..256) and a 256 px .png.
Usage: python tools/icon.py <source.png> <out.ico> <out.png>"""
import sys
from PIL import Image, ImageDraw, ImageFilter

src, out_ico, out_png = sys.argv[1], sys.argv[2], sys.argv[3]
img = Image.open(src).convert('RGBA')
w, h = img.size

# the background: light and nearly grey, joined to the border (flood fill from every border pixel that is like it)
def is_bg(p):
    r, g, b, _ = p
    return min(r, g, b) > 200 and max(r, g, b) - min(r, g, b) < 28

px = img.load()
mask = Image.new('L', (w, h), 0)
m = mask.load()
stack = [(x, y) for x in range(w) for y in (0, h - 1)] + [(x, y) for y in range(h) for x in (0, w - 1)]
while stack:
    x, y = stack.pop()
    if x < 0 or y < 0 or x >= w or y >= h or m[x, y] or not is_bg(px[x, y]):
        continue
    m[x, y] = 255
    stack.extend([(x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)])
# a soft edge: the background mask grown a little and blurred, then out of the alpha
soft = mask.filter(ImageFilter.MaxFilter(3)).filter(ImageFilter.GaussianBlur(1.2))
alpha = Image.eval(soft, lambda v: 255 - v)
img.putalpha(alpha)

# cropped to what is left, as a square with a little room round it
box = alpha.point(lambda v: 255 if v > 20 else 0).getbbox()
img = img.crop(box)
side = int(max(img.size) * 1.04)
sq = Image.new('RGBA', (side, side), (0, 0, 0, 0))
sq.alpha_composite(img, ((side - img.size[0]) // 2, (side - img.size[1]) // 2))

sq.resize((256, 256), Image.LANCZOS).save(out_png)
sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
frames = [sq.resize(s, Image.LANCZOS) for s in sizes]
frames[-1].save(out_ico, sizes=sizes, append_images=frames[:-1])
print('ok', box, side)
