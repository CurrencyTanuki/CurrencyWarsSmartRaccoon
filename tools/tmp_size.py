from PIL import Image
import os
base = r"C:\Users\zzz81\AppData\Local\Temp\reasonix-session-tmp-2856735112\cwbackdiag"
tpl = r"D:\CWAFix-20260814\data\4.4\character-card-templates\currency_wars_character_69__default.png"
for p in [tpl, os.path.join(base, "SLOT_0.png")]:
    im = Image.open(p)
    print(os.path.basename(p), im.size, im.mode)
