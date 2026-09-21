# -*- coding: utf-8 -*-
"""把可变字体 NotoSansSC-VF.ttf 实例化为静态 Regular（修复 Avalonia 渲染发虚）。

原因：Avalonia 对可变字体（fvar/gvar/HVAR）的 wght 轴支持不完善，
默认字重被解析为过细实例 → 笔画发虚、模糊。

产出：子集化的静态 Regular TTF（只保留常用汉字 + 拉丁，体积大幅缩小）。
"""
import pathlib
import sys

try:
    from fontTools.ttLib import TTFont
    from fontTools.varLib import instancer
    from fontTools.subset import Subsetter, Options
except ImportError:
    print("需要 fontTools：pip install fonttools")
    sys.exit(1)

SRC = r"C:\Windows\Fonts\NotoSansSC-VF.ttf"
OUT = r"D:\Project\imagagent\Imagagent\src\Imgagent.App\Assets\Fonts\NotoSansSC-Regular.ttf"

print("1) 加载可变字体…")
font = TTFont(SRC)
print(f"   表: {sorted(font.keys())[:12]}…")

# 2) 实例化到 wght=400（Regular）
print("2) 实例化 wght=400…")
if "fvar" in font:
    axes = {a.axisTag: a.defaultValue for a in font["fvar"].axes}
    print(f"   轴: {axes}")
    axes["wght"] = 400
    font = instancer.instantiateVariableFont(font, axes, inplace=False, updateFontNames=True)
    print("   实例化完成")
else:
    print("   非可变字体，跳过")

# 3) 子集化：只保留常用字符（大幅缩小体积）
print("3) 子集化（常用汉字 + 拉丁 + 标点）…")
options = Options()
options.layout_features = ["*"]
options.name_IDs = ["*"]
options.notdef_outline = True
options.recalc_bounds = True
options.drop_tables = ["DSIG"]

subsetter = Subsetter(options=options)

# 保留：ASCII + 常用标点 + CJK 统一汉字（U+4E00-U+9FFF）+ 中文标点 + 希腊/西里尔基础
chars = set()
chars.update(chr(c) for c in range(0x20, 0x7F))          # ASCII
chars.update(chr(c) for c in range(0xA0, 0x100))         # Latin-1
chars.update("　、。〈〉《》「」『』【】〔〕・ー―‐‑–—‘’“”…※→←↑↓■□●○◆◇★☆⚠✓✕")
chars.update(chr(c) for c in range(0x2000, 0x2070))      # 常用标点
chars.update(chr(c) for c in range(0x3000, 0x3040))      # CJK 标点
chars.update(chr(c) for c in range(0x4E00, 0xA000))      # CJK 统一汉字
chars.update(chr(c) for c in range(0xFF00, 0xFF61))      # 全角
chars.update("①②③④⑤⑥⑦⑧⑨⑩")

subsetter.populate(text="".join(sorted(chars)))
subsetter.subset(font)
print(f"   子集字符数: {len(chars)}")

# 4) 保存
print("4) 保存…")
out_path = pathlib.Path(OUT)
out_path.parent.mkdir(parents=True, exist_ok=True)
font.save(str(out_path))
font.close()

size_mb = out_path.stat().st_size / 1024 / 1024
print(f"\n完成: {out_path.name}  ({size_mb:.2f} MB)")
print(f"（原可变字体 16.95 MB → 静态子集 {size_mb:.2f} MB）")
