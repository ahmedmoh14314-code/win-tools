# Regenerates the committed WebP samples with Pillow (libwebp built into Pillow, independent of the SkiaSharp code under
# test) from the GDI+-made PNG samples, losslessly, so decoders can be checked pixel-exact against the PNG originals.
# Run: python tests/samples/New-WebPSamples.py   (needs Pillow with WebP support; development tooling only)
import hashlib, os
from PIL import Image

here = os.path.dirname(os.path.abspath(__file__))
for name in ('quadrants-64x48', 'alpha-32x32'):
    src = os.path.join(here, name + '.png')
    dst = os.path.join(here, name + '.webp')
    Image.open(src).save(dst, 'WEBP', lossless=True, exact=True)  # exact: keep RGB under fully transparent pixels
    data = open(dst, 'rb').read()
    print(f'{name}.webp  {len(data)} bytes  {hashlib.sha256(data).hexdigest().upper()}')
