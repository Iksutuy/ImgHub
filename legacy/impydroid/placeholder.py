"""离线占位图 —— 网络不通 / 被地区限制 / 不想花钱时也能把流程跑通。

确定性：同样的提示词+step 永远得到同一张图，便于复现与对拍。
"""


from __future__ import annotations
# ---------------------------------------------------------------------------
# 自举保护：本文件是包的内部模块。若在 Pydroid 里直接打开它点运行，
# 相对导入一定会失败（attempted relative import with no known parent package）。
# 这段代码把包目录补进 sys.path，然后**转交给真正的入口 main.py**。
# 正常被 import 时它什么都不做（零开销）。
if __name__ == "__main__" or __package__ in (None, ""):
    import os as _os
    import sys as _sys

    _HERE = _os.path.dirname(_os.path.abspath(__file__))
    _PARENT = _os.path.dirname(_HERE)
    if _PARENT not in _sys.path:
        _sys.path.insert(0, _PARENT)
    if __name__ == "__main__":
        _sys.stderr.write(
            "\n[!] %s 是包的内部模块，不是启动入口。\n"
            "    正确入口是同一目录下的 main.py：\n\n"
            "        %s\n\n"
            "    正在替你改用正确入口启动...\n\n"
            % (_os.path.basename(__file__), _os.path.join(_PARENT, "main.py")))
        try:
            from impydroid.app import cli as _cli
            _sys.exit(_cli())
        except SystemExit:
            raise
        except Exception as _e:
            _sys.stderr.write("启动失败：%s: %s\n" % (type(_e).__name__, _e))
            _sys.exit(2)
# ---------------------------------------------------------------------------

import zlib


from .pngcodec import encode_png_rgb



def _hsv(h: float, s: float, v: float) -> tuple[int, int, int]:
    h %= 1.0
    i = int(h * 6)
    f = h * 6 - i
    p_, q, t = v * (1 - s), v * (1 - f * s), v * (1 - (1 - f) * s)
    r, g, b = [(v, t, p_), (q, v, p_), (p_, v, t), (p_, q, v), (t, p_, v), (v, p_, q)][i % 6]
    return int(r * 255), int(g * 255), int(b * 255)


def placeholder_png(prompt: str, step: int = 0, size: int = 768) -> bytes:
    """按提示词生成一张渐变占位图。step 用画面上的一条色带表示"第几轮"。"""
    seed = zlib.crc32(prompt.encode("utf-8")) & 0xFFFFFFFF
    hue = (seed % 360) / 360.0
    hue2 = (hue + 0.18) % 1.0
    band = max(6, size // 28)
    band_y = (step % 20) * band
    rows: list[bytes] = []
    for y in range(size):
        v = y / size
        row = bytearray()
        for x in range(size):
            u = x / size
            t = u * 0.65 + v * 0.35
            if band_y <= y < band_y + band:
                r, g, b = _hsv(hue2, 0.85, 0.95)
            else:
                r, g, b = _hsv(hue + t * 0.25, 0.55 - 0.3 * v, 0.35 + 0.55 * (1 - v))
                if ((x + y) // 24 + (seed % 7)) % 7 == 0:
                    r, g, b = min(255, r + 22), min(255, g + 22), min(255, b + 22)
                if u < 0.03 or u > 0.97 or v < 0.03 or v > 0.97:
                    r, g, b = r // 3, g // 3, b // 3
            row += bytes((r, g, b))
        rows.append(bytes(row))
    return encode_png_rgb(size, size, rows)
