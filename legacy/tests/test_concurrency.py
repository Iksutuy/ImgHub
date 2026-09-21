#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""并发与性能测试（全部离线）。

重点守住几条**实测得出的结论**，防止以后改回去：
  * gather 必须保持输入顺序（否则历史记录乱序）
  * 单个任务失败只影响它自己
  * 空/单元素不开线程池
  * CPU 密集任务**不该**开线程（GIL 下实测更慢）-> CPU_WORKERS == 1
  * PNG 缩放性能不退化（2048->1024 优化后约 0.2s，优化前 2.15s）
  * 小图跳过解码（零拷贝）
  * 编码/解码像素级往返一致（反滤波被优化过，必须守住）
  * 落盘优先硬链接（省一次全量写盘）

用法：python3 tests/test_concurrency.py
"""
from __future__ import annotations

import os
import shutil
import struct
import sys
import tempfile
import time
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from impydroid import concurrency, pngcodec          # noqa: E402
from impydroid.placeholder import placeholder_png     # noqa: E402

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(errors="replace")
    except Exception:                                 # noqa: BLE001
        pass

RESULTS = []


def check(name, cond, extra=""):
    RESULTS.append((name, bool(cond), extra))


# ---------------------------------------------------------- gather 语义
def test_gather_order():
    def slow(x):
        time.sleep(0.15 - x * 0.04)        # 先提交的故意慢完成
        return x * 10

    t0 = time.perf_counter()
    r = concurrency.gather(slow, [0, 1, 2, 3])
    dt = time.perf_counter() - t0
    check("gather: 保持输入顺序", r == [0, 10, 20, 30], str(r))
    check("gather: 确实并发了（而非串行）", dt < 0.28, "%.2fs" % dt)


def test_gather_errors():
    def boom(x):
        if x == 1:
            raise ValueError("bad-1")
        return x

    r = concurrency.gather(boom, [0, 1, 2])
    got = [type(v).__name__ if isinstance(v, BaseException) else v for v in r]
    check("gather: 单个失败不影响其它", r[0] == 0 and isinstance(r[1], ValueError)
          and r[2] == 2, str(got))


def test_gather_edges():
    calls = {"n": 0}

    def fn(x):
        calls["n"] += 1
        return x

    check("gather: 空列表返回空", concurrency.gather(fn, []) == [])
    check("gather: 空列表不调用", calls["n"] == 0)
    check("gather: 单元素也能返回", concurrency.gather(fn, [7]) == [7])
    t0 = time.perf_counter()
    concurrency.gather(fn, [1])
    check("gather: 单元素很快（不开线程池）", time.perf_counter() - t0 < 0.05)
    ok = True
    for w in (0, 1, 2, 99):
        if concurrency.gather(fn, [1, 2, 3], workers=w) != [1, 2, 3]:
            ok = False
    check("gather: workers 各种取值都正确（含 0/99）", ok)


def test_map_unordered():
    r = concurrency.map_unordered(lambda x: x * 2, [1, 2, 3])
    check("map_unordered: 全部返回（顺序无关）", sorted(r) == [2, 4, 6], str(r))
    check("map_unordered: 空输入", concurrency.map_unordered(lambda x: x, []) == [])


# ---------------------------------------------------------- GIL 结论
def test_cpu_workers():
    check("CPU_WORKERS == 1（GIL 下并行无效）", concurrency.CPU_WORKERS == 1,
          str(concurrency.CPU_WORKERS))
    check("describe() 说明单线程", "单线程" in concurrency.describe(),
          concurrency.describe())


# ---------------------------------------------------------- PNG 性能
def test_resize_perf():
    data = placeholder_png("bench", 0, 2048)
    t0 = time.perf_counter()
    out, _ = pngcodec.shrink_for_reference(data, 1024)
    dt = time.perf_counter() - t0
    check("性能: 2048->1024 在 1.5s 内", dt < 1.5, "%.2fs" % dt)
    check("性能: 缩放结果尺寸正确", pngcodec.png_size(out) == (1024, 1024),
          str(pngcodec.png_size(out)))


def test_small_skips_decode():
    small = placeholder_png("s", 0, 256)
    t0 = time.perf_counter()
    out, media = pngcodec.shrink_for_reference(small, 1024)
    dt = time.perf_counter() - t0
    check("优化: 小图直接返回原字节（零拷贝）", out is small)
    check("优化: 小图不做解码（<10ms）", dt < 0.01, "%.1fms" % (dt * 1000))
    check("优化: 小图 media 正确", media == "image/png", media)


def test_resize_correctness():
    data = placeholder_png("c", 0, 512)
    w, h, rows = pngcodec.load_png_rgb(data)
    a = pngcodec.nearest_resize(w, h, rows, 128, workers=1)
    b = pngcodec.nearest_resize(w, h, rows, 128, workers=2)
    check("正确性: workers=1 与 2 结果一致",
          a[0] == b[0] and a[1] == b[1] and a[2] == b[2])
    check("正确性: 缩放后尺寸", a[0] == 128 and a[1] == 128, "%dx%d" % (a[0], a[1]))
    same = pngcodec.nearest_resize(w, h, rows, 4096)
    check("正确性: 无需缩放时原样返回", same[2] is rows)


def test_unfilter_roundtrip():
    w, h = 96, 64
    rows = [bytes((y * 3 + x * 5) % 256 for x in range(w * 3)) for y in range(h)]
    png = pngcodec.encode_png_rgb(w, h, rows)
    w2, h2, rows2 = pngcodec.load_png_rgb(png)
    check("往返: 尺寸一致", (w2, h2) == (w, h), "%dx%d" % (w2, h2))
    check("往返: 每行字节完全一致", rows2 == rows)

    def chunk(tag, payload):
        return (struct.pack(">I", len(payload)) + tag + payload
                + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))

    gray_raw = b"".join(b"\x00" + bytes([(y * 4) % 256] * w) for y in range(h))
    gray = (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 0, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(gray_raw)) + chunk(b"IEND", b""))
    gw, gh, grows = pngcodec.load_png_rgb(gray)
    ok = all(len(set(r[i:i + 3])) == 1 for r in grows for i in range(0, 9, 3))
    check("往返: 灰度图三通道相同", (gw, gh) == (w, h) and ok)


# ---------------------------------------------------------- 硬链接
def test_hardlink_save():
    from impydroid import settings, ui
    root = Path(tempfile.mkdtemp())
    album, home = root / "album", root / "home"
    album.mkdir(); home.mkdir()
    old = settings.HOME
    try:
        settings.set_home(home)
        src = album / "shot.png"
        src.write_bytes(b"P" * 5000)
        dest = ui._ensure_in_home(src, src.read_bytes())
        check("落盘: 目标文件存在", dest.exists(), str(dest))
        check("落盘: 内容一致", dest.read_bytes() == src.read_bytes())
        try:
            linked = os.stat(src).st_ino == os.stat(dest).st_ino
        except OSError:
            linked = False
        check("落盘: 优先硬链接（同 inode，零拷贝）", linked, "退回了复制")
        again = ui._ensure_in_home(dest, b"x")
        check("落盘: HOME 内文件不重复写", again == dest, str(again))
    finally:
        settings.set_home(old)
        shutil.rmtree(root, ignore_errors=True)


def main():
    print("concurrency & performance test suite")
    print("-" * 62)
    for fn in (test_gather_order, test_gather_errors, test_gather_edges,
               test_map_unordered, test_cpu_workers, test_resize_perf,
               test_small_skips_decode, test_resize_correctness,
               test_unfilter_roundtrip, test_hardlink_save):
        try:
            fn()
        except Exception as e:                       # noqa: BLE001
            import traceback
            check(fn.__name__ + " 崩溃", False,
                  "%s: %s\n%s" % (type(e).__name__, e, traceback.format_exc()[-300:]))
    passed = sum(1 for _, ok, _ in RESULTS if ok)
    for name, ok, extra in RESULTS:
        line = "  %s %s" % ("OK  " if ok else "FAIL", name)
        if extra and not ok:
            line += "\n         " + extra
        print(line)
    print("-" * 62)
    print("%d/%d passed" % (passed, len(RESULTS)))
    return 0 if passed == len(RESULTS) else 1


if __name__ == "__main__":
    sys.exit(main())
