# Contact sheet of one pass's views: python sheet.py <folder> <tag>
import os, sys
from PIL import Image, ImageDraw

folder, tag = sys.argv[1], sys.argv[2]
names = ["rest_side", "rest_front", "rest_top", "rest_3q", "open_side", "open_3q"]
ims = [Image.open(os.path.join(folder, f"{tag}_{n}.png")).convert("RGB") for n in names]
w, h = ims[0].size
sheet = Image.new("RGB", (w * 3, h * 2))
for i, (im, n) in enumerate(zip(ims, names)):
    x, y = (i % 3) * w, (i // 3) * h
    sheet.paste(im, (x, y))
    ImageDraw.Draw(sheet).text((x + 12, y + 10), n, fill=(255, 255, 0))
path = os.path.join(folder, f"{tag}_sheet.png")
sheet.save(path)
print(path)
