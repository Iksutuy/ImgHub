# -*- coding: utf-8 -*-
"""把 Material Symbols Outlined 可变字体实例化为**静态子集**，供 UI 图标使用。

为什么必须这么做（与 NotoSansSC 同一套理由，见 docs/CONSTRAINTS.md D6）：
  1. Avalonia 对可变字体（fvar/gvar/HVAR）的 wght 轴支持不完善 ——
     默认字重会被解析成过细实例，图标会**发虚/糊**；
  2. 原始 TTF 10.2 MB，全量嵌入对 APK/AOT 产物都是浪费 ——
     我们只用几十个码位，子集化后只有几十 KB。

产出：src/ImgHub.App/Assets/Fonts/MaterialSymbols.ttf（静态 + 子集）
许可：Apache-2.0（Google Material Design Icons 项目），见 NOTICE.md

用法：python tools/make-icon-font.py
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

ROOT = pathlib.Path(r"D:\Project\imagagent\Imagagent")
SRC = ROOT / "src/ImgHub.App/Assets/Fonts/MaterialSymbolsOutlined.ttf"
OUT = ROOT / "src/ImgHub.App/Assets/Fonts/MaterialSymbols.ttf"

# 实际在 UI 里用到的图标码位。新增图标时**必须**在这里补上再重跑本脚本，
# 否则子集字体里没有该字形 → 界面上显示成空白/方框。
ICON_CODEPOINTS = [
    0xE037,  # play_arrow    —— 生成
    0xE3C9,  # edit          —— 编辑 / 编辑图片
    0xE664,  # auto_fix_high —— 润色
    0xE872,  # delete        —— 清空
    0xE166,  # undo          —— 撤销
    0xE15A,  # redo          —— 重做
    0xE3B4,  # center_focus  —— 图片居中
    0xE171,  # download/save —— 保存到相册
    0xE2C8,  # folder_open   —— 数据目录
    0xE3F4,  # image         —— 系统看图器
    0xE8B9,  # settings      —— 设置
    0xE312,  # keyboard      —— 快捷键
    0xE88E,  # info          —— 蒙版说明
    0xE145,  # add           —— 添加参考图
    0xE15B,  # remove        —— 移除 / 缩小
    0xE8FF,  # zoom_in       —— 放大
    0xE900,  # zoom_out      —— 缩小
    0xE5D5,  # refresh       —— 复位
    0xE40A,  # palette       —— 调色板
    0xE3AE,  # brush         —— 画笔
    0xE3C4,  # crop_free     —— 方框
    0xE5CD,  # close         —— 关闭
    0xE5CA,  # check         —— 完成
    0xE51C,  # dark_mode     —— 深色
    0xE518,  # light_mode    —— 浅色
    0xE413,  # photo_library —— 多选 / 历史
    0xE89E,  # open_in_new   —— 导入
    0xE887,  # help          —— 帮助
    0xE429,  # tune          —— 高级参数 / 参数
    0xE53B,  # layers        —— 参考图
    0xE8B7,  # content_cut   —— 即梦素材提取
    0xEA19,  # menu_book     —— 提示词指南
    0xE5C4,  # navigate_before —— 上一条
    0xE5C8,  # navigate_next   —— 下一条
    0xE931,  # minimize        —— 窗口最小化
    0xE930,  # maximize        —— 窗口最大化
    0xE3C6,  # crop_square     —— 窗口还原
    0xE89E,  # open_in_new     —— 导入
]


def main() -> int:
    if not SRC.exists():
        print(f"缺少源字体：{SRC}")
        print("（从 google/material-design-icons 的 variablefont/ 目录下载 MaterialSymbolsOutlined[FILL,GRAD,opsz,wght].ttf）")
        return 1

    print(f"1) 加载 {SRC.name}（{SRC.stat().st_size / 1024 / 1024:.2f} MB）…")
    font = TTFont(str(SRC))
    print(f"   表: {sorted(font.keys())[:12]}…")

    # 2) 实例化到固定的 FILL=0 / GRAD=0 / opsz=24 / wght=400
    print("2) 实例化（FILL=0, GRAD=0, opsz=24, wght=400）…")
    if "fvar" in font:
        axes = {a.axisTag: a.defaultValue for a in font["fvar"].axes}
        print(f"   轴: {axes}")
        axes.update({"FILL": 0, "GRAD": 0, "opsz": 24, "wght": 400})
        font = instancer.instantiateVariableFont(font, axes, inplace=False, updateFontNames=True)
        print(f"   实例化完成；剩余表含 fvar？{'fvar' in font}")
    else:
        print("   非可变字体，跳过")

    # 3) 子集化：只保留用到的图标码位
    print(f"3) 子集化（{len(ICON_CODEPOINTS)} 个图标）…")
    options = Options()
    options.layout_features = []          # 图标字体不需要 OpenType 特性
    options.name_IDs = ["*"]              # 保留 name 表（含许可声明 nameID 13/14）
    options.notdef_outline = True
    options.recalc_bounds = True
    options.drop_tables = ["DSIG"]
    options.no_subset_tables += ["name"]  # name 表不参与子集

    subsetter = Subsetter(options=options)
    subsetter.populate(unicodes=ICON_CODEPOINTS)
    subsetter.subset(font)

    # 4) 校验：确认用到的码位都还在，且已无 fvar
    cmap = font.getBestCmap()
    missing = [cp for cp in ICON_CODEPOINTS if cp not in cmap]
    if missing:
        print(f"   ⚠️ 子集后缺少码位：{[hex(c) for c in missing]}")
        return 1
    if "fvar" in font:
        print("   ⚠️ 仍含 fvar（可变字体表）—— Avalonia 下会发虚")
        return 1

    # 5) 补齐许可声明（nameID 13 = License Description, 14 = License URL）。
    #    ⚠️ 实例化+子集会丢掉这两条，而 NOTICE.md 明确承诺"许可声明随字体保留" ——
    #       丢了就等于分发了一个没有许可声明的字体，属合规缺口。
    print("5) 写入许可声明（nameID 13/14）…")
    from fontTools.ttLib.tables._n_a_m_e import NameRecord
    lic_desc = (
        "Licensed under the Apache License, Version 2.0. "
        "Copyright 2020 Google LLC. "
        "Material Symbols is a trademark of Google LLC."
    )
    lic_url = "https://www.apache.org/licenses/LICENSE-2.0"
    name_tbl = font["name"]
    for nid, text in ((13, lic_desc), (14, lic_url)):
        name_tbl.names = [r for r in name_tbl.names if r.nameID != nid]
        rec = NameRecord()
        rec.nameID = nid
        rec.platformID = 3
        rec.platEncID = 1
        rec.langID = 0x409
        rec.string = text.encode("utf-16-be")
        name_tbl.names.append(rec)

    font.save(str(OUT))
    print(f"6) 已写出 {OUT.name}（{OUT.stat().st_size / 1024:.1f} KB）")
    print(f"   校验：{len(ICON_CODEPOINTS)} 个码位齐全，无 fvar，许可声明已写  (OK)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
