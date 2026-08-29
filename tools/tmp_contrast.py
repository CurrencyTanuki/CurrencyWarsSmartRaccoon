from PIL import Image, ImageDraw
base = r"C:\Users\zzz81\AppData\Local\Temp\reasonix-session-tmp-2856735112\cwbackdiag"
tpl_path = r"D:\CWAFix-20260814\data\4.4\character-card-templates\currency_wars_character_69__default.png"
tpl = Image.open(tpl_path).convert("RGBA")
slot = Image.open(base + r"\SLOT_0.png").convert("RGBA")
# 模板放大2x到与SLOT_0相近高度
tpl2 = tpl.resize((tpl.width * 2, tpl.height * 2), Image.LANCZOS)
H = max(tpl2.height, slot.height)
W = tpl2.width + slot.width + 20
canvas = Image.new("RGBA", (W, H), (40, 40, 40, 255))
canvas.paste(tpl2, (0, 0), tpl2)
canvas.paste(slot, (tpl2.width + 20, 0), slot)
d = ImageDraw.Draw(canvas)
d.text((5, 5), "TEMPLATE_69(Huohuo)", fill=(255, 255, 0, 255))
d.text((tpl2.width + 20, 5), "WARP_SLOT0", fill=(255, 255, 0, 255))
out = base + r"\COMPARE.png"
canvas.convert("RGB").save(out)
print(out)
