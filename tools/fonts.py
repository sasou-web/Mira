"""Mira's typeface, Nunito, for every app: python3 -I tools/fonts.py (needs fonttools 4.x and brotli).

Google Fonts only publishes Nunito as a variable font: the four weights Mira uses (400, 600, 700, 800) are cut from it
as static fonts (WPF and Avalonia read statics best), then the iPhone and Mira web get the Latin subset of each.
"""
import hashlib, pathlib, sys, urllib.request
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer
from fontTools import subset

REVISION = '6157dfaa640ca5738b4d1a7070fe389c3036d7fc'  # google/fonts, 2026-10-30
BASE = f'https://raw.githubusercontent.com/google/fonts/{REVISION}/ofl/nunito'
DIGEST = 'bb55a5ca5c2042335b3991af27c4d0705d0ef41cac6164ac737fd8f2a1e85207'
WEIGHTS = {400: 'Regular', 600: 'SemiBold', 700: 'Bold', 800: 'ExtraBold'}
# Google Fonts' "latin" subset, the one Mira web always had.
LATIN = 'U+0000-00FF,U+0131,U+0152-0153,U+02BB-02BC,U+02C6,U+02DA,U+02DC,U+0304,U+0308,U+0329,U+2000-206F,U+20AC,U+2122,U+2191,U+2193,U+2212,U+2215,U+FEFF,U+FFFD'

root = pathlib.Path(__file__).resolve().parent.parent
desktop = root / 'src/Mira.Desktop/Assets/Fonts'
ios = root / 'src/Mira.iOS/Mira/Resources/Fonts'
web = root / 'src/Mira.Web/fonts'

def fetch(name):
    with urllib.request.urlopen(f'{BASE}/{name}') as answer: return answer.read()

variable = fetch('Nunito%5Bwght%5D.ttf')
if hashlib.sha256(variable).hexdigest() != DIGEST: sys.exit('Nunito: unexpected file, check REVISION and DIGEST.')
license = fetch('OFL.txt')
source = root / '.artifacts/fonts/Nunito[wght].ttf'
source.parent.mkdir(parents=True, exist_ok=True)
source.write_bytes(variable)

for folder in (desktop, ios, web):
    for old in folder.glob('NunitoSans-*.ttf'): old.unlink()
    for old in folder.glob('nunito-sans-*.woff2'): old.unlink()
    (folder / 'OFL.txt').write_bytes(license)

for weight, style in WEIGHTS.items():
    font = instancer.instantiateVariableFont(TTFont(source, recalcTimestamp=False), {'wght': weight}, static=True, updateFontNames=True)
    font.recalcTimestamp = False  # the variable font's dates: the same bytes at every run
    static = desktop / f'Nunito-{style}.ttf'
    font.save(static)
    # The iPhone and Mira web carry only the Latin subset: the iPhone in TrueType, Mira web in WOFF2.
    options = subset.Options(); options.layout_features = ['*']; options.name_IDs = ['*']
    latin = TTFont(static, recalcTimestamp=False)
    cutter = subset.Subsetter(options)
    cutter.populate(unicodes=subset.parse_unicodes(LATIN))
    cutter.subset(latin)
    latin.save(ios / static.name)
    latin.flavor = 'woff2'
    latin.save(web / f'nunito-{weight}.woff2')

(desktop / 'SOURCE.txt').write_text(f'Nunito\nhttps://github.com/google/fonts/tree/{REVISION}/ofl/nunito\n'
    'Static instances (400, 600, 700, 800) of the official variable font, cut by tools/fonts.py. SIL Open Font License 1.1.\n')
print('Nunito written for Windows, Mac, iPhone and Mira web.')
