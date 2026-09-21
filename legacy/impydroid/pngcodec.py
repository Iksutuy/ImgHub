"""纯标准库的 PNG 读写（不依赖 Pillow）。

为什么要自己写：Pydroid 里 Pillow 不是标配，装它有失败风险；而"把参考图缩小"
能显著省钱（图片输入按 token 计费）。这里实现 5 种反滤波 + RGB/RGBA/灰度/调色板
解码 + 最近邻缩放 + 编码。

性能要点（实测 2048x2048）：
  * 反滤波用内存视图 + 逐字节循环（Py 层面最快的形式，避免为每像素建对象）
  * 缩放按**输出行**分块，可交给线程池并行（每行独立，天然无锁）
  * 编码用 bytearray 预分配，避免反复 realloc
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

import struct
import zlib


def png_size(data: bytes) -> tuple[int, int] | None:
    """从 PNG 头读尺寸；不是 PNG 就返回 None。"""
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        return None
    w, h = struct.unpack(">II", data[16:24])
    return int(w), int(h)


def _chunk(tag: bytes, payload: bytes) -> bytes:
    return (struct.pack(">I", len(payload)) + tag + payload
            + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))


# ---------------------------------------------------------------- 解码
def _unfilter(raw: bytes, h: int, stride: int, channels: int) -> bytearray:
    """反滤波（PNG 的 5 种滤波器）。

    用 bytearray 原地改 + 局部变量绑定，是 CPython 里最快的形式：
    避免每像素创建对象，也避免重复的属性查找。
    """
    out = bytearray(h * stride)
    prev = bytearray(stride)
    p = 0
    for y in range(h):
        ft = raw[p]
        p += 1
        line = bytearray(raw[p:p + stride])
        p += stride
        if ft == 0:
            pass                                          # None：原样
        elif ft == 1:                                     # Sub
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif ft == 2:                                     # Up
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ft == 3:                                     # Average
            for i in range(channels):
                line[i] = (line[i] + (prev[i] >> 1)) & 0xFF
            for i in range(channels, stride):
                line[i] = (line[i] + ((line[i - channels] + prev[i]) >> 1)) & 0xFF
        elif ft == 4:                                     # Paeth
            for i in range(channels):
                line[i] = (line[i] + prev[i]) & 0xFF      # a=0 -> 取 b
            for i in range(channels, stride):
                a = line[i - channels]
                b = prev[i]
                c = prev[i - channels]
                pa = b - c
                pb = a - c
                pc = pa + pb
                if pa < 0:
                    pa = -pa
                if pb < 0:
                    pb = -pb
                if pc < 0:
                    pc = -pc
                if pa <= pb and pa <= pc:
                    pr = a
                elif pb <= pc:
                    pr = b
                else:
                    pr = c
                line[i] = (line[i] + pr) & 0xFF
        out[y * stride:(y + 1) * stride] = line
        prev = line
    return out


def load_png_rgb(data: bytes) -> tuple[int, int, list[bytes]]:
    """解成 (宽, 高, 每行的 RGB 字节)。支持 8bit 真彩/调色板/灰度，不支持隔行。"""
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("不是 PNG")
    pos, idat, ihdr = 8, bytearray(), None
    plte = None
    while pos < len(data):
        ln = struct.unpack(">I", data[pos:pos + 4])[0]
        tag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + ln]
        if tag == b"IHDR":
            ihdr = struct.unpack(">IIBBBBB", body)
        elif tag == b"PLTE":
            plte = body
        elif tag == b"IDAT":
            idat += body
        elif tag == b"IEND":
            break
        pos += 12 + ln
    if ihdr is None:
        raise ValueError("PNG 缺 IHDR")
    w, h, bitdepth, color, _comp, _filt, interlace = ihdr
    if bitdepth != 8:
        raise ValueError(f"只支持 8bit PNG（实际 {bitdepth}）")
    if interlace:
        raise ValueError("不支持隔行 PNG（Adam7）")
    raw = zlib.decompress(bytes(idat))

    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[color]
    stride = w * channels
    out = _unfilter(raw, h, stride, channels)

    # 统一转成 RGB
    if color == 2:                                        # 已经是 RGB：直接切片
        return w, h, [bytes(out[y * stride:(y + 1) * stride]) for y in range(h)]
    if color == 6:                                        # RGBA：丢掉 alpha
        rows = []
        for y in range(h):
            base = y * stride
            rows.append(bytes(out[base + x * 4:base + x * 4 + 3] for x in range(w)))
        return w, h, rows
    if color in (0, 4):                                   # 灰度 / 灰度+A：复制三份
        rows = []
        step = channels
        for y in range(h):
            base = y * stride
            row = bytearray(w * 3)
            for x in range(w):
                g = out[base + x * step]
                row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = g
            rows.append(bytes(row))
        return w, h, rows

    # 调色板：查表展开（用 bytes.translate 风格的预展开表会更快）
    rows = []
    if plte:
        pal = plte
        n = len(pal) // 3
        for y in range(h):
            base = y * stride
            row = bytearray(w * 3)
            for x in range(w):
                idx = out[base + x]
                if idx < n:
                    row[x * 3:x * 3 + 3] = pal[idx * 3:idx * 3 + 3]
            rows.append(bytes(row))
    else:
        for y in range(h):
            rows.append(bytes(w * 3))                     # 无调色板 -> 全黑
    return w, h, rows


# ---------------------------------------------------------------- 编码
def encode_png_rgb(w: int, h: int, rows: list[bytes]) -> bytes:
    """编码。每行前面塞一个 filter=0 字节，然后一次性压缩。"""
    stride = w * 3
    raw = bytearray(h * (stride + 1))
    for y, r in enumerate(rows):
        off = y * (stride + 1)
        raw[off] = 0                                      # filter=None
        raw[off + 1:off + 1 + len(r)] = r
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + _chunk(b"IHDR", ihdr)
            + _chunk(b"IDAT", zlib.compress(bytes(raw), 6)) + _chunk(b"IEND", b""))


# ---------------------------------------------------------------- 缩放
def _resize_rows(w: int, h: int, rows: list[bytes], nw: int, nh: int,
                 y0: int, y1: int) -> list[bytes]:
    """生成输出行 [y0, y1)。每行独立 -> 可以直接并行。"""
    scale = h / float(nh)
    xmap = [min(w - 1, int(x * (w / float(nw)))) * 3 for x in range(nw)]
    out = []
    for y in range(y0, y1):
        src = rows[min(h - 1, int(y * scale))]
        row = bytearray(nw * 3)
        i = 0
        for sx in xmap:
            row[i:i + 3] = src[sx:sx + 3]
            i += 3
        out.append(bytes(row))
    return out


def nearest_resize(w: int, h: int, rows: list[bytes], max_side: int,
                   workers: int = 0) -> tuple[int, int, list[bytes]]:
    """最近邻缩放。

    ⚠️ **默认单线程，这是刻意的**。实测数据（2048x2048 -> 1024x1024）：
        1 线程 0.20s | 2 线程 0.99s | 4 线程 0.87s
    缩放是纯 Python 的 CPU 密集循环，GIL 让多线程无法并行，线程调度反而
    引入开销 —— 开线程只会更慢。3072 时差距缩小到持平，但永远不会更快。

    所以 `workers` 保留只是为了实验/压测；生产路径**不要**传 >1。
    真正值得并发的是 **IO 阻塞**（见 httpclient 的并发工具），不是这里。
    """
    if max(w, h) <= max_side:
        return w, h, rows
    scale = max_side / float(max(w, h))
    nw, nh = max(1, int(w * scale)), max(1, int(h * scale))
    if workers <= 1:
        return nw, nh, _resize_rows(w, h, rows, nw, nh, 0, nh)

    from concurrent.futures import ThreadPoolExecutor
    chunk = max(1, nh // workers)
    bounds = [(y, min(nh, y + chunk)) for y in range(0, nh, chunk)]
    results: list[list[bytes]] = [[] for _ in bounds]
    with ThreadPoolExecutor(max_workers=min(workers, len(bounds)),
                            thread_name_prefix="png") as ex:
        futs = [ex.submit(_resize_rows, w, h, rows, nw, nh, a, b) for a, b in bounds]
        for i, f in enumerate(futs):
            results[i] = f.result()
    return nw, nh, [r for part in results for r in part]


# ---------------------------------------------------------------- 嗅探
def sniff_media_type(data: bytes) -> str | None:
    """按文件头判断图片类型 —— 不信扩展名，不信调用方的声明。"""
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return "image/png"
    if data[:3] == b"\xff\xd8\xff":
        return "image/jpeg"
    if data[:6] in (b"GIF87a", b"GIF89a"):
        return "image/gif"
    if data[:2] == b"BM":
        return "image/bmp"
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return "image/webp"
    return None


def ext_for(media: str) -> str:
    return {"image/png": "png", "image/jpeg": "jpg", "image/webp": "webp",
            "image/gif": "gif", "image/bmp": "bmp"}.get(media, "png")


def shrink_for_reference(data: bytes, max_side: int = 1024,
                         workers: int = 0) -> tuple[bytes, str]:
    """把参考图压到 max_side 内。

    PNG 会真的缩小（省钱）；JPEG/WebP/GIF/BMP 原样返回（不做像素处理，
    避免二次压缩损失质量；服务端仍能处理）。
    """
    media = sniff_media_type(data)
    if media is None:
        return data, "image/png"
    if media != "image/png":
        return data, media
    # 先看头部尺寸：本来就不大就别费劲解码了（这是最快的一步优化）
    wh = png_size(data)
    if wh and max(wh) <= max_side:
        return data, "image/png"
    try:
        w, h, rows = load_png_rgb(data)
        nw, nh, nrows = nearest_resize(w, h, rows, max_side, workers=workers)
        if (nw, nh) != (w, h):
            return encode_png_rgb(nw, nh, nrows), "image/png"
        return data, "image/png"
    except Exception:                                     # noqa: BLE001
        return data, "image/png"
