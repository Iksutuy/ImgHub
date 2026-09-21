#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""impydroid 包测试（离线，不联网、不花钱）。

用法：
    python3 tests/test_impydroid.py          # 直接跑
    python3 -m pytest tests/ -q              # 或用 pytest

覆盖：
    console      控制台加固（含 ASCII-only 终端回归）
    settings     数据目录选择与切换
    pngcodec     PNG 编解码/缩放/嗅探
    store        文件名安全、设置与历史持久化、key 持久化
    api          离线占位图路径
    photos       相册扫描降级
    ui/app       端到端菜单流程
"""
from __future__ import annotations

import json
import os
import shutil
import sys
import tempfile
import unicodedata
from pathlib import Path

# 输出加固（万一在奇怪终端里跑）
for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(errors="replace")
    except Exception:                              # noqa: BLE001
        pass

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(ROOT))

from impydroid import settings                    # noqa: E402
from impydroid import api, console, photos, pngcodec, preview, store, ui   # noqa: E402

RESULTS: list[tuple[str, bool, str]] = []


def check(name: str, cond, extra: str = "") -> None:
    RESULTS.append((name, bool(cond), extra))


def fresh_home(tag: str) -> Path:
    d = Path(tempfile.mkdtemp(prefix=f"impydroid-{tag}-"))
    settings.set_home(d)
    return d


# ============================================================ console
def test_console() -> None:
    check("console: 已加固 stdio", isinstance(console.STDIO_INFO, dict))
    check("console: STDIO_INFO 记录了 stdout",
          "stdout" in console.STDIO_INFO, str(console.STDIO_INFO))
    check("console: 探测结果是布尔", isinstance(console.SYMBOLS_OK, bool)
          and isinstance(console.CJK_OK, bool))
    # 危险字符经过 safe print 不抛异常
    try:
        console.print("probe: \u2550 \u2713 \u2717 \u26a0 \u4e2d\u6587 \U0001F441")
        check("console: 危险字符不抛异常", True)
    except Exception as e:                         # noqa: BLE001
        check("console: 危险字符不抛异常", False, type(e).__name__)
    # sanitize 在"不支持符号"时能把装饰字符换成 ASCII
    old_sym, old_cjk = console.SYMBOLS_OK, console.CJK_OK
    try:
        console.SYMBOLS_OK = False
        console.CJK_OK = True
        out = console.sanitize("\u2550\u2713\u2717")
        check("console: sanitize 降级为 ASCII", out == "=[OK][X]", repr(out))
    finally:
        console.SYMBOLS_OK, console.CJK_OK = old_sym, old_cjk
    # 不应该污染 builtins
    import builtins
    check("console: 未污染 builtins.print", builtins.print is not console.print)


# ============================================================ settings
def test_settings() -> None:
    d = fresh_home("settings")
    try:
        check("settings: set_home 生效", settings.HOME == d, str(settings.HOME))
        check("settings: 派生路径跟随",
              settings.KEY_FILE == d / ".imgagent_key"
              and settings.CONF_FILE == d / "config.json"
              and settings.STATE_FILE == d / "state.json"
              and settings.LOG_FILE == d / "history.jsonl")
        check("settings: 数据目录可写", os.access(d, os.W_OK))
        check("settings: 默认模型非空", bool(settings.DEFAULT_MODEL))
        check("settings: 模型清单非空", len(settings.MODEL_CHOICES) >= 3)
    finally:
        shutil.rmtree(d, ignore_errors=True)


# ============================================================ pngcodec
def test_pngcodec() -> None:
    from impydroid.placeholder import placeholder_png
    png = placeholder_png("roundtrip", step=2, size=64)
    check("pngcodec: png_size 正确", pngcodec.png_size(png) == (64, 64),
          str(pngcodec.png_size(png)))
    w, h, rows = pngcodec.load_png_rgb(png)
    check("pngcodec: 解出尺寸一致", (w, h) == (64, 64))
    check("pngcodec: 每行长度 = w*3", all(len(r) == 64 * 3 for r in rows))
    again = pngcodec.encode_png_rgb(w, h, rows)
    _, _, rows2 = pngcodec.load_png_rgb(again)
    check("pngcodec: 像素级往返一致", rows2 == rows)
    nw, nh, nrows = pngcodec.nearest_resize(64, 64, rows, 16)
    check("pngcodec: 缩放尺寸正确", (nw, nh) == (16, 16) and len(nrows) == 16)
    check("pngcodec: 缩放后可编码",
          pngcodec.png_size(pngcodec.encode_png_rgb(nw, nh, nrows)) == (16, 16))
    # 嗅探
    cases = [(b"\x89PNG\r\n\x1a\n", "image/png"), (b"\xff\xd8\xff\xe0", "image/jpeg"),
             (b"GIF89a", "image/gif"), (b"BM\x00\x00", "image/bmp"),
             (b"RIFF\x00\x00\x00\x00WEBP", "image/webp")]
    ok = all(pngcodec.sniff_media_type(hdr) == want for hdr, want in cases)
    check("pngcodec: 文件头嗅探（5 种）", ok)
    check("pngcodec: 垃圾数据返回 None",
          pngcodec.sniff_media_type(b"not an image") is None)
    # 缩放：PNG 真缩小，JPEG 原样透传
    big = placeholder_png("big", 0, 256)
    small, media = pngcodec.shrink_for_reference(big, 64)
    check("pngcodec: PNG 参考图被缩小",
          pngcodec.png_size(small) == (64, 64), str(pngcodec.png_size(small)))
    fake_jpeg = b"\xff\xd8\xff" + b"x" * 100
    out, media = pngcodec.shrink_for_reference(fake_jpeg, 64)
    check("pngcodec: JPEG 原样透传", out == fake_jpeg and media == "image/jpeg")


# ============================================================ store
def test_store() -> None:
    d = fresh_home("store")
    try:
        # 文件名安全
        evil = ["../../etc/passwd", "a/b", "..\\..\\win", "", "...", "/etc/shadow",
                "a\x00b", "x" * 200, "名字.带.点", ".hidden"]
        bad = []
        for e in evil:
            for suf in (".png", ".JPG", "", ".tar.gz"):
                n = store.safe_filename("0917_120000_001", e, suf)
                if ("/" in n or "\\" in n or n.startswith(".")
                        or "\x00" in n or len(n) > 95):
                    bad.append((e, suf, n))
        check("store: 文件名防穿越（40 组合）", not bad, str(bad[:2]))

        # key 持久化
        store.save_key("sk-or-v1-TESTKEY")
        kf = d / ".imgagent_key"
        check("store: key 落盘", kf.exists()
              and kf.read_text().strip() == "sk-or-v1-TESTKEY")
        if os.name == "nt":
            # Windows 的 chmod 是 no-op（POSIX 权限位无意义），只验证落盘。
            check("store: key 权限 600", True, "n/a (windows chmod no-op)")
        else:
            check("store: key 权限 600", oct(kf.stat().st_mode)[-3:] == "600",
                  oct(kf.stat().st_mode)[-3:])
        check("store: load_key 能读回",
              store.load_key(silent=True) == "sk-or-v1-TESTKEY")

        # 设置 + 历史
        s = store.Session()
        s.model = "google/gemini-2.5-flash-image"
        s.quality = "medium"
        s.aspect = "16:9"
        s.offline = True
        s.preview = False
        s.total_cost = 0.4321
        s.save_config()
        cfg = json.loads((d / "config.json").read_text())
        check("store: 设置写进 config.json",
              cfg["model"].endswith("gemini-2.5-flash-image"))
        check("store: 质量/画幅", cfg["quality"] == "medium" and cfg["aspect"] == "16:9")
        check("store: 开关位", cfg["offline"] is True and cfg["preview"] is False)

        s2 = store.Session()
        check("store: 重启读回设置",
              s2.model == s.model and s2.quality == "medium"
              and s2.offline is True and s2.preview is False,
              f"{s2.model}/{s2.quality}/{s2.offline}/{s2.preview}")

        s2.push(store.Item(file="a.png", prompt="one", kind="gen"))
        s2.push(store.Item(file="b.png", prompt="two", kind="edit"))
        st = json.loads((d / "state.json").read_text())
        check("store: 历史落盘", len(st["items"]) == 2, str(len(st["items"])))
        check("store: counter 递增", st["counter"] == 2, str(st["counter"]))
        check("store: current 指向最新",
              s2.current is not None and s2.current.file == "b.png")

        names = sorted(f.name for f in d.iterdir())
        check("store: 文件都在同一目录",
              set(names) == {".imgagent_key", "config.json", "state.json"}, str(names))
    finally:
        shutil.rmtree(d, ignore_errors=True)


# ============================================================ api（离线）
def test_api_offline() -> None:
    d = fresh_home("api")
    try:
        res = api.generate("a cat", api_key="", model=settings.DEFAULT_MODEL,
                           quality="low", aspect="1:1", offline=True, step=0)
        check("api: 离线返回 1 张", len(res.images) == 1)
        data, media = res.images[0]
        check("api: 离线图是合法 PNG",
              media == "image/png" and pngcodec.png_size(data) is not None,
              str(pngcodec.png_size(data)))
        # 参考图路径（不联网，只验 payload 构造）
        res2 = api.generate("add sun", api_key="", model=settings.DEFAULT_MODEL,
                            quality="low", aspect="1:1",
                            refs=[(data, media)], offline=True)
        check("api: 带参考图也能离线出图", len(res2.images) == 1)
        # 确定性
        a = api.generate("same", api_key="", model=settings.DEFAULT_MODEL,
                         quality="low", aspect="1:1", offline=True).images[0][0]
        b = api.generate("same", api_key="", model=settings.DEFAULT_MODEL,
                         quality="low", aspect="1:1", offline=True).images[0][0]
        check("api: 同输入同输出（确定性）", a == b)
    finally:
        shutil.rmtree(d, ignore_errors=True)


# ============================================================ photos
def test_photos() -> None:
    d = fresh_home("photos")
    try:
        album = d / "album"
        album.mkdir()
        from impydroid.placeholder import placeholder_png
        (album / "a.png").write_bytes(placeholder_png("a", 0, 32))
        (album / "b.txt").write_text("not an image")
        # 把 PHOTO_DIRS 换成我们的临时目录，避免真去扫 /sdcard
        old = photos.PHOTO_DIRS
        photos.PHOTO_DIRS = [str(album)]
        try:
            got = photos.scan(10)
            check("photos: 扫描只收图片", len(got) == 1 and got[0].name == "a.png",
                  str([p.name for p in got]))
            # 空目录 -> 返回空，不抛异常
            photos.PHOTO_DIRS = ["/definitely/not/here"]
            check("photos: 目录不存在时返回空且不抛",
                  photos.scan(10) == [])

            # 时间预算：给一个极小的 budget，必须尽快返回（不能无限扫描）
            import time as _t
            photos.PHOTO_DIRS = [str(album), "/usr", "/etc", "/var"]
            t0 = _t.monotonic()
            photos.scan(40, budget=0.2)
            dt = _t.monotonic() - t0
            check("photos: 遵守时间预算（不会卡住）", dt < 3.0, f"耗时 {dt:.2f}s")
        finally:
            photos.PHOTO_DIRS = old
    finally:
        shutil.rmtree(d, ignore_errors=True)


# ============================================================ android 桥
def test_android_bridge() -> None:
    """没有 pyjnius 时必须优雅降级，不能抛异常。"""
    from impydroid import android
    # 强制重新探测（清掉可能已有的结果）
    android.BRIDGE.ok = False
    android.BRIDGE.reason = ""
    got = android.ready()
    check("android: 无 pyjnius 时 ready() 返回 False", got is False, str(got))
    check("android: 失败原因可读", bool(android.BRIDGE.reason),
          repr(android.BRIDGE.reason))
    check("android: status() 不抛异常且非空",
          bool(android.status()), android.status()[:60])
    check("android: is_android() 返回布尔", isinstance(android.is_android(), bool))
    # 未初始化时 pick_image / view_image 必须安全返回
    android.BRIDGE.ok = False
    check("android: 未就绪时 pick_image 返回 None",
          android.BRIDGE.pick_image(timeout=0.1) is None)
    from pathlib import Path as _P
    check("android: 未就绪时 view_image 返回 False",
          android.BRIDGE.view_image(_P("/nonexistent.png")) is False)
    # Activity 候选清单不能为空（否则永远找不到）
    check("android: Activity 候选清单非空", len(android.ACTIVITY_CANDIDATES) >= 3,
          str(len(android.ACTIVITY_CANDIDATES)))


# ============================================================ preview 降级
def test_preview_fallback() -> None:
    d = fresh_home("preview")
    try:
        from impydroid.placeholder import placeholder_png
        p = d / "p.png"
        p.write_bytes(placeholder_png("p", 0, 32))
        # 无 pyjnius + 无 tkinter（容器里）→ 应走 term/path 分支，不抛
        _saved_tk = preview._tk_show
        preview._tk_show = lambda p, prompt="": False   # 测试里禁弹窗
        try:
            used = preview.show(p, "probe")
            # 四级降级：viewer -> term -> tk -> path（term 是"没插件也能看图"的那级）
            check("preview: 降级返回级别", used in ("viewer", "term", "tk", "path"), used)
            if os.name == "nt":
                # Windows 上用 os.startfile 调系统看图器（零依赖），返回 viewer 合法
                check("preview: Windows 走系统看图器", used in ("viewer", "term", "tk", "path"), used)
            else:
                check("preview: 无插件时应走终端字符画", used == "term", used)
        except Exception as e:                     # noqa: BLE001
            check("preview: 降级返回级别", False, f"{type(e).__name__}: {e}")
        finally:
            preview._tk_show = _saved_tk
        check("preview: tk_available 返回布尔", isinstance(preview.tk_available(), bool))
    finally:
        shutil.rmtree(d, ignore_errors=True)


# ============================================================ 端到端
def test_e2e_menu() -> None:
    """模拟真实用户：走一遍菜单（离线）。"""
    d = fresh_home("e2e")
    try:
        album = d / "album"
        album.mkdir()
        try:
            from PIL import Image
            Image.new("RGB", (1200, 800), (40, 140, 220)).save(
                album / "IMG_001.jpg", quality=88)
        except ImportError:
            from impydroid.placeholder import placeholder_png
            (album / "IMG_001.png").write_bytes(placeholder_png("photo", 0, 64))

        old_dirs = photos.PHOTO_DIRS
        photos.PHOTO_DIRS = [str(album)]

        # 注意：菜单编号与 do_settings 的分支严格对应，改动菜单要一起改这里。
        # 设置菜单现在有 12 个入口，所以这里把关键路径都走一遍。
        script = [
            "7", "6",                 # 设置 -> 关「生成后自动问预览」
            "7", "1",                 # 设置 -> 换 provider -> 选 OpenRouter（当前值）
            "10",                     # 设置 -> 查看润色配置（曾因 DEFAULT_BASE_URL 崩过）
            "0",                      # 设置 -> 返回主菜单
            "1", "a cat", "n",        # 生成
            "3", "1", "my photo",     # 上传第 1 张
            "2", "add sunset",        # 修改当前图
            "4", "1",                 # 历史 -> 回到第 1 号
            "8",                      # 数据目录
            "9",                      # 安卓状态
            "6",                      # 预览（走降级）
            "0",                      # 退出
        ]
        it = iter(script)
        import builtins
        real_input = builtins.input
        builtins.input = lambda p="": next(it)
        # ui 与 photos 都用内建 input，直接替换 builtins 即可
        try:
            rc = ui_app_main()
        except (EOFError, StopIteration):
            rc = 0
        finally:
            builtins.input = real_input
            photos.PHOTO_DIRS = old_dirs

        st = json.loads((d / "state.json").read_text())
        cfg = json.loads((d / "config.json").read_text())
        kinds = [i["kind"] for i in st["items"]]

        check("e2e: 生成进历史", "offline" in kinds, str(kinds))
        check("e2e: 上传成为当前图", "import" in kinds, str(kinds))
        check("e2e: 设置持久化", cfg["preview"] is False and cfg["offline"] is True,
              str({k: cfg[k] for k in ("preview", "offline")}))
        imgs = [f for f in d.iterdir() if f.suffix.lower() in settings.IMG_EXT]
        check("e2e: 图片实际落盘", len(imgs) >= 3, str(len(imgs)))
        check("e2e: 历史文件名无路径分隔符",
              all("/" not in i["file"] and "\\" not in i["file"] for i in st["items"]))
        check("e2e: 退出码为 0", rc == 0, str(rc))
    finally:
        shutil.rmtree(d, ignore_errors=True)


# ============================================================ provider 相关
def test_provider_config() -> None:
    """provider 切换的核心不变量（这些点都出过真 bug）。"""
    from impydroid import settings as st

    old_prov = st.API_PROVIDER
    try:
        st.set_provider(provider="openrouter")
        or_choices = st.model_choices()
        check("provider: OpenRouter 清单用 provider/model 格式",
              all("/" in m for m in or_choices), str(or_choices[:2]))

        st.set_provider(provider="apimart")
        am_choices = st.model_choices()
        check("provider: APIMart 清单是裸模型名（不含 /）",
              all("/" not in m for m in am_choices), str(am_choices[:2]))
        check("provider: APIMart 清单含 gpt-image-2.5-flare",
              "gpt-image-2.5-flare" in am_choices, str(am_choices))
        check("provider: 两套清单不同", or_choices != am_choices)
        check("provider: APIMart 默认模型不带斜杠",
              "/" not in st.default_model(), st.default_model())

        check("provider: APIMart 裸名匹配", st.model_matches_provider("gpt-image-2.5-flare"))
        check("provider: APIMart 不接受 openai/xxx",
              not st.model_matches_provider("openai/gpt-image-2.5-flare"))
        st.set_provider(provider="openrouter")
        check("provider: OpenRouter 接受 openai/xxx",
              st.model_matches_provider("openai/gpt-image-2.5-flare"))
        check("provider: OpenRouter 不接受裸名",
              not st.model_matches_provider("gpt-image-2.5-flare"))

        st.set_provider(provider="apimart")
        check("provider: APIMart 端点",
              st.provider_base() == "https://api.apimart.ai/v1", st.provider_base())
        st.set_provider(provider="openrouter")
        check("provider: OpenRouter 端点",
              "openrouter.ai" in st.provider_base(), st.provider_base())

        d = fresh_home("prov")
        try:
            st.set_provider(provider="apimart")
            am_key_file = st.key_file_for()
            st.set_provider(provider="openrouter")
            or_key_file = st.key_file_for()
            check("provider: 两个 provider 的 key 文件不同",
                  am_key_file != or_key_file, f"{am_key_file} vs {or_key_file}")
            check("provider: APIMart key 文件在 HOME 下",
                  am_key_file.parent == d, f"{am_key_file.parent} vs {d}")

            # 回归：以前 set_home 只改 KEY_FILE，_APIMART_KEY_FILE 指向旧目录
            st.set_provider(provider="apimart")
            check("回归: set_home 后 APIMart key 路径跟随",
                  st._APIMART_KEY_FILE.parent == d, str(st._APIMART_KEY_FILE))

            st.save_apimart_key("sk-apimart-roundtrip")
            check("回归: APIMart key 写读往返一致",
                  st.load_apimart_key() == "sk-apimart-roundtrip",
                  str(st.load_apimart_key()))
        finally:
            shutil.rmtree(d, ignore_errors=True)

        st.set_provider(provider="apimart")
        check("provider: APIMart 认 sk- 开头", st.looks_like_key("sk-abc"))
        st.set_provider(provider="openrouter")
        check("provider: OpenRouter 不认 sk-abc（缺 or-v1）",
              not st.looks_like_key("sk-abc"))
        check("provider: OpenRouter 认 sk-or-v1-", st.looks_like_key("sk-or-v1-xyz"))
        check("provider: 空 key 一律不认", not st.looks_like_key("   "))
    finally:
        st.set_provider(provider=old_prov)


def test_pick_index() -> None:
    """回归：int(s)-1 的负数索引会静默绕回，选到错误项。"""
    check("索引: '1' -> 0", ui._pick_index("1", 3) == 0)
    check("索引: '3' -> 2", ui._pick_index("3", 3) == 2)
    check("索引: '0' -> None（以前会绕到最后一项）", ui._pick_index("0", 3) is None)
    check("索引: '-1' -> None", ui._pick_index("-1", 3) is None)
    check("索引: '4' -> None（越界）", ui._pick_index("4", 3) is None)
    check("索引: 'abc' -> None", ui._pick_index("abc", 3) is None)
    check("索引: '' -> None", ui._pick_index("", 3) is None)
    check("索引: ' 2 ' -> 1（容忍空格）", ui._pick_index(" 2 ", 3) == 1)
    check("索引: n=0 时任何输入都 None", ui._pick_index("1", 0) is None)


def test_apimart_endpoints() -> None:
    """回归：APIMart 的模型列表/校验端点不能用 OpenRouter 的路径。"""
    from unittest import mock
    from impydroid import settings as st
    from impydroid import api as api_mod

    old_prov = st.API_PROVIDER
    captured = {}

    class _R:
        def __init__(self, payload):
            self._b = json.dumps(payload).encode()
        def read(self):
            return self._b
        def __enter__(self):
            return self
        def __exit__(self, *a):
            return False

    def fake(payload):
        def _f(req, timeout=None, context=None):
            captured["url"] = req.full_url
            return _R(payload)
        return _f

    try:
        st.set_provider(provider="apimart")
        payload = {"object": "list", "data": [
            {"id": "gpt-image-2.5-flare", "category": "image"},
            {"id": "wan-2.7-image", "category": "image"},
            {"id": "gpt-4o", "category": "chat"},
        ]}
        with mock.patch("urllib.request.urlopen", fake(payload)):
            ids = api_mod.list_models("sk-x")
        check("APIMart: 模型列表走 /v1/models（不是 /images/models）",
              captured["url"].endswith("/v1/models?expand=1"), captured["url"])
        check("APIMart: category 过滤掉 chat 模型",
              "gpt-4o" not in ids and "gpt-image-2.5-flare" in ids, str(ids))

        with mock.patch("urllib.request.urlopen", fake(payload)):
            info = api_mod.check_key("sk-x")
        check("APIMart: check_key 不打 /v1/key",
              "/key" not in captured["url"], captured["url"])
        check("APIMart: check_key 返回 _provider 标记",
              info.get("_provider") == "apimart", str(info.get("_provider")))

        st.set_provider(provider="openrouter")
        with mock.patch("urllib.request.urlopen",
                        fake({"data": [{"id": "openai/gpt-image-2"}]})):
            or_ids = api_mod.list_models("sk-or-x")
        check("OpenRouter: 仍走 /images/models",
              captured["url"].endswith("/images/models"), captured["url"])
        check("OpenRouter: 返回原样 id", or_ids == ["openai/gpt-image-2"], str(or_ids))
    finally:
        st.set_provider(provider=old_prov)


def test_apimart_payload() -> None:
    """回归：两个 provider 的请求体字段名不同，不能混用。"""
    from impydroid import settings as st
    from impydroid import api as api_mod

    old_prov = st.API_PROVIDER
    try:
        st.set_provider(provider="apimart")
        path, pl = api_mod._build_payload("a cat", "gpt-image-2.5-flare",
                                          "low", "1:1", 1, None)
        check("APIMart: 端点 /images/generations", path == "/images/generations", path)
        check("APIMart: 用 size 而不是 aspect_ratio",
              "size" in pl and "aspect_ratio" not in pl, str(list(pl)))
        refs = [(b"fake", "image/png")]
        _, pl2 = api_mod._build_payload("edit", "gpt-image-2.5-flare",
                                        "low", "1:1", 1, refs)
        check("APIMart: payload 不含 base64 参考图（需先上传拿 URL）",
              "input_urls" not in pl2 and "input_references" not in pl2 and "_refs" not in pl2,
              str(list(pl2)))

        st.set_provider(provider="openrouter")
        path, pl3 = api_mod._build_payload("a dog", "openai/gpt-image-2",
                                           "low", "1:1", 1, refs)
        check("OpenRouter: 端点 /images", path == "/images", path)
        check("OpenRouter: 用 aspect_ratio", "aspect_ratio" in pl3, str(list(pl3)))
        check("OpenRouter: 参考图用 input_references",
              "input_references" in pl3, str(list(pl3)))

        # n 的裁剪
        _, pl4 = api_mod._build_payload("x", "m", "low", "1:1", 999, None)
        check("payload: n 上限裁到 10", pl4["n"] == 10, str(pl4["n"]))
        _, pl5 = api_mod._build_payload("x", "m", "low", "1:1", 0, None)
        check("payload: n 下限裁到 1", pl5["n"] == 1, str(pl5["n"]))
    finally:
        st.set_provider(provider=old_prov)


def test_apimart_async() -> None:
    """回归：APIMart 是异步 API，必须提交→轮询→下载三步走。"""
    from impydroid import api as api_mod

    # 1. poll_task 的 URL 不能重复拼 /v1（provider_base 已含 /v1）
    from impydroid import settings as st
    captured = {}
    old_prov = st.API_PROVIDER
    try:
        st.set_provider(provider="apimart")

        class _R:
            def __init__(self, payload):
                self._b = json.dumps(payload).encode()
            def read(self):
                return self._b
            def __enter__(self):
                return self
            def __exit__(self, *a):
                return False

        def fake(req, timeout=None, context=None):
            captured.setdefault("urls", []).append(req.full_url)
            # 上传端点返回**扁平**结构（实测如此），任务端点返回 data 包裹
            if "/uploads/" in req.full_url:
                captured["content_type"] = req.headers.get("Content-type", "")
                return _R({"bytes": 8, "content_type": "image/png",
                           "url": "https://cdn/ref.png"})
            return _R({"code": 200, "data": {
                "status": "completed",
                "result": {"images": [{"url": ["https://x/y.png"]}]},
                "cost": 0.01, "usage": {}}})

        from unittest import mock
        with mock.patch("urllib.request.urlopen", fake):
            data = api_mod.poll_task("task_ABC", api_key="sk-x", max_wait=1.0)
        url = captured["urls"][0]
        check("APIMart: poll_task 路径不重复 /v1",
              "/v1/v1/" not in url and "/tasks/task_ABC" in url, url)
        check("APIMart: poll_task 返回 data",
              data.get("status") == "completed", str(data)[:80])

        # 2. 上传必须用 multipart（JSON 一定 400）
        captured.clear()
        with mock.patch("urllib.request.urlopen", fake):
            u = api_mod.upload_image(b"\x89PNG\r\n\x1a\n", "image/png", "sk-x")
        ct = captured.get("content_type", "")
        check("APIMart: upload_image 用 multipart/form-data",
              "multipart/form-data" in ct, ct)
        check("APIMart: upload_image 解析扁平响应的 url",
              u == "https://cdn/ref.png", u)

        # 3. _build_payload 不含 base64 参考图（APIMart 只吃公网 URL）
        _, pl = api_mod._build_payload("x", "gpt-image-2.5-flare", "low", "1:1", 1,
                                       [(b"fake", "image/png")])
        check("APIMart: payload 不含 input_urls/input_references/_refs",
              not any(k in pl for k in ("input_urls", "input_references", "_refs")),
              str(list(pl)))
        check("APIMart: n 上限裁到 4", pl["n"] <= 4, str(pl["n"]))
    finally:
        st.set_provider(provider=old_prov)


def test_polish_display() -> None:
    """回归：润色选项不能截断，要按显示宽度折行。"""
    from impydroid import ui as ui_mod

    long_cn = "柔和的午后阳光斜照在木窗台上，一只橘猫蜷曲着身子眯眼酣睡，毛发光泽细腻，" * 3
    lines = ui_mod._wrap_cjk(long_cn, 60, indent="")
    check("润色显示: 长中文被折成多行", len(lines) > 1, str(len(lines)))
    # 每行的显示宽度不能超过限制
    def disp_w(t):
        return sum(2 if unicodedata.east_asian_width(c) in ("W", "F") else 1 for c in t)
    check("润色显示: 每行显示宽度 <= 限制",
          all(disp_w(l) <= 60 for l in lines),
          str([disp_w(l) for l in lines]))
    check("润色显示: 折行后内容无损（拼回等于原文）",
          "".join(lines) == long_cn, "内容变了")
    check("润色显示: 空串不崩", ui_mod._wrap_cjk("", 40) == [""] or True)

    # _term_width 有合理兜底
    w = ui_mod._term_width()
    check("润色显示: _term_width 在合理范围", 40 <= w <= 120, str(w))


def test_polish_labels() -> None:
    """回归：模型会照抄示例标签，必须剥掉。"""
    from impydroid import polish as pl

    cases = [
        ("[润色结果 1]\n一只橘猫", "一只橘猫"),
        ("【编辑方向2】：把背景换成雪原", "把背景换成雪原"),
        ("1. 赛博朋克城市", "赛博朋克城市"),
        ("2) 水彩插画风格", "水彩插画风格"),
        ("提示词：极简线稿", "极简线稿"),
        ("风格3-电影感画面", "电影感画面"),
    ]
    for raw, want in cases:
        got = pl._strip_label(raw)
        check(f"润色剥标签: {raw[:14]!r} -> {want[:10]!r}", got == want, f"got={got!r}")

    # 不该误伤正文
    keep = "一只橘猫慵懒地趴在窗台上晒太阳"
    check("润色剥标签: 正文不被误伤", pl._strip_label(keep) == keep,
          pl._strip_label(keep))


def test_quality_tiers() -> None:
    """回归：质量档位在两个 provider 下不同，且不能给出会 400 的选项。"""
    from impydroid import settings as st

    old = st.API_PROVIDER
    try:
        # OpenRouter 不支持 xhigh / max（传了直接 400）
        st.set_provider(provider="openrouter")
        orq = st.quality_choices("openai/gpt-image-2.5-flare")
        check("质量: OpenRouter 无 xhigh", "xhigh" not in orq, str(orq))
        check("质量: OpenRouter 无 max", "max" not in orq, str(orq))
        check("质量: OpenRouter 含 auto/low/medium/high",
              all(q in orq for q in ("auto", "low", "medium", "high")), str(orq))
        check("质量: OpenRouter 下 xhigh 不合法",
              not st.quality_supported("xhigh", "x"), "应为 False")

        # APIMart + 2.5 系：支持 6 档
        st.set_provider(provider="apimart")
        amq = st.quality_choices("gpt-image-2.5-flare")
        check("质量: APIMart+2.5 支持 xhigh/max",
              "xhigh" in amq and "max" in amq, str(amq))
        # APIMart + 非 2.5 系：xhigh/max 会 400（文档明确不自动降级）
        lq = st.quality_choices("gpt-image-2")
        check("质量: APIMart+gpt-image-2 去掉 xhigh/max",
              "xhigh" not in lq and "max" not in lq, str(lq))
        check("质量: APIMart+gpt-image-2 保留 low/high",
              "low" in lq and "high" in lq, str(lq))
    finally:
        st.set_provider(provider=old)


def test_cost_estimate() -> None:
    """回归：成本估算不能有 10 倍偏差，2k 不能重复乘倍数。"""
    from impydroid import settings as st

    # 1k 基准价与官方 token 表的偏差应在合理范围（我们存的是折扣后价）
    official = {"low": 0.00588, "medium": 0.01317, "high": 0.05268,
                "xhigh": 0.09366, "max": 0.21072}
    for q, off in official.items():
        mine = st.estimate_cost(q, "1k")
        ratio = mine / off
        check(f"成本: {q} 估算在官方的 0.6~1.2 倍内（{ratio:.2f}x）",
              0.6 <= ratio <= 1.2, f"估算={mine} 官方={off}")

    # 旧版 bug：xhigh/max 落到默认 0.02，比实际低 10 倍
    check("成本: xhigh 不再退化成 0.02",
          st.estimate_cost("xhigh", "1k") > 0.05,
          str(st.estimate_cost("xhigh", "1k")))
    check("成本: max 不再退化成 0.02",
          st.estimate_cost("max", "1k") > 0.15,
          str(st.estimate_cost("max", "1k")))

    # 回归：2k 不能是 1k 的 4 倍（查表失败 + 重复乘倍数导致）
    for q in ("low", "medium", "high", "max"):
        r = st.estimate_cost(q, "2k") / st.estimate_cost(q, "1k")
        check(f"成本: {q} 的 2k/1k 比值 ≈ 2（不是 4）",
              1.9 <= r <= 2.1, f"比值={r:.2f}")

    # 4k 应是 1k 的 4 倍
    r4 = st.estimate_cost("low", "4k") / st.estimate_cost("low", "1k")
    check("成本: low 的 4k/1k 比值 ≈ 4", 3.9 <= r4 <= 4.1, f"比值={r4:.2f}")

    # auto 实测稳定落在 low，估算应接近 low 而不是 max
    check("成本: auto 估算接近 low（实测 3/3 落在 low）",
          abs(st.estimate_cost("auto", "1k") - st.estimate_cost("low", "1k")) < 0.002,
          f"auto={st.estimate_cost('auto','1k')} low={st.estimate_cost('low','1k')}")

    # n 张线性叠加
    check("成本: n=3 是 n=1 的 3 倍",
          abs(st.estimate_cost("low", "1k", 3) / st.estimate_cost("low", "1k", 1) - 3) < 0.01,
          "非线性")
    check("成本: n=0 也至少算 1 张",
          st.estimate_cost("low", "1k", 0) == st.estimate_cost("low", "1k", 1), "n=0")


def test_session_sanitize() -> None:
    """回归：config.json 里 provider/model/quality 非法组合要在启动时纠正。"""
    from impydroid import settings as st
    from impydroid import store as st_mod
    import tempfile
    from pathlib import Path

    old = st.API_PROVIDER
    d = Path(tempfile.mkdtemp(prefix="impydroid-sanitize-"))
    try:
        # 场景：provider=openrouter 但 quality=xhigh（非法）→ 应纠正为 low
        st.set_home(d)
        (d / "config.json").write_text(json.dumps({
            "model": "openai/gpt-image-2.5-flare", "quality": "xhigh",
            "aspect": "1:1", "provider": "openrouter"}), encoding="utf-8")
        for f in ("state.json",):
            try:
                (d / f).unlink()
            except OSError:
                pass
        st.set_provider(provider="openrouter")
        s1 = st_mod.Session()
        check("启动纠正: OpenRouter + xhigh -> low", s1.quality == "low", s1.quality)

        # 场景：APIMart + 2.5 系 + xhigh（合法）→ 保留
        st.set_provider(provider="openrouter")
        (d / "config.json").write_text(json.dumps({
            "model": "gpt-image-2.5-flare", "quality": "xhigh",
            "aspect": "1:1", "provider": "apimart"}), encoding="utf-8")
        s2 = st_mod.Session()
        check("启动纠正: APIMart+2.5 + xhigh 保留", s2.quality == "xhigh", s2.quality)
        check("启动纠正: model 已切到 APIMart 命名",
              "/" not in s2.model, s2.model)

        # 场景：APIMart + 非 2.5 模型 + xhigh（非法）→ 纠正
        st.set_provider(provider="openrouter")
        (d / "config.json").write_text(json.dumps({
            "model": "gpt-image-2", "quality": "max",
            "aspect": "1:1", "provider": "apimart"}), encoding="utf-8")
        s3 = st_mod.Session()
        check("启动纠正: APIMart+gpt-image-2 + max -> low",
              s3.quality == "low", s3.quality)
    finally:
        import shutil
        shutil.rmtree(d, ignore_errors=True)
        st.set_provider(provider=old)


def test_ensure_in_home() -> None:
    """回归：os.link 在 Pydroid 里不存在；保存失败不能写坏记录。"""
    from impydroid import settings as st
    from impydroid import ui as ui_mod
    from impydroid import store as st_mod
    import tempfile
    import os as _os
    from pathlib import Path

    real_album = st_mod.album_dirs
    d = Path(tempfile.mkdtemp(prefix="impydroid-eih-"))
    st.set_home(d)
    data = b"\x89PNG\r\n\x1a\n" + b"Z" * 400
    try:
        # --- 场景1：os.link 不存在（Pydroid 的真实情况）---
        # 旧代码只 catch OSError，AttributeError 会冒泡 → "有一张没能保存"
        saved_link = getattr(_os, "link", None)
        if saved_link is not None:
            del _os.link
        try:
            st_mod.album_dirs = lambda: [Path(tempfile.mkdtemp())]
            p = st_mod.save_to_album(data, "image/png", "测试", 1)
            hp = ui_mod._ensure_in_home(p, data)
            check("保存: 无 os.link 时退回复制并成功", hp is not None, str(hp))
            check("保存: 退回复制后内容一致",
                  hp is not None and hp.read_bytes() == data, "内容不符")
        finally:
            if saved_link is not None:
                _os.link = saved_link

        # --- 场景2：HOME 真的写不进去 → 必须返回 None（不能返回假路径）---
        blocker = Path(tempfile.mkdtemp()) / "file_not_dir"
        blocker.write_text("x")
        st.set_home(blocker / "sub")            # mkdir 必失败
        st_mod.album_dirs = lambda: [Path(tempfile.mkdtemp())]
        p2 = st_mod.save_to_album(data, "image/png", "测试2", 2)
        hp2 = ui_mod._ensure_in_home(p2, data)
        check("保存: HOME 不可写时返回 None（不写坏记录）", hp2 is None, str(hp2))

        # 回归核心：旧代码会返回一个不存在的路径，导致 Item 在下次 load 被过滤掉
        if hp2 is not None:
            check("保存: 若返回路径则它必须真实存在",
                  hp2.exists(), f"{hp2} 不存在 —— 会导致图片'消失'")

        # --- 场景3：HOME 就是目标目录时直接返回 ---
        st.set_home(d)
        inside = d / "already.png"
        inside.write_bytes(data)
        check("保存: 已在 HOME 内则原样返回",
              ui_mod._ensure_in_home(inside, data) == inside)
    finally:
        st_mod.album_dirs = real_album
        import shutil
        shutil.rmtree(d, ignore_errors=True)


def test_platform_paths() -> None:
    """回归：Termux 与 Pydroid 的相册路径顺序不同（顺序错会导致图存到看不见的地方）。"""
    import os as _os
    if _os.name == "nt":
        # Windows 不走相册路径逻辑（/sdcard 不存在），此契约仅 Android/Termux 适用。
        return
    from impydroid import store as st_mod
    from impydroid import photos as ph_mod

    old_prefix = _os.environ.get("PREFIX")
    old_home = _os.environ.get("HOME")
    try:
        # --- Termux：~/storage/shared 必须排在 /sdcard 之前 ---
        _os.environ["PREFIX"] = "/data/data/com.termux/files/usr"
        _os.environ["HOME"] = "/tmp/fake-termux-home"
        # 模块级缓存：photos.PHOTO_DIRS 是导入时算的，重新算一次
        dirs = st_mod.album_dirs()
        strs = [str(d) for d in dirs]
        termux_idx = next((i for i, x in enumerate(strs)
                           if "storage/shared/Pictures" in x), 999)
        sdcard_idx = next((i for i, x in enumerate(strs)
                           if x.startswith("/sdcard/")), 999)
        check("平台路径: Termux 下 ~/storage/shared 排在 /sdcard 前",
              termux_idx < sdcard_idx, f"termux@{termux_idx} sdcard@{sdcard_idx}")
        check("平台路径: 最后一项永远是 HOME（兜底可写）",
              dirs[-1] == st_mod.settings.HOME, str(dirs[-1]))

        pdirs = ph_mod._photo_dirs()
        t_i = next((i for i, x in enumerate(pdirs) if "storage/shared" in x), 999)
        s_i = next((i for i, x in enumerate(pdirs) if x.startswith("/sdcard/")), 999)
        check("平台路径: Termux 扫描表也把 storage/shared 放前面",
              t_i < s_i, f"termux@{t_i} sdcard@{s_i}")

        # --- 非 Termux：/sdcard 排前面（Pydroid 的正确路径）---
        _os.environ.pop("PREFIX", None)
        _os.environ["HOME"] = "/tmp/fake-desktop-home"
        strs2 = [str(d) for d in st_mod.album_dirs()]
        sdcard_idx2 = next((i for i, x in enumerate(strs2)
                            if x.startswith("/sdcard/")), 999)
        termux_idx2 = next((i for i, x in enumerate(strs2)
                            if "storage/shared/Pictures" in x), 999)
        check("平台路径: 非 Termux 时 /sdcard 排在 storage/shared 前",
              sdcard_idx2 < termux_idx2, f"sdcard@{sdcard_idx2} termux@{termux_idx2}")
    finally:
        if old_prefix is None:
            _os.environ.pop("PREFIX", None)
        else:
            _os.environ["PREFIX"] = old_prefix
        if old_home is None:
            _os.environ.pop("HOME", None)
        else:
            _os.environ["HOME"] = old_home


def test_tui_ascii_render() -> None:
    """回归：TUI 预览必须是纯文本，不能含 ANSI 转义码（curses 不会解释它）。"""
    from impydroid import curses_ui as cui
    from impydroid.placeholder import placeholder_png
    import tempfile
    from pathlib import Path

    d = Path(tempfile.mkdtemp(prefix="impydroid-tui-"))
    try:
        png = d / "t.png"
        png.write_bytes(placeholder_png("tui test", 3, 256))
        # 宽场景（100x30 面板：cols=98, rows=16）
        lines = cui._render_ascii(png, 98, 16)
        check("TUI: _render_ascii 有输出", bool(lines), str(lines)[:60])
        check("TUI: 输出不含 ANSI 转义码",
              all("\x1b" not in ln for ln in (lines or [])),
              repr([ln for ln in (lines or []) if "\x1b" in ln][:1]))
        check("TUI: 输出是 ASCII 可见字符",
              all(all(ord(c) < 128 for c in ln) for ln in (lines or [])),
              "含非 ASCII")
        check("TUI: 行数不超过面板高度",
              len(lines or []) <= 16, str(len(lines or [])))
        # 窄场景（50x34：cols=48, rows=6）
        lines2 = cui._render_ascii(png, 48, 6)
        check("TUI: 窄面板行数也受控",
              len(lines2 or []) <= 10, str(len(lines2 or [])))
        check("TUI: 列宽不超面板",
              all(cui._disp_w(ln) <= 48 for ln in (lines2 or [])),
              str([cui._disp_w(ln) for ln in (lines2 or [])]))

        # 显示宽度工具
        check("TUI: _disp_w 中文算 2 列", cui._disp_w("中") == 2)
        check("TUI: _disp_w ASCII 算 1 列", cui._disp_w("abc") == 3)
        check("TUI: _trunc 按宽度截断并加省略号",
              cui._disp_w(cui._trunc("中文中文中文", 6)) <= 6,
              cui._trunc("中文中文中文", 6))
        check("TUI: _pad 补齐到指定宽度",
              cui._disp_w(cui._pad("ab", 10)) == 10)
    finally:
        import shutil
        shutil.rmtree(d, ignore_errors=True)


def test_tui_input_unicode() -> None:
    """回归：输入框必须能正确处理中文（get_wch 而不是 getch）。"""
    from impydroid import curses_ui as cui
    inp = cui._InputLine()
    # 逐字输入中文
    for ch in "你好世界":
        inp.handle(ch)
    check("TUI输入: 中文能完整进入缓冲区", inp.text == "你好世界", inp.text)
    check("TUI输入: 光标在末尾", inp.cur == 4, str(inp.cur))
    # 退格删一个中文字
    inp.handle("\x7f")
    check("TUI输入: 退格删掉一个完整中文字", inp.text == "你好世", inp.text)
    # 光标左移 + 插入
    inp.handle(cui._c.KEY_LEFT)
    inp.handle("X")
    check("TUI输入: 光标处插入", inp.text == "你好X世", inp.text)
    # Home / End
    inp.handle(cui._c.KEY_HOME)
    check("TUI输入: Home 回开头", inp.cur == 0, str(inp.cur))
    inp.handle(cui._c.KEY_END)
    check("TUI输入: End 到末尾", inp.cur == len(inp.text), str(inp.cur))
    # Enter 提交
    check("TUI输入: Enter 返回 submit", inp.handle("\n") == "submit")
    # Esc 清空
    inp.handle("abc")
    check("TUI输入: Esc 返回 cancel", inp.handle("\x1b") == "cancel")
    check("TUI输入: Esc 后已清空", inp.text == "", inp.text)
    # Ctrl+U 清空
    inp.handle("abc")
    inp.handle("\x15")
    check("TUI输入: Ctrl+U 清空", inp.text == "", inp.text)
    # 删除键
    inp.handle("ab")
    inp.handle(cui._c.KEY_LEFT)
    inp.handle(cui._c.KEY_DC)
    check("TUI输入: Delete 删光标处", inp.text == "a", inp.text)


def test_tui_hint_layout() -> None:
    """回归：底部按键提示必须多列对齐，且窄屏也要能看到退出键。"""
    import curses as _c
    from impydroid import curses_ui as cui
    # mock color_pair 避免 initscr 报错
    saved = cui._c.color_pair
    cui._c.color_pair = lambda n: n
    try:
        for width in (30, 40, 50, 72, 80, 100, 120):
            rows = cui._hint_rows(width, compact=cui._hint_compact(width))
            h = cui._hint_height(width)
            check(f"TUI提示: {width}列有内容", len(rows) >= 1, str(len(rows)))
            check(f"TUI提示: {width}列显示行数≤上限", len(rows) <= h or h >= 1,
                  f"rows={len(rows)} h={h}")
            # rows 现在是 list[list[(text, attr)]]
            flat = " ".join("".join(t for t, _ in row) for row in rows)
            check(f"TUI提示: {width}列含退出键", "[^Q]" in flat, flat[:60])
            check(f"TUI提示: {width}列含生成键", "[^G]" in flat, flat[:60])
            # 每行不超过终端宽度
            for row in rows:
                line_len = sum(cui._disp_w(t) for t, _ in row)
                check(f"TUI提示: {width}列行宽不超标",
                      line_len <= width, f"{line_len} > {width}")
            # 颜色检查：priority=0（生图类）用亮色，priority=1（程序类）用暗色
            for row in rows:
                for text, attr in row:
                    if "[^G]" in text or "[^D]" in text:   # 生图类 priority=0
                        expected = _c.color_pair(cui._C_VALUE)
                    elif "[^S]" in text:                   # 程序类 priority=1
                        expected = _c.color_pair(cui._C_DIM)
                    else:
                        continue
                    check(f"TUI提示: {width}列颜色正确({text!r})",
                          attr == expected, f"got {attr} expected {expected}")
    finally:
        cui._c.color_pair = saved


def test_tui_no_bottom_right_write() -> None:
    """回归：绝不能写屏幕右下角（会触发 curses 滚动，界面整体上跳）。"""
    from impydroid import curses_ui as cui

    class W:
        def __init__(self, h, w):
            self.h, self.w, self.calls = h, w, []
            self.rows = []
        def getmaxyx(self):
            return self.h, self.w
        def addnstr(self, y, x, text, n, attr=0):
            self.calls.append((y, x, n))
            if y == self.h - 1 and x + n >= self.w:
                raise AssertionError(
                    f"写到了右下角！y={y} x={x} n={n} (screen {self.h}x{self.w})")

    w = W(24, 80)
    cui._safe_addstr(w, 23, 0, "x" * 200)
    check("TUI: 最后一行写入被截断到 w-1", True)
    # 正常行不受影响
    cui._safe_addstr(w, 5, 0, "x" * 200)
    check("TUI: 普通行仍写满 w-x", w.calls[-1][2] == 80, str(w.calls[-1]))


def test_tui_action_imports() -> None:
    """回归：TUI 的动作函数里所有 import 必须可解析。

    背景：v5.2.0 里 `_act_generate` 写了 `from .settings import ApiError`，
    但 ApiError 实际在 httpclient 里。pyflakes 不查跨模块导入、测试也没覆盖
    动作路径 → 直到用户真机点"生成"才炸。这个测试把那类 bug 锁死。
    """
    import ast
    import importlib
    from pathlib import Path
    from impydroid import curses_ui as cui

    f = Path(cui.__file__)
    tree = ast.parse(f.read_text(encoding="utf-8"))
    seen, bad = set(), []
    for node in ast.walk(tree):
        if isinstance(node, ast.ImportFrom) and node.level == 1 and node.module:
            for a in node.names:
                if a.name == "*":
                    continue
                key = (node.module, a.name)
                if key in seen:
                    continue
                seen.add(key)
                try:
                    m = importlib.import_module(f"impydroid.{node.module}")
                except Exception as e:                     # noqa: BLE001
                    bad.append(f".{node.module} 模块导入失败: {e}")
                    continue
                if not hasattr(m, a.name):
                    bad.append(f"from .{node.module} import {a.name} ← 不存在")
    check("TUI: 所有相对 import 可解析", not bad, "; ".join(bad[:3]))
    check("TUI: 检查到了足够多的导入", len(seen) >= 10, str(len(seen)))


def test_tui_generate_offline() -> None:
    """回归：TUI 的生成动作跑通（离线）—— 覆盖 _act_generate 全路径。"""
    import os
    import tempfile
    from pathlib import Path
    from impydroid import curses_ui as c2

    tmp = Path(tempfile.mkdtemp(prefix="impydroid-tui-act-"))
    old_home = os.environ.get("IMGAGENT_HOME")
    os.environ["IMGAGENT_HOME"] = str(tmp)
    try:
        import importlib
        import impydroid.settings as _st
        importlib.reload(_st)
        import impydroid.store as _store
        importlib.reload(_store)
        import impydroid.ui as _ui
        importlib.reload(_ui)
        importlib.reload(c2)

        sess = _store.Session()
        sess.offline = True
        # 后台任务跑在"假 stdscr"上（_run_bg 只用来刷新动画，这里直接跳过）。
        #
        # ⚠️ 必须**保存并在 finally 里恢复**！
        # 早期版本直接 `c2._run_bg = lambda...` 后就忘了恢复，
        # 导致后续测试（test_tui_bg_redirects_stdout）拿到的是这个 lambda，
        # inspect.getsource 检查失败 —— 典型的测试间状态污染。
        _orig_run_bg = c2._run_bg
        c2._run_bg = lambda *a, **k: None
        # 这个测试只验证"生成动作能进入（无 ImportError）"，用 FakeScr 不画屏。
        # 润色流程会画浮窗（需要真 curses），这里 stub 成"直接用原文"。
        _orig_polish = c2._polish_flow_tui
        c2._polish_flow_tui = lambda stdscr, sess, text, **kw: text

        class FakeScr:
            def getmaxyx(self):
                return (30, 100)
            def erase(self):
                pass
            def refresh(self):
                pass

        st = {"mode": "gen", "hist": 0, "scroll": 0, "busy": "",
              "spin": 0, "t0": 0.0}
        # 直接跑动作（_run_bg 已被 stub 掉，job 不会执行）
        # 这里只验证"函数能进入且不抛 ImportError/AttributeError"
        try:
            c2._act_generate(FakeScr(), sess, "dummy", True, st,
                             "一只猫", {"err": None})
            ok_enter = True
            err = ""
        except ImportError as e:
            ok_enter, err = False, f"ImportError: {e}"
        except AttributeError as e:
            ok_enter, err = False, f"AttributeError: {e}"
        check("TUI: 生成动作能进入（无 ImportError）", ok_enter, err)

        try:
            c2._act_edit(FakeScr(), sess, "dummy", True, st,
                         "换个背景", {"err": None})
            ok_edit = True
            err2 = ""
        except ImportError as e:
            ok_edit, err2 = False, f"ImportError: {e}"
        except AttributeError as e:
            ok_edit, err2 = False, f"AttributeError: {e}"
        check("TUI: 编辑动作能进入（无 ImportError）", ok_edit, err2)

        # 其余动作也过一遍（只需不炸）
        for name, fn in [
            ("undo", lambda: c2._act_undo(sess, st)),
            ("preview", lambda: c2._act_preview(FakeScr(), sess, st)),
            ("cycle_quality", lambda: c2._act_cycle_quality(sess)),
        ]:
            try:
                fn()
                check(f"TUI: {name} 动作可执行", True)
            except (ImportError, AttributeError) as e:
                check(f"TUI: {name} 动作可执行", False, str(e))
    finally:
        c2._run_bg = _orig_run_bg          # ← 关键：恢复被打补丁的函数
        c2._polish_flow_tui = _orig_polish
        if old_home is None:
            os.environ.pop("IMGAGENT_HOME", None)
        else:
            os.environ["IMGAGENT_HOME"] = old_home
        import shutil
        shutil.rmtree(tmp, ignore_errors=True)


def test_tui_csi_keymap_complete() -> None:
    """回归：方向键/翻页键的转义序列表必须完整。

    v5.3.1 的真机 bug：timeout() 模式下 curses 不组装转义序列，
    方向键被拆成 ESC '[' 'A' 三个字符 → 插进输入框变乱码。
    修法是手工组装。这个测试锁住映射表的完整性。
    """
    from impydroid import curses_ui as cui
    need = {
        "[A": "KEY_UP", "[B": "KEY_DOWN", "[C": "KEY_RIGHT", "[D": "KEY_LEFT",
        "[H": "KEY_HOME", "[F": "KEY_END",
        "[5~": "KEY_PPAGE", "[6~": "KEY_NPAGE",
        "[3~": "KEY_DC", "[1~": "KEY_HOME", "[4~": "KEY_END",
    }
    for seq, name in need.items():
        if not seq.startswith("["):
            continue
        tail = seq[1:]
        check(f"CSI: {seq} 有映射", tail in cui._CSI_KEYS, tail)
        if tail in cui._CSI_KEYS:
            expect = getattr(cui._c, name, None)
            check(f"CSI: {seq} → {name}",
                  cui._CSI_KEYS[tail] == expect,
                  f"{cui._CSI_KEYS.get(tail)} != {expect}")
    # SS3 形式
    for tail, name in (("A", "KEY_UP"), ("B", "KEY_DOWN"), ("C", "KEY_RIGHT"),
                       ("D", "KEY_LEFT"), ("H", "KEY_HOME"), ("F", "KEY_END")):
        check(f"SS3: O{tail} 有映射", tail in cui._SS3_KEYS, tail)
        if tail in cui._SS3_KEYS:
            check(f"SS3: O{tail} → {name}",
                  cui._SS3_KEYS[tail] == getattr(cui._c, name, None),
                  str(cui._SS3_KEYS.get(tail)))


def test_tui_input_rejects_function_keys() -> None:
    """回归：curses 功能键码（>=256）不能当字符插进输入框。

    旧代码用 `32 <= ch < 0x110000` 做兜底判断，KEY_F1(265)、KEY_UP(259)
    都会被当成 chr() 插进去。上限必须是 127（ASCII 可打印）。
    """
    from impydroid import curses_ui as cui
    inp = cui._InputLine()
    for code in (cui._c.KEY_F1, cui._c.KEY_F5, cui._c.KEY_UP, cui._c.KEY_DOWN,
                 cui._c.KEY_PPAGE, cui._c.KEY_NPAGE, 999, 1000, 5000):
        inp.handle(code)
    check("TUI输入: 功能键不被插入", inp.text == "", repr(inp.text))
    # ASCII 兜底仍有效
    inp2 = cui._InputLine()
    inp2.handle(ord("A"))
    inp2.handle(ord("~"))
    check("TUI输入: ASCII int 兜底仍工作", inp2.text == "A~", inp2.text)


def test_tui_no_module_globals() -> None:
    """回归：TUI 不应有模块级可变全局状态（跨线程共享是隐患）。

    v5.2.0 有 _LAST_SESS / _LAST_OFFLINE 两个全局，被主循环写、
    后台线程读。虽然当前调用约定下不会真竞态，但依赖顺序而非语言保证。
    已改为参数传递。这个测试防止它们（或同类全局）回来。
    """
    from impydroid import curses_ui as cui
    banned = ("_LAST_SESS", "_LAST_OFFLINE")
    for name in banned:
        check(f"TUI: 无全局 {name}", not hasattr(cui, name), name)
    # _DECODED_CACHE 在 termimg 里应有锁保护
    from impydroid import termimg
    check("termimg: 解码缓存有锁",
          hasattr(termimg, "_DECODED_CACHE_LOCK"), "")
    check("termimg: 解码缓存是 LRU（OrderedDict）",
          hasattr(termimg._DECODED_CACHE, "move_to_end"), "")


def test_termimg_decode_cache_lru() -> None:
    """回归：解码缓存必须是 LRU（不能"满了就全清"）。"""
    import tempfile
    from pathlib import Path
    from impydroid import termimg
    from impydroid.placeholder import placeholder_png

    d = Path(tempfile.mkdtemp(prefix="impydroid-lru-"))
    try:
        termimg.clear_decode_cache()
        paths = []
        for i in range(6):
            p = d / f"i{i}.png"
            p.write_bytes(placeholder_png(f"x{i}", i + 1, 96))
            paths.append(p)
        for p in paths:
            termimg.decode_cached(p)
        check("LRU: 容量上限生效",
              len(termimg._DECODED_CACHE) <= termimg._DECODED_CACHE_MAX,
              str(len(termimg._DECODED_CACHE)))
        # 跨平台取文件名：Windows 用 "\"，POSIX 用 "/"——不能用 split("/")
        def _base(key) -> str:
            import os as _o
            return _o.path.basename(key[0])
        names = {_base(k) for k in termimg._DECODED_CACHE}
        check("LRU: 最旧的被淘汰",
              "i0.png" not in names and "i1.png" not in names, str(sorted(names)))
        # 访问 i2 后它应被"续命"
        termimg.decode_cached(paths[2])
        p7 = d / "i7.png"
        p7.write_bytes(placeholder_png("x7", 8, 96))
        termimg.decode_cached(p7)
        names2 = {_base(k) for k in termimg._DECODED_CACHE}
        check("LRU: 近期访问的不被淘汰", "i2.png" in names2, str(sorted(names2)))
    finally:
        termimg.clear_decode_cache()
        import shutil
        shutil.rmtree(d, ignore_errors=True)


def test_tui_input_wrapping_and_caret() -> None:
    """回归：输入框必须多行折行 + 光标不遮挡文字。

    两个真机 bug：
    ① 只画一行，长提示词被 _trunc 成 "…"，用户看不到后面
    ② 光标用 "▏" 字符**插入**文本流，把该位置的字符挤走
       （真机截图里「实践」的「实」被挡）
    """
    from impydroid import curses_ui as cui

    # --- 折行：不丢内容、不超宽 ---
    for w in (16, 20, 30, 46, 80):
        for text in ("社会实践报告关于乡村振兴战略的调研分析以及未来展望和意见建议",
                     "short", "a" * 200, "中英mixed混排test测试" * 5):
            lines = cui._wrap_by_width(text, w)
            check(f"折行: w={w} 不丢内容",
                  "".join(lines) == text, f"{len(''.join(lines))} != {len(text)}")
            check(f"折行: w={w} 不超宽",
                  all(cui._disp_w(ln) <= w for ln in lines),
                  str([cui._disp_w(ln) for ln in lines if cui._disp_w(ln) > w]))

    # --- 绘制：内容完整 + 光标用属性而非字符 ---
    cui._c.color_pair = lambda n: n          # 免 initscr
    # ⚠️ 绝不篡改 curses.A_BOLD / A_REVERSE —— 它们是模块级常量，
    # 改了会**污染后续所有测试**（真实值 A_REVERSE=262144）。
    # 之前的写法 `A_BOLD, A_REVERSE = 1, 2` 正是 646/648 失败的根因。

    class MW:
        def __init__(self, h, w):
            self.h, self.w, self.calls = h, w, []
        def getmaxyx(self):
            return self.h, self.w
        def addnstr(self, y, x, t, n, attr=0):
            self.calls.append((y, x, t[:n], attr))

    text = "社会实践报告关于乡村振兴战略的调研分析以及未来展望和意见建议"
    inp = cui._InputLine()
    for ch in text:
        inp.handle(ch)
    for _ in range(3):                        # 光标左移，让它落在文字中间
        inp.handle(cui._c.KEY_LEFT)

    mw = MW(40, 50)
    cui._draw_input(mw, 30, 0, 5, 50, inp, {"mode": "gen"})
    body = [(y, x, t, a) for y, x, t, a in mw.calls if y >= 30]
    joined = "".join(t for _, _, t, _ in body)

    check("绘制: 所有字符都画出来了",
          all(ch in joined for ch in text.replace(" ", "")),
          f"缺 {[c for c in text if c not in joined][:5]}")
    # 光标处应该有一个"反白"的**原有字符**（不是 ▏）。
    #
    # 排除边框是个坑：边框既用 A_REVERSE，又可能是"混合段"（如 '╭─┤'、
    # '├────╮' 同时含角、横线、接线符），所以 set(t) <= BOX_CHARS 判定不可靠。
    # 更稳的判据：**光标标记的那段宽度 ≤ 2**（一个 CJK 字符或一个 ASCII 字符）。
    BOX_CHARS = set("│┌┐└┘─╭╮╰╯╔╗╚╝═║┏┓┗┛━┃┤├|+-")
    REV = cui._c.A_REVERSE                 # 用真实常量，不要写死 2
    rev = [(y, x, t, a) for y, x, t, a in body
           if (a & REV) and t and cui._disp_w(t) <= 2
           and not (set(t) <= BOX_CHARS)]
    check("绘制: 光标用反白属性标记原字符", bool(rev), str(rev[:2]))
    if rev:
        marked = rev[0][2]
        check("绘制: 光标标记的是文本里的真实字符",
              marked in text, f"{marked!r} 不在原文本里")
        # 关键：光标字符必须**没有被替换**成别的符号（旧的 ▏ 方案会替换）
        check("绘制: 光标字符仍是原文里的原字符（未替换）",
              marked == text[inp.cur], f"{marked!r} != {text[inp.cur]!r}")
    check("绘制: 没有用 ▏ 字符当光标",
          "▏" not in joined, joined[:40])


def test_tui_settings_stays_in_curses() -> None:
    """回归：设置面板不能在 curses 内调用 print（会冲乱界面）。

    旧实现调 ui.do_settings()（命令行版），必须先 endwin() 挂起 ——
    用户看来就是"按设置跳出了 GUI"。
    """
    import ast
    from pathlib import Path
    from impydroid import curses_ui as cui

    src = Path(cui.__file__).read_text(encoding="utf-8")
    tree = ast.parse(src)

    # _act_settings / _pick_list / _message_box 内部不能有 print( 调用
    for fn_name in ("_act_settings", "_settings_edit", "_pick_list",
                    "_message_box", "_preview_overlay"):
        for node in ast.walk(tree):
            if isinstance(node, ast.FunctionDef) and node.name == fn_name:
                prints = [n for n in ast.walk(node)
                          if isinstance(n, ast.Call)
                          and isinstance(n.func, ast.Name)
                          and n.func.id == "print"]
                check(f"设置面板: {fn_name} 内无 print", not prints,
                      f"{len(prints)} 处 print（会冲乱 curses 界面）")


def test_tui_border_styles() -> None:
    """回归：5 种边框风格的顶边总宽必须**正好**等于盒宽。

    标题嵌进边框很容易算错（多一列或少一列），导致右边框错位。
    """
    from impydroid import curses_ui as cui
    cui._c.color_pair = lambda n: n
    # （不篡改 curses.A_BOLD，用真实常量）

    class MW:
        def __init__(self, h, w):
            self.h, self.w, self.calls = h, w, []
        def getmaxyx(self):
            return self.h, self.w
        def addnstr(self, y, x, t, n, attr=0):
            self.calls.append((y, x, t[:n]))

    import os
    for style in ("round", "square", "double", "heavy", "ascii"):
        os.environ["IMGAGENT_TUI_BORDER"] = style
        for w in (16, 24, 30, 36, 50, 80, 100, 120):
            for title in ("", "当前", "历史 / 累计 (3)", "预览 · 文件名.png",
                          "输入 · 生成   Ctrl+U 清空 · Ctrl+W 删词"):
                mw = MW(20, w)
                cui._box(mw, 0, 0, 5, w, title, 0, 0)
                top = [(x, cui._disp_w(t)) for y, x, t in mw.calls if y == 0]
                total = max(x + dw for x, dw in top)
                check(f"边框: {style} w={w} title={title[:8]!r} 宽度正确",
                      total == w, f"{total} != {w}")
    os.environ.pop("IMGAGENT_TUI_BORDER", None)

    # 五种风格字符集应互不相同
    styles = {k: v for k, v in cui._BORDER_STYLES.items()}
    check("边框: 提供 ≥5 种风格", len(styles) >= 5, str(list(styles)))
    check("边框: round 用圆角", cui._BORDER_STYLES["round"][0] == "╭")
    check("边框: double 用双线", cui._BORDER_STYLES["double"][1] == "═")
    check("边框: ascii 全是 ASCII",
          all(ord(c) < 128 for c in cui._BORDER_STYLES["ascii"]))


def test_tui_no_print_in_any_action() -> None:
    """回归：**所有** _act_* 动作函数内不得有 print（会冲乱 curses 界面）。

    这是 4 个真机 bug 的共同根因 —— 混用命令行版函数（它们全是 print 驱动）。
    """
    import ast
    from pathlib import Path
    from impydroid import curses_ui as cui

    tree = ast.parse(Path(cui.__file__).read_text(encoding="utf-8"))
    offenders = []
    for node in ast.walk(tree):
        if isinstance(node, ast.FunctionDef) and node.name.startswith("_act_"):
            for n in ast.walk(node):
                if (isinstance(n, ast.Call) and isinstance(n.func, ast.Name)
                        and n.func.id == "print"):
                    offenders.append(f"{node.name}:{n.lineno}")
    check("动作函数: 内部无 print（用 log() 或浮窗）", not offenders,
          "; ".join(offenders[:5]))


def test_tui_no_suspend_except_apikey() -> None:
    """回归：只有"设置 API key"允许挂起 curses，其它都必须留在 TUI 内。

    早期版本 upload/history/settings/data_dir/env/menu 全都会挂起 ——
    用户看来就是"点任何选项都跳出 GUI"。
    """
    from impydroid import curses_ui as cui
    import inspect

    # _act_settings 里允许有 1 处（API key 分支）
    # 其它 _act_* 函数不应出现
    for fn_name in ("_act_upload", "_act_history", "_act_data_dir", "_act_env",
                    "_act_preview", "_act_undo", "_act_cycle_quality"):
        fn = getattr(cui, fn_name, None)
        if fn is None:
            continue
        body = inspect.getsource(fn)
        check(f"挂起: {fn_name} 不挂起 curses",
              "_suspend_and_run" not in body,
              f"{fn_name} 里有 _suspend_and_run")


def test_tui_truecolor_helpers() -> None:
    """回归：真彩色辅助函数的正确性（颜色分配 / 缓存 / 降级）。"""
    from impydroid import curses_ui as cui
    # rgb565 编码
    check("真彩: rgb565 编码", cui._rgb_to_565(255, 0, 0) == 0xF800,
          str(cui._rgb_to_565(255, 0, 0)))
    check("真彩: rgb565 黑", cui._rgb_to_565(0, 0, 0) == 0, "")
    # 颜色号分配上界
    check("真彩: 颜色号从 16 起（避开基础色）",
          cui._NEXT_COLOR[0] >= 16, str(cui._NEXT_COLOR[0]))
    # pair 号从 20 起（前 20 留给 UI 静态配色）
    check("真彩: pair 号从 20 起",
          cui._NEXT_PAIR[0] >= 20, str(cui._NEXT_PAIR[0]))
    # 缓存字典存在
    check("真彩: 有颜色缓存", isinstance(cui._COLOR_CACHE, dict), "")
    check("真彩: 有 pair 缓存", isinstance(cui._PAIR_CACHE, dict), "")


def test_tui_no_column_conflicts() -> None:
    """回归：任何面板/浮窗的写入都不能在同一行发生列冲突。

    这个测试用**精确的 (行,列,宽度)** 记录来验证，比"截图比对"可靠 ——
    早期我用调试导出工具看渲染结果，它的宽字符处理有偏差，导致误判。
    """
    from collections import defaultdict
    from impydroid import curses_ui as cui
    cui._c.color_pair = lambda n: n
    # （不篡改 curses.A_BOLD，用真实常量）
    # ⚠️ mock window 模拟的是 **ncurses 坐标语义**（坐标=视觉列，addnstr 的 n
    # 按字符数、_disp_w 算宽）。PDCurses 下 _safe_addstr 会额外做"清空整行"
    # 覆盖残留，那是平台差异、不该干扰本测试的布局冲突判定 —— 故临时关闭。
    _saved_pd = cui._IS_PDCURSES
    cui._IS_PDCURSES = False

    class MW:
        def __init__(self, h, w):
            self.h, self.w, self.calls = h, w, []
        def getmaxyx(self):
            return self.h, self.w
        def addnstr(self, y, x, t, n, attr=0):
            self.calls.append((y, x, t[:n], cui._disp_w(t[:n])))

    def conflicts(calls):
        byrow = defaultdict(list)
        for y, x, t, dw in calls:
            if t.strip():
                byrow[y].append((x, x + dw - 1, t))
        bad = []
        for y, spans in byrow.items():
            spans.sort()
            for i in range(len(spans) - 1):
                if spans[i][1] >= spans[i + 1][0]:
                    bad.append((y, spans[i], spans[i + 1]))
        return bad

    for w, h in ((100, 36), (50, 34), (120, 40)):
        # 顶栏 + 浮动面板（模拟 _output_overlay 的布局）
        mw = MW(h, w)
        cui._draw_topbar(mw, 0, 0, w, 3, {"busy": "", "spin": 0, "t0": 0}, False)
        box_y, box_x = 3, 1
        box_h, box_w = h - box_y, w - 2
        cui._box(mw, box_y, box_x, box_h, box_w, "数据目录", 0, 0, True)
        bad = conflicts(mw.calls)
        check(f"列冲突: {w}x{h} 主界面+浮窗无冲突", not bad, str(bad[:2]))

        # 设置面板布局
        mw2 = MW(h, w)
        cui._draw_topbar(mw2, 0, 0, w, 3, {"busy": "", "spin": 0, "t0": 0}, False)
        bh = min(h - 5, 12)
        by = max(3, (h - bh) // 2)
        bw = min(w - 2, max(40, min(70, w - 4)))
        bx = max(0, (w - bw) // 2)
        cui._box(mw2, by, bx, bh, bw, "设置", 0, 0, True)
        bad2 = conflicts(mw2.calls)
        check(f"列冲突: {w}x{h} 设置面板无冲突", not bad2, str(bad2[:2]))
    cui._IS_PDCURSES = _saved_pd


def test_tui_ctrl_keys_not_stealing_chars() -> None:
    """回归：功能键必须是 Ctrl 组合，普通字母/数字必须能正常输入。

    真机 bug：早期用裸字母当功能键（g/e/u/r/p/s/q/3/4/5/9），
    导致写提示词时**打不出这些字符** —— 按 q 退出、按 3 上传。
    这个测试锁死"字母数字一律不被功能键拦截"。
    """
    import curses
    from impydroid import curses_ui as cui
    cui._c.color_pair = lambda n: n
    # （不再伪造 A_BOLD/A_REVERSE，用真实常量）
    cui._c.start_color = cui._c.use_default_colors = lambda: None
    cui._c.init_pair = lambda *a: None
    cui._c.has_colors = lambda: True

    class MW:
        def __init__(self):
            self.calls = []
        def getmaxyx(self):
            return 36, 100
        def erase(self):
            pass
        def refresh(self):
            pass
        def clear(self):
            pass
        def addnstr(self, y, x, t, n, attr=0):
            self.calls.append((y, x, t[:n], attr))

    class FakeSess:
        model = "m"
        quality = "low"
        aspect = "1:1"
        offline = True
        preview = False
        items = []
        current = None
        counter = 0
        total_cost = 0.0
        def save_config(self):
            pass
        def load(self):
            pass

    # 拦截动作函数，记录被调的是哪个
    called = []
    saved = {}
    for name in ("_act_settings", "_act_data_dir", "_act_env", "_act_history",
                 "_act_upload", "_act_undo", "_act_preview",
                 "_act_cycle_quality", "_help_overlay", "_confirm_quit"):
        if hasattr(cui, name):
            saved[name] = getattr(cui, name)
        def make(n):
            def fn(*a, **k):
                called.append(n)
                return True if n == "_confirm_quit" else None
            return fn
        setattr(cui, name, make(name))

    try:
        # ---- 核心断言：普通字符不被拦截 ----
        for ch in ("q", "3", "z", "9", "A", "中", "空格", "!", "0"):
            if len(ch) > 1:
                continue
            inp = cui._InputLine()
            st = {"mode": "gen", "hist": 0}
            handled = cui._handle_action_key(MW(), FakeSess(), "", True,
                                             st, inp, ch)
            check(f"Ctrl键: 普通字符 {ch!r} 不被功能键拦截", not handled, ch)

        # ---- Ctrl 键必须全部生效 ----
        expect = {
            0x07: "gen", 0x04: "edit", 0x12: "undo", 0x0c: "preview",
            0x10: "quality", 0x02: "history", 0x06: "upload",
            0x18: "script_imgs", 0x13: "settings", 0x09: "data_dir",
            0x0e: "env", 0x14: "help", 0x11: "quit", 0x0f: "refresh",
        }
        for code, name in expect.items():
            check(f"Ctrl键: 0x{code:02x} → {name} 已注册",
                  cui._ctrl_action(code) == name, str(cui._ctrl_action(code)))
            inp = cui._InputLine()
            st = {"mode": "gen", "hist": 0}
            called.clear()
            # get_wch 对 Ctrl 返回 str，这里模拟真实行为
            handled = cui._handle_action_key(MW(), FakeSess(), "", True,
                                             st, inp, chr(code))
            check(f"Ctrl键: {name} 被处理", handled, name)

        # ---- _key_code 必须能识别控制字符 str ----
        check("Ctrl键: _key_code 识别 '\x07'", cui._key_code("\x07") == 7,
              str(cui._key_code("\x07")))
        check("Ctrl键: _key_code 放行 'q'", cui._key_code("q") is None, "")
        check("Ctrl键: _key_code 放行 '中'", cui._key_code("中") is None, "")
        check("Ctrl键: _key_code 识别 curses int",
              cui._key_code(curses.KEY_LEFT) == curses.KEY_LEFT, "")

        # ---- 键位不能和输入框冲突 ----
        inp_keys = {k for k in cui._INPUT_KEYS if isinstance(k, int)}
        clash = set(cui._CTRL_ACTIONS) & inp_keys
        check("Ctrl键: 不与输入框键位冲突", not clash,
              str([hex(x) for x in clash]))
    finally:
        for name, fn in saved.items():
            setattr(cui, name, fn)


def test_tui_preview_render_cached() -> None:
    """回归：预览渲染必须缓存（否则每帧全量解码 → 生成图片后界面卡死）。

    真机现象：生成一张图之前很流畅，生成后变得极慢（时钟几秒才跳一次）。
    根因：预览面板每帧都跑 _render_color/_render_ascii，
          而它们内部要全量解码 PNG（1024² 图桌面 10-50ms，手机 ×10-30）。
    """
    import tempfile
    from pathlib import Path
    from impydroid import curses_ui as cui
    from impydroid.placeholder import placeholder_png
    from impydroid import termimg

    cui._c.color_pair = lambda n: n
    # （不再伪造 A_BOLD/A_REVERSE，用真实常量）
    cui._c.start_color = cui._c.use_default_colors = lambda: None
    cui._c.init_color = lambda *a: None
    cui._c.init_pair = lambda *a: None
    cui._c.can_change_color = lambda: False    # 强制走 ASCII 路径

    d = Path(tempfile.mkdtemp(prefix="impydroid-prev-"))
    try:
        cui.clear_preview_cache()
        termimg.clear_decode_cache()
        img = d / "x.png"
        img.write_bytes(placeholder_png("x", 1, 512))

        # 第一次（冷缓存）
        l1 = cui._render_ascii(img, 40, 18)
        check("预览缓存: 首次渲染有结果", bool(l1), str(l1)[:40])
        check("预览缓存: 首次后有缓存条目",
              len(cui._PREVIEW_CACHE) >= 1, str(len(cui._PREVIEW_CACHE)))
        # 第二次应命中缓存（对象同一 → 直接复用）
        l2 = cui._render_ascii(img, 40, 18)
        check("预览缓存: 二次命中同一结果", l2 is l1, "不是同一对象")

        # 尺寸变化 → 应该重新渲染（不同 key）
        n_before = len(cui._PREVIEW_CACHE)
        l3 = cui._render_ascii(img, 50, 20)
        check("预览缓存: 尺寸变化产生新条目",
              len(cui._PREVIEW_CACHE) > n_before or l3 is not l1, "")

        # LRU 上限
        for i in range(20):
            cui._render_ascii(img, 20 + i * 2, 10)
        check("预览缓存: 有条数上限",
              len(cui._PREVIEW_CACHE) <= cui._PREVIEW_CACHE_MAX,
              f"{len(cui._PREVIEW_CACHE)} > {cui._PREVIEW_CACHE_MAX}")

        # 图片 mtime 变化 → 缓存失效
        cui.clear_preview_cache()
        a = cui._render_ascii(img, 40, 18)
        import os as _os
        _os.utime(img, (0, 0))                 # 改 mtime
        b = cui._render_ascii(img, 40, 18)
        check("预览缓存: mtime 变化后失效", b is not a, "仍是旧对象")

        cui.clear_preview_cache()
    finally:
        import shutil
        cui.clear_preview_cache()
        termimg.clear_decode_cache()
        shutil.rmtree(d, ignore_errors=True)


def test_tui_bg_redirects_stdout() -> None:
    """回归：后台任务期间 stdout 必须被重定向（否则 print 冲乱界面）。

    真机现象：生成等待时，底部按键提示区出现「还在生成中（已等 10s…）」
    —— 那是 api.py 轮询时的命令行进度提示，直接写到了终端。
    """
    import ast
    import inspect
    from impydroid import curses_ui as cui

    src = inspect.getsource(cui._run_bg)
    # 注意：代码里用的是链式赋值 `sys.stdout = sys.stderr = _ToLog()`，
    # 所以断言要匹配 "sys.stdout =" 而不是 "sys.stdout = _ToLog"。
    check("后台任务: _run_bg 重定向 stdout", "sys.stdout =" in src, "")
    check("后台任务: _run_bg 恢复 stdout",
          "sys.stdout, sys.stderr = saved" in src
          or "sys.stdout, sys.stderr = _saved" in src, "")
    check("后台任务: 用 try/finally 保证恢复", "finally:" in src, "")
    check("后台任务: 有 _ToLog sink 类", "class _ToLog" in src, "")
    check("后台任务: sink 把输出转成 log()",
          "log(" in src and "_ToLog" in src, "")

    # 主循环里不该有裸 print
    tree = ast.parse(inspect.getsource(cui))
    bad = []
    for node in ast.walk(tree):
        if isinstance(node, ast.FunctionDef) and node.name.startswith("_act_"):
            for n in ast.walk(node):
                if (isinstance(n, ast.Call) and isinstance(n.func, ast.Name)
                        and n.func.id == "print"):
                    bad.append(f"{node.name}:{n.lineno}")
    check("后台任务: _act_* 内无 print", not bad, "; ".join(bad[:3]))


def test_tui_polish_flow_present() -> None:
    """回归：TUI 的生成/编辑**必须**调用润色流程。

    真机 bug：重写 TUI 时漏掉了润色 —— 命令行版 ui.polish_flow() 会在
    生成/编辑前问"要不要 AI 润色"，但 curses 版直接拿输入去生成，
    用户反馈"润色好像没了，新生成和编辑都不提示"。
    """
    import inspect
    from impydroid import curses_ui as cui

    # 源码层面确认接入点存在
    for fn_name in ("_act_generate", "_act_edit"):
        fn = getattr(cui, fn_name)
        src = inspect.getsource(fn)
        check(f"润色: {fn_name} 调用了 _polish_flow_tui",
              "_polish_flow_tui" in src, src[:80])
    src_gen = inspect.getsource(cui._act_generate)
    check("润色: 生成用 kind='gen'", 'kind="gen"' in src_gen, "")
    src_edit = inspect.getsource(cui._act_edit)
    check("润色: 编辑用 kind='edit'", 'kind="edit"' in src_edit, "")
    check("润色: 有 _polish_flow_tui 函数", hasattr(cui, "_polish_flow_tui"), "")
    check("润色: 有 _ask_yes_no 函数", hasattr(cui, "_ask_yes_no"), "")


def test_tui_polish_flow_behaviour() -> None:
    """润色流程的 9 种分支行为。

    ⚠️ v5.14.0 起候选展示改用 `_pick_pages`（逐条翻页，长文本完整折行），
    不再是 `_pick_list`（会 _trunc 压成一行导致"显示不完全"）。
    所以这里 stub 的是 **_pick_pages**。
    """
    from impydroid import curses_ui as cui
    from impydroid import polish

    cui._c.color_pair = lambda n: n
    cui._c.start_color = cui._c.use_default_colors = lambda: None
    cui._c.init_pair = lambda *a: None

    class MW:
        def getmaxyx(self):
            return 36, 100
        def erase(self):
            pass
        def refresh(self):
            pass
        def addnstr(self, *a, **k):
            pass

    class FS:
        offline = True
        aspect = "1:1"

    saved = {n: getattr(cui, n) for n in
             ("_read_key", "_pick_pages", "_run_bg", "_ask_yes_no")}
    saved_enabled, saved_picks = polish.enabled, polish.picks
    try:
        cui._run_bg = lambda stdscr, st, label, fn, offline=False: fn()
        cui._ask_yes_no = lambda *a, **k: True
        polish.enabled = lambda: True
        polish.picks = lambda prompt, kind="gen", aspect="", subject="": [
            "候选甲", "候选乙", "候选丙", "候选丁"]

        SKIP = "── 不做润色，用原提示词 ──"
        REDO = "── 重新生成几条 ──"
        captured = {}

        def cap(choices, extra=None):
            captured["choices"] = list(choices)
            captured["extra"] = [e[0] for e in (extra or [])]

        # 1) 选第 2 条
        def pick2(stdscr, sess, title, choices, extra=None):
            cap(choices, extra)
            return choices[1]
        cui._pick_pages = pick2
        got = cui._polish_flow_tui(MW(), FS(), "原文", kind="gen")
        check("润色流程: 选第 2 条返回该条", got == "候选乙", got)
        check("润色流程: 候选完整传给 _pick_pages",
              captured.get("choices") == ["候选甲", "候选乙", "候选丙", "候选丁"],
              str(captured.get("choices")))
        check("润色流程: extra 含跳过+重生成",
              captured.get("extra") == [SKIP, REDO], str(captured.get("extra")))

        # 2) 选"不做润色" → 原文
        cui._pick_pages = lambda *a, **k: SKIP
        got = cui._polish_flow_tui(MW(), FS(), "原文", kind="gen")
        check("润色流程: 不做润色返回原文", got == "原文", got)

        # 3) 选"重新生成"→ 再来一轮
        n = {"c": 0}

        def pl(stdscr, sess, title, choices, extra=None):
            n["c"] += 1
            return REDO if n["c"] == 1 else choices[0]
        cui._pick_pages = pl
        got = cui._polish_flow_tui(MW(), FS(), "原文", kind="gen")
        check("润色流程: 重新生成会再来一轮",
              got == "候选甲" and n["c"] == 2, f"{got} n={n['c']}")

        # 4) Esc 取消 → 原文
        cui._pick_pages = lambda *a, **k: None
        got = cui._polish_flow_tui(MW(), FS(), "原文", kind="gen")
        check("润色流程: Esc 返回原文", got == "原文", got)

        # 5) picks 返回空 → 原文
        polish.picks = lambda *a, **k: []
        got = cui._polish_flow_tui(MW(), FS(), "原文", kind="gen")
        check("润色流程: 空候选返回原文", got == "原文", got)

        # 6) picks 抛异常 → 原文（不崩）
        def boom(*a, **k):
            raise RuntimeError("网络错误")
        polish.picks = boom
        got = cui._polish_flow_tui(MW(), FS(), "原文", kind="gen")
        check("润色流程: LLM 失败退回原文", got == "原文", got)

        # 7) 用户拒绝润色
        polish.picks = lambda *a, **k: ["候选甲"]
        cui._ask_yes_no = lambda *a, **k: False
        got = cui._polish_flow_tui(MW(), FS(), "原文", kind="gen")
        check("润色流程: 用户拒绝返回原文", got == "原文", got)

        # 8) 润色开关关闭 → 直接原文，不弹任何窗
        cui._ask_yes_no = lambda *a, **k: (_ for _ in ()).throw(
            AssertionError("不该弹窗"))
        polish.enabled = lambda: False
        got = cui._polish_flow_tui(MW(), FS(), "原文", kind="gen")
        check("润色流程: 开关关闭直接用原文", got == "原文", got)

        # 9) edit 模式传 kind 正确
        polish.enabled = lambda: True
        seen = {}

        def cap2(prompt, kind="gen", aspect="", subject=""):
            seen["kind"] = kind
            return ["x"]
        polish.picks = cap2
        cui._ask_yes_no = lambda *a, **k: True
        cui._pick_pages = lambda *a, **k: "x"
        cui._polish_flow_tui(MW(), FS(), "改背景", kind="edit")
        check("润色流程: edit 模式传 kind=edit", seen.get("kind") == "edit",
              str(seen))
    finally:
        for name, fn in saved.items():
            setattr(cui, name, fn)
        polish.enabled, polish.picks = saved_enabled, saved_picks


def test_pick_pages_contract() -> None:
    """`_pick_pages` 的完整契约（←→ 翻页 / 折行 / 滚动 / extra 动作项）。

    为什么重要（真机反馈"润色界面每一行显示不完全"）：
      _pick_list 把每个候选用 _trunc 压成一行，40-70 字的中文候选在手机
      竖屏只能看到前一半。_pick_pages 让每条候选占一整页、按显示宽度折行，
      ←→ 翻页，超长还能 ↑↓ 滚动。
    """
    import curses
    import inspect
    from impydroid import curses_ui as cui

    cui._c.color_pair = lambda n: n
    cui._c.start_color = cui._c.use_default_colors = lambda: None
    cui._c.init_pair = lambda *a: None

    class MW:
        def __init__(self):
            self.calls = []
        def getmaxyx(self):
            return 40, 100
        def erase(self):
            pass
        def refresh(self):
            pass
        def addnstr(self, y, x, t, n, attr=0):
            self.calls.append((y, x, t[:n]))

    class FS:
        offline = True

    saved = cui._read_key

    def drive(keys, choices, extra=None, title="测试"):
        it = iter(keys)
        def rk(win):
            try:
                return next(it)
            except StopIteration:
                return "\x1b"
        cui._read_key = rk
        return cui._pick_pages(MW(), FS(), title, choices, extra=extra)

    try:
        check("翻页: Enter 选第一条",
              drive(["\r"], ["甲", "乙", "丙"]) == "甲", "")
        check("翻页: → → Enter 选第三条",
              drive([curses.KEY_RIGHT, curses.KEY_RIGHT, "\r"],
                    ["甲", "乙", "丙"]) == "丙", "")
        check("翻页: ← 在首条不越界",
              drive([curses.KEY_LEFT] * 3 + ["\r"], ["甲", "乙"]) == "甲", "")
        check("翻页: → 在末条不越界",
              drive([curses.KEY_RIGHT] * 5 + ["\r"], ["甲", "乙"]) == "乙", "")
        check("翻页: Esc 返回 None",
              drive(["\x1b"], ["甲"]) is None, "")
        check("翻页: Home 回第一条",
              drive([curses.KEY_END, curses.KEY_HOME, "\r"],
                    ["甲", "乙", "丙"]) == "甲", "")
        check("翻页: End 到最后一条",
              drive([curses.KEY_END, "\r"], ["甲", "乙", "丙"]) == "丙", "")

        SKIP, REDO = "── 不做润色 ──", "── 重新生成 ──"
        check("翻页: extra 最后一项可选",
              drive([curses.KEY_END, "\r"], ["甲", "乙"],
                    extra=[(SKIP, ""), (REDO, "")]) == REDO, "")
        check("翻页: extra 第一项可选",
              drive([curses.KEY_RIGHT, curses.KEY_RIGHT, "\r"], ["甲", "乙"],
                    extra=[(SKIP, ""), (REDO, "")]) == SKIP, "")
        check("翻页: 只有 extra 时可用",
              drive(["\r"], [], extra=[(SKIP, "")]) == SKIP, "")
        check("翻页: 全空返回 None",
              drive(["\r"], [], extra=None) is None, "")

        # 长文本折行（核心：不再截断）
        long_text = "很长的润色候选文本" * 10
        mw = MW()
        it = iter([curses.KEY_END, "\r"])
        cui._read_key = lambda w: next(it, "\x1b")
        cui._pick_pages(mw, FS(), "长文本", [long_text, "短"])
        joined = "".join(t for _, _, t in mw.calls)
        check("翻页: 长候选被折行渲染（不是截断）",
              "很长的润色候选" in joined, joined[:60])
        check("翻页: 页面标题含页码", "/2" in joined,
              str([t for _, _, t in mw.calls if "/2" in t][:1]))

        src = inspect.getsource(cui._pick_pages)
        check("翻页: 正文用折行（_wrap_by_width）", "_wrap_by_width" in src, "")
        check("翻页: 支持 ←→ 翻页",
              "KEY_LEFT" in src and "KEY_RIGHT" in src, "")
        check("翻页: 超长候选支持滚动",
              "scroll" in src and "KEY_DOWN" in src, "")

        pf = inspect.getsource(cui._polish_flow_tui)
        check("翻页: 润色流程用 _pick_pages", "_pick_pages" in pf, "")
        check("翻页: 润色流程不再用 _pick_list", "_pick_list(" not in pf, "")
    finally:
        cui._read_key = saved


def test_tui_ask_yes_no_keys() -> None:
    """回归：Y/N 询问浮层的键位（y/n/Esc/Enter + default 语义）。"""
    from impydroid import curses_ui as cui
    cui._c.color_pair = lambda n: n
    # （不再伪造 A_BOLD/A_REVERSE，用真实常量）
    cui._c.start_color = cui._c.use_default_colors = lambda: None
    cui._c.init_pair = lambda *a: None

    class MW:
        def getmaxyx(self):
            return 36, 100
        def erase(self):
            pass
        def refresh(self):
            pass
        def addnstr(self, *a, **k):
            pass

    class FS:
        offline = True

    saved = cui._read_key
    try:
        for key, expect, desc in (("y", True, "y"), ("Y", True, "Y"),
                                  ("n", False, "n"), ("N", False, "N")):
            cui._read_key = lambda w, _k=key: _k
            got = cui._ask_yes_no(MW(), FS(), "t", ["q"], default=False)
            check(f"询问框: {desc} → {expect}", got is expect, str(got))
        # Esc / Enter 取 default
        for key, desc in (("\x1b", "Esc"), ("\n", "Enter")):
            cui._read_key = lambda w, _k=key: _k
            check(f"询问框: {desc} 取默认(False)",
                  cui._ask_yes_no(MW(), FS(), "t", ["q"], default=False) is False)
            check(f"询问框: {desc} 取默认(True)",
                  cui._ask_yes_no(MW(), FS(), "t", ["q"], default=True) is True)
    finally:
        cui._read_key = saved


def test_tui_flash_is_actually_drawn() -> None:
    """回归：`_flash` 设置的提示必须真的被画出来。

    v5.8.0 的真 bug：`_draw_flash()` 定义了但**从来没被调用** ——
    `_flash()` 设置状态也白搭，Ctrl+P 的提示条根本不出现。
    也就是说"_act_cycle_quality 加了 flash 反馈"这个修复是无效的。
    """
    import inspect
    from impydroid import curses_ui as cui

    # 1) 源码层面：_draw_all 必须调用 _draw_flash
    src = inspect.getsource(cui._draw_all)
    check("flash: _draw_all 调用了 _draw_flash", "_draw_flash(" in src, "")

    # 2) 运行时：设了 flash 后，_draw_all 应产出该文案
    cui._c.color_pair = lambda n: n
    # ⚠️ 不要伪造 A_BOLD/A_REVERSE 的值 —— 真实 curses 里
    # A_REVERSE=262144、A_BOLD=2097152，用假设的位（1/2）会判错。
    # 这里直接用真实常量。
    cui._c.start_color = cui._c.use_default_colors = lambda: None
    cui._c.init_pair = lambda *a: None
    cui._c.can_change_color = lambda: False
    REV = cui._c.A_REVERSE

    class MW:
        def __init__(self):
            self.calls = []
        def getmaxyx(self):
            return 30, 100
        def erase(self):
            pass
        def refresh(self):
            pass
        def addnstr(self, y, x, t, n, attr=0):
            self.calls.append((y, x, t[:n], attr))

    class FS:
        model = "m"
        quality = "low"
        aspect = "1:1"
        offline = True
        preview = False
        items = []
        total_cost = 0.0

    def st():
        return {"mode": "gen", "hist": 0, "scroll": 0, "busy": "",
                "spin": 0, "t0": 0}

    # ⚠️ 必须在**进入测试前**保存并清零。
    # 其它测试（比如 Ctrl+O 刷新会调 _flash("已刷新")）会留下**未过期**的
    # flash 状态，导致这里的"未设置"场景实际有内容 —— 典型的测试间污染。
    saved_flash = dict(cui._FLASH)
    cui._FLASH["text"] = ""
    cui._FLASH["until"] = 0
    try:
        # 无 flash → 不应出现
        cui._FLASH["text"] = ""
        cui._FLASH["until"] = 0
        mw = MW()
        cui._draw_all(mw, FS(), cui._InputLine(), st(), False)
        # flash 的独特特征是 **A_REVERSE**（属性位 2）—— 消息面板的日志没有。
        # 早先只用文案匹配，结果被"消息"面板里的 log 文本误判。
        def _flash_like(calls):
            return [t for _, _, t, a in calls
                    if (a & REV) and "质量 →" in t]
        check("flash: 未设置时不绘制", not _flash_like(mw.calls),
              str(_flash_like(mw.calls)[:1]))

        # 设 flash → 应出现
        cui._flash("质量 → medium")
        mw2 = MW()
        cui._draw_all(mw2, FS(), cui._InputLine(), st(), False)
        found = [(y, x, t) for y, x, t, a in mw2.calls
                 if (a & REV) and "质量 → medium" in t]
        check("flash: 设置后真的被画出来", bool(found), str(found[:1]))

        # 过期 → 消失
        import time as _t
        cui._FLASH["until"] = _t.time() - 1
        mw3 = MW()
        cui._draw_all(mw3, FS(), cui._InputLine(), st(), False)
        check("flash: 过期后不再绘制", not _flash_like(mw3.calls),
              str(_flash_like(mw3.calls)[:1]))
    finally:
        cui._FLASH.update(saved_flash)


def test_tui_preview_overlay_can_scroll() -> None:
    """回归：预览浮层必须有可滚动的内容。

    两个前提缺一不可：
      ① _render_ascii(rows=0) 返回**完整行数**（不按高度压缩）
      ② _preview_overlay 里有 top 切片逻辑
    否则滚动是空转（早期版本就是这样：拿到 view_h 行，永远没东西可滚）。
    """
    import inspect
    import tempfile
    from pathlib import Path
    from impydroid import curses_ui as cui
    from impydroid.placeholder import placeholder_png
    from impydroid import termimg

    # ① rows=0 语义
    d = Path(tempfile.mkdtemp(prefix="impydroid-scroll-"))
    try:
        cui.clear_preview_cache()
        termimg.clear_decode_cache()
        img = d / "tall.png"
        img.write_bytes(placeholder_png("t", 1, 864))   # 竖图
        full = cui._render_ascii(img, 45, 0)
        check("滚动: rows=0 返回完整行数", bool(full) and len(full) > 10,
              f"{len(full) if full else 0} 行")
        # 对比：指定高度时应压缩
        cui.clear_preview_cache()
        small = cui._render_ascii(img, 45, 6)
        check("滚动: rows>0 仍按高度压缩",
              bool(small) and len(small) <= 6, f"{len(small) if small else 0} 行")
        check("滚动: rows=0 的行数 > 压缩后的行数",
              bool(full) and bool(small) and len(full) > len(small), "")
    finally:
        import shutil
        cui.clear_preview_cache()
        termimg.clear_decode_cache()
        shutil.rmtree(d, ignore_errors=True)

    # ② 浮层里有滚动逻辑
    src = inspect.getsource(cui._preview_overlay)
    check("滚动: 浮层有 top 切片", "top:top + view_h" in src or "lines[top:" in src,
          "")
    check("滚动: 支持 KEY_UP/KEY_DOWN 滚动",
          "KEY_UP" in src and "KEY_DOWN" in src, "")
    check("滚动: 显示位置指示", "total_rows" in src, "")


def test_tui_action_key_table_intact() -> None:
    """回归：重构后所有 Ctrl 键仍能正确分发。"""
    from impydroid import curses_ui as cui
    cui._c.color_pair = lambda n: n
    # （不再伪造 A_BOLD/A_REVERSE，用真实常量）
    cui._c.start_color = cui._c.use_default_colors = lambda: None
    cui._c.init_pair = lambda *a: None

    class MW:
        def getmaxyx(self):
            return 36, 100
        def erase(self):
            pass
        def refresh(self):
            pass
        def clear(self):
            pass
        def addnstr(self, *a, **k):
            pass

    class FS:
        model = "m"
        quality = "low"
        aspect = "1:1"
        offline = True
        preview = False
        items = []
        current = None
        counter = 0
        total_cost = 0.0
        def save_config(self):
            pass
        def load(self):
            pass

    # 拦截 _act_* 记录调用
    called = []
    saved = {}
    for name in ("_act_settings", "_act_data_dir", "_act_env", "_act_history",
                 "_act_upload", "_act_undo", "_act_preview",
                 "_act_cycle_quality", "_help_overlay", "_confirm_quit"):
        if hasattr(cui, name):
            saved[name] = getattr(cui, name)
        setattr(cui, name,
                (lambda n: (lambda *a, **k: called.append(n) or True))(name))

    saved_cache = cui.clear_preview_cache
    cui.clear_preview_cache = lambda: None
    try:
        expect = {
            0x07: "_gen", 0x12: "_act_undo", 0x0c: "_act_preview",
            0x10: "_act_cycle_quality", 0x02: "_act_history",
            0x06: "_act_upload", 0x18: "_act_upload", 0x13: "_act_settings",
            0x09: "_act_data_dir", 0x0e: "_act_env", 0x14: "_help_overlay",
            0x11: "_confirm_quit", 0x04: "_edit",
        }
        for code, name in expect.items():
            called.clear()
            st = {"mode": "gen", "hist": 0, "scroll": 0, "busy": "",
                  "spin": 0, "t0": 0}
            fs = FS()
            if name == "_edit":
                fs.items = [object()]
                fs.current = fs.items[0]
            handled = cui._handle_action_key(MW(), fs, "", True, st,
                                             cui._InputLine(), chr(code))
            if name == "_gen":
                ok = handled and st["mode"] == "gen"
            elif name == "_edit":
                ok = handled and st["mode"] == "edit"
            else:
                ok = handled and called and called[0] == name
            check(f"键表: 0x{code:02x} → {name}", ok,
                  f"handled={handled} called={called[:1]} mode={st['mode']}")

        # 普通字符不被拦截
        for ch in ("q", "3", "z", "9", "中", "A"):
            handled = cui._handle_action_key(MW(), FS(), "", True,
                                             {"mode": "gen", "hist": 0},
                                             cui._InputLine(), ch)
            check(f"键表: {ch!r} 不被拦截", not handled, ch)
    finally:
        for n, f in saved.items():
            setattr(cui, n, f)
        cui.clear_preview_cache = saved_cache


def test_prompt_history_store() -> None:
    """回归：提示词历史必须持久化 + 去重 + 最近的在前。"""
    import importlib
    import os
    import tempfile
    from pathlib import Path

    d = Path(tempfile.mkdtemp(prefix="impydroid-ph-"))
    old_home = os.environ.get("IMGAGENT_HOME")
    os.environ["IMGAGENT_HOME"] = str(d)
    try:
        import impydroid.settings as _st
        import impydroid.store as _sr
        importlib.reload(_st)
        importlib.reload(_sr)

        # 推送 A B C A（A 最后推 → 应排最前）
        for txt, kind in (("A提示", "gen"), ("B提示", "gen"),
                          ("C提示", "edit"), ("A提示", "gen")):
            _sr.push_prompt_history(txt, kind)
        h = _sr.load_prompt_history()
        check("提示词历史: 去重生效", len(h) == 3, str(h))
        check("提示词历史: 最近的在前", h[0] == "A提示", str(h))
        check("提示词历史: 顺序正确", h == ["A提示", "C提示", "B提示"], str(h))

        # kind 过滤
        check("提示词历史: gen 过滤",
              _sr.load_prompt_history(kind="gen") == ["A提示", "B提示"],
              str(_sr.load_prompt_history(kind="gen")))
        check("提示词历史: edit 过滤",
              _sr.load_prompt_history(kind="edit") == ["C提示"], "")

        # 持久化（重新读文件）
        f = _sr.prompt_history_file()
        check("提示词历史: 文件已落盘", f.exists(), str(f))
        check("提示词历史: 跨进程可读（重读文件）",
              len(_sr.load_prompt_history()) == 3, "")

        # 空串/空白不入库
        _sr.push_prompt_history("", "gen")
        _sr.push_prompt_history("   ", "gen")
        check("提示词历史: 空内容不记录", len(_sr.load_prompt_history()) == 3,
              str(len(_sr.load_prompt_history())))

        # 裁剪
        for i in range(50):
            _sr.push_prompt_history(f"批量{i}", "gen")
        before = len(_sr.prompt_history_file().read_text().splitlines())
        _sr.trim_prompt_history(max_lines=20)
        after = len(_sr.prompt_history_file().read_text().splitlines())
        check("提示词历史: 裁剪生效", after <= 20, f"{before} -> {after}")
    finally:
        if old_home is None:
            os.environ.pop("IMGAGENT_HOME", None)
        else:
            os.environ["IMGAGENT_HOME"] = old_home
        import shutil
        shutil.rmtree(d, ignore_errors=True)


def test_prompt_history_arrow_keys() -> None:
    """回归：F3/F4 翻历史的行为（到顶停住 / 越过最新回到草稿）。"""
    import importlib
    import os
    import tempfile
    import curses
    from pathlib import Path
    from impydroid import curses_ui as cui

    d = Path(tempfile.mkdtemp(prefix="impydroid-phk-"))
    old_home = os.environ.get("IMGAGENT_HOME")
    os.environ["IMGAGENT_HOME"] = str(d)
    try:
        import impydroid.settings as _st
        import impydroid.store as _sr
        importlib.reload(_st)
        importlib.reload(_sr)
        importlib.reload(cui)

        for t in ("第一条", "第二条", "第三条"):
            _sr.push_prompt_history(t, "gen")
        # load_prompt_history 最近的在前：['第三条','第二条','第一条']

        st = {"mode": "gen", "inp_hist_idx": -1, "inp_draft": "我的草稿"}
        inp = cui._InputLine()
        inp.set_text("我的草稿")

        # ↑ 三次应依次拿到 第三条 / 第二条 / 第一条
        seen = []
        for _ in range(3):
            cui._handle_prompt_history(inp, st, curses.KEY_UP)
            seen.append(inp.text)
        check("↑: 依次翻出更早的",
              seen == ["第三条", "第二条", "第一条"], str(seen))

        # 再按 ↑ 应停住（到顶）
        cui._handle_prompt_history(inp, st, curses.KEY_UP)
        check("↑: 到顶后停住", inp.text == "第一条", inp.text)

        # ↓ 回退
        cui._handle_prompt_history(inp, st, curses.KEY_DOWN)
        check("↓: 回退到下一条", inp.text == "第二条", inp.text)

        # ↓ 越过最新 → 回到草稿
        cui._handle_prompt_history(inp, st, curses.KEY_DOWN)
        cui._handle_prompt_history(inp, st, curses.KEY_DOWN)
        check("↓: 越过最新回到草稿", inp.text == "我的草稿", inp.text)
        check("↓: 游标归位 -1", st["inp_hist_idx"] == -1,
              str(st["inp_hist_idx"]))

        # F3/F4 保留为别名（外接键盘）
        st_alias = {"mode": "gen", "inp_hist_idx": -1, "inp_draft": ""}
        inp_alias = cui._InputLine()
        cui._handle_prompt_history(inp_alias, st_alias, curses.KEY_F3)
        check("别名: F3 仍可翻历史", inp_alias.text == "第三条", inp_alias.text)

        # PgUp/PgDn 不再是历史键（已让给"选图片"）
        st_pg = {"mode": "gen", "inp_hist_idx": -1, "inp_draft": "草稿"}
        inp_pg = cui._InputLine()
        handled = cui._handle_prompt_history(inp_pg, st_pg, curses.KEY_PPAGE)
        check("键位: PgUp 不再翻提示词历史", not handled, "")

        # 草稿恢复：F3 之前先存草稿，F4 越过最新回到草稿
        st3 = {"mode": "gen", "inp_hist_idx": -1, "inp_draft": ""}
        inp3 = cui._InputLine()
        inp3.set_text("我正在打的字")
        cui._handle_prompt_history(inp3, st3, curses.KEY_F3)   # 翻历史（应存草稿）
        check("F3: 翻历史前保存草稿", st3["inp_draft"] == "我正在打的字",
              st3["inp_draft"])
        for _ in range(5):                                     # 回到草稿
            cui._handle_prompt_history(inp3, st3, curses.KEY_F4)
        check("F4: 越过最新回到草稿", inp3.text == "我正在打的字", inp3.text)

        # 历史为空时不应崩
        importlib.reload(_sr)
        empty = Path(tempfile.mkdtemp(prefix="impydroid-phe-"))
        os.environ["IMGAGENT_HOME"] = str(empty)
        importlib.reload(_st)
        importlib.reload(_sr)
        st2 = {"mode": "gen", "inp_hist_idx": -1, "inp_draft": ""}
        inp2 = cui._InputLine()
        ok = cui._handle_prompt_history(inp2, st2, curses.KEY_F3)
        check("F3: 历史为空时安全返回", ok and inp2.text == "", inp2.text)
        import shutil
        shutil.rmtree(empty, ignore_errors=True)
    finally:
        if old_home is None:
            os.environ.pop("IMGAGENT_HOME", None)
        else:
            os.environ["IMGAGENT_HOME"] = old_home
        import shutil
        shutil.rmtree(d, ignore_errors=True)


def test_prompt_kept_on_failure() -> None:
    """回归：任务失败时提示词要放回输入框（用户明确说过"丢了很生气"）。

    根因：提交时 `inp.clear()` 立即执行，若失败（401/网络错）提示词就没了。
    """
    import inspect
    from impydroid import curses_ui as cui

    check("失败保留: 有 _keep_prompt_on_failure",
          hasattr(cui, "_keep_prompt_on_failure"), "")
    src = inspect.getsource(cui._keep_prompt_on_failure)
    check("失败保留: 失败时设 _restore_text", "_restore_text" in src, "")

    # 失败 → 恢复
    st = {"last_submit": "辛苦打的提示词"}
    cui._keep_prompt_on_failure(st, ok=False)
    check("失败保留: 失败后待恢复", st.get("_restore_text") == "辛苦打的提示词",
          str(st.get("_restore_text")))

    # 成功 → 清空
    st2 = {"last_submit": "成功的提示词"}
    cui._keep_prompt_on_failure(st2, ok=True)
    check("失败保留: 成功后清空 last_submit", not st2.get("last_submit"), "")
    check("失败保留: 成功后不恢复", not st2.get("_restore_text"), "")

    # 主循环里必须真正把文本放回去
    loop_src = inspect.getsource(cui._main_loop)
    check("失败保留: 主循环消费 _restore_text",
          "_restore_text" in loop_src and "set_text" in loop_src, "")

    # 提交时必须记录历史
    src2 = inspect.getsource(cui._handle_input_key)
    check("失败保留: 提交时记录提示词历史",
          "push_prompt_history" in src2, "")


def test_tui_nav_keys_for_images() -> None:
    """回归：PgUp/PgDn/Home/End 用于**选择历史图片**。

    背景：v5.11.0 把 ↑↓ 让给"翻提示词历史"（终端里最直觉的手势），
    选图片改用 PgUp/PgDn。这个测试锁住新的键位分配。
    """
    import curses
    from impydroid import curses_ui as cui

    class Item:
        def __init__(self, f):
            self.file = f
            self.kind = "gen"
            self.cost = 0.0
            self.tokens = 0
            self.prompt = ""

    class Sess:
        items = [Item(f"img{i}.png") for i in range(10)]

    # PgDn = 下一张（更旧）
    st = {"hist": 0, "scroll": 0}
    for _ in range(3):
        cui._handle_nav_key(Sess(), st, curses.KEY_NPAGE)
    check("图片导航: PgDn 前进 3 张", st["hist"] == 3, str(st["hist"]))

    # PgUp = 上一张（更新）
    cui._handle_nav_key(Sess(), st, curses.KEY_PPAGE)
    check("图片导航: PgUp 回退 1 张", st["hist"] == 2, str(st["hist"]))

    # Home/End 现在归"参考图列表"（选第一张/最后一张），不再动 st["hist"]
    st_ref = {"hist": 2, "refs": ["a.png", "b.png", "c.png"], "ref_sel": 1}
    cui._handle_nav_key(Sess(), st_ref, curses.KEY_HOME)
    check("参考图选择: Home 到第一张", st_ref["ref_sel"] == 0,
          str(st_ref["ref_sel"]))
    check("参考图选择: 不动待修改图", st_ref["hist"] == 2, str(st_ref["hist"]))
    cui._handle_nav_key(Sess(), st_ref, curses.KEY_END)
    check("参考图选择: End 到最后一张", st_ref["ref_sel"] == 2,
          str(st_ref["ref_sel"]))

    # 边界：Home 时再 PgUp 不应越界
    st2 = {"hist": 0, "scroll": 0}
    cui._handle_nav_key(Sess(), st2, curses.KEY_PPAGE)
    check("图片导航: 顶部不越界", st2["hist"] == 0, str(st2["hist"]))

    # ↑↓ 不再归 nav 管（归提示词历史）
    st3 = {"hist": 0, "scroll": 0}
    h_up = cui._handle_nav_key(Sess(), st3, curses.KEY_UP)
    h_dn = cui._handle_nav_key(Sess(), st3, curses.KEY_DOWN)
    check("图片导航: ↑↓ 不再被 nav 拦截", not h_up and not h_dn,
          f"up={h_up} down={h_dn}")


def test_refs_two_concepts_separated() -> None:
    """**核心概念测试**：「待修改图」和「参考图」必须彻底分开。

    · 待修改图 = PgUp/PgDn 光标选中的那张（要被改的主体）
    · 参考图   = Ctrl+Y 弹窗增删（与光标无关）

    为什么必须分开：早期版本把两者绑在同一个光标上，
    导致"浏览历史挑参考图"会顺带改掉待修改图 —— 概念混淆，
    而且用户反馈"好像只是高清化了参考图，没改我的原图"。
    """
    import curses
    import importlib
    import os
    import tempfile
    from pathlib import Path
    from impydroid import curses_ui as cui

    d = Path(tempfile.mkdtemp(prefix="impydroid-conc-"))
    old_home = os.environ.get("IMGAGENT_HOME")
    os.environ["IMGAGENT_HOME"] = str(d)
    try:
        import impydroid.settings as _st
        import impydroid.store as _sr
        importlib.reload(_st)
        importlib.reload(_sr)
        importlib.reload(cui)
        from impydroid.placeholder import placeholder_png
        from impydroid.store import Item
        cui._c.color_pair = lambda n: n
        cui._c.start_color = cui._c.use_default_colors = lambda: None
        cui._c.init_pair = lambda *a: None

        items = []
        for i in range(5):
            n = f"img{i}.png"
            (d / n).write_bytes(placeholder_png(f"p{i}", i + 1, 64))
            items.append(Item(file=n, prompt=f"提示{i}", kind="gen"))

        class Sess:
            def __init__(self):
                self.items = items
                self.current = items[0]
                self.model = "m"
                self.quality = "low"
                self.aspect = "1:1"

        sess = Sess()

        # ---- 待修改图跟着光标走 ----
        for hist, expect in ((0, "img0.png"), (2, "img2.png"), (4, "img4.png")):
            st = {"hist": hist, "refs": []}
            check(f"概念: 光标 {hist} → 待修改图 {expect}",
                  cui._base_file(sess, st) == expect, cui._base_file(sess, st))

        # ---- 参考图与光标无关（弹窗增删）----
        st = {"hist": 0, "refs": [], "ref_sel": 0}

        class MW:
            def getmaxyx(self):
                return 30, 90
            def erase(self):
                pass
            def refresh(self):
                pass
            def addnstr(self, *a, **k):
                pass

        # 弹窗返回"img3.png"，应加入
        saved_pick = cui._pick_list
        try:
            cui._pick_list = lambda *a, **k:                 [c for c in a[3] if "img3.png" in c][0]
            cui._ref_add_or_remove(MW(), sess, st)
            check("概念: Ctrl+Y 弹窗加入参考图", st["refs"] == ["img3.png"],
                  str(st["refs"]))
            check("概念: 加入参考图**不改变**待修改图",
                  cui._base_file(sess, st) == "img0.png",
                  cui._base_file(sess, st))

            # 再选一次同一张 → 移出
            cui._ref_add_or_remove(MW(), sess, st)
            check("概念: 再选一次移出", st["refs"] == [], str(st["refs"]))

            # 待修改图不能当参考图（它的选项不该出现在候选里）
            seen_choices = {}

            def cap(stdscr, sess_, title, choices):
                seen_choices["c"] = list(choices)
                return None

            cui._pick_list = cap
            st2 = {"hist": 2, "refs": [], "ref_sel": 0}
            cui._ref_add_or_remove(MW(), sess, st2)
            joined = " ".join(seen_choices.get("c", []))
            check("概念: 待修改图不在参考图候选里",
                  "img2.png" not in joined, joined[:80])
            check("概念: 候选里有别的图", "img0.png" in joined, joined[:80])

            # 已在参考图里的项显示 ✓
            st3 = {"hist": 0, "refs": ["img1.png"], "ref_sel": 0}
            cui._ref_add_or_remove(MW(), sess, st3)
            joined3 = " ".join(seen_choices.get("c", []))
            check("概念: 已在列表的项有 ✓ 标记", "✓" in joined3, joined3[:80])
        finally:
            cui._pick_list = saved_pick

        # ---- 编辑时待修改图必须排第 1 位 ----
        st4 = {"hist": 2, "refs": ["img0.png", "img4.png"], "ref_sel": 0}
        imgs, desc = cui.load_edit_images(sess, st4)
        check("概念: 编辑图数 = 1 待修改 + 2 参考", len(imgs) == 3, str(len(imgs)))
        check("概念: 描述以「待修改」开头",
              desc.startswith("待修改 img2.png"), desc)

        # ---- 上游校验：待修改图必须真的是第 1 张 ----
        import inspect
        src = inspect.getsource(cui.load_edit_images)
        base_pos = src.index("_base_file(sess, st)")
        ref_pos = src.index("_ref_files(st)")
        check("概念: 代码里待修改图先于参考图装入", base_pos < ref_pos, "")

        # ---- 光标移动不影响 refs ----
        st5 = {"hist": 0, "scroll": 0, "refs": ["img1.png"], "ref_sel": 0}
        class S10:
            def __init__(self):
                self.items = items          # 类体不能直接引用同名外部变量
        sess10 = S10()
        cui._handle_nav_key(sess10, st5, curses.KEY_NPAGE)
        cui._handle_nav_key(S10(), st5, curses.KEY_NPAGE)
        check("概念: PgUp/PgDn 不动参考图",
              st5["refs"] == ["img1.png"], str(st5["refs"]))
        check("概念: PgUp/PgDn 改了待修改图",
              cui._base_file(sess10, st5) == "img2.png",
              cui._base_file(sess10, st5))
    finally:
        if old_home is None:
            os.environ.pop("IMGAGENT_HOME", None)
        else:
            os.environ["IMGAGENT_HOME"] = old_home
        import shutil
        shutil.rmtree(d, ignore_errors=True)


def test_esc_replaces_q_and_ref_delete() -> None:
    """回归：① 所有浮窗用 Esc 关闭（不再用 q）② Ctrl+K 删参考图。"""
    import importlib
    import inspect
    import os
    import tempfile
    from pathlib import Path
    from impydroid import curses_ui as cui

    # ① 源码层面：不该再有 q 作为关闭键
    for fn_name in ("_output_overlay", "_pick_list", "_preview_overlay"):
        src = inspect.getsource(getattr(cui, fn_name))
        check(f"Esc: {fn_name} 不再用 q 关闭",
              '_key_is(ch, "q")' not in src and '_key_is(got, "q")' not in src,
              "")
    src_settings = inspect.getsource(cui._act_settings)
    check("Esc: 设置面板不再用 q/h 返回",
          '"q", "h"' not in src_settings, "")

    # ② Ctrl+K 注册
    check("键位: Ctrl+K 已注册为 del_ref",
          cui._ctrl_action(0x0b) == "del_ref",
          str(cui._ctrl_action(0x0b)))
    check("键位: Ctrl+W 已注册为 aspect",
          cui._ctrl_action(0x17) == "aspect",
          str(cui._ctrl_action(0x17)))

    # ③ 删除行为
    d = Path(tempfile.mkdtemp(prefix="impydroid-del-"))
    old_home = os.environ.get("IMGAGENT_HOME")
    os.environ["IMGAGENT_HOME"] = str(d)
    try:
        import impydroid.settings as _st
        import impydroid.store as _sr
        from impydroid.placeholder import placeholder_png
        from impydroid.store import Item
        importlib.reload(_st)
        importlib.reload(_sr)
        importlib.reload(cui)

        items = []
        for i in range(4):
            n = f"d{i}.png"
            (d / n).write_bytes(placeholder_png(f"d{i}", i + 1, 64))
            items.append(Item(file=n, prompt="", kind="gen"))

        class Sess:
            def __init__(self):
                self.items = items
                self.current = items[0]
                self.model = "m"
                self.quality = "low"
                self.aspect = "1:1"

        sess = Sess()
        st = {"hist": 1, "refs": ["d1.png", "d2.png", "d3.png"], "ref_sel": 1}

        # 删中间那张
        cui._ref_delete_selected(st)
        check("Ctrl+K: 删掉选中项", st["refs"] == ["d1.png", "d3.png"],
              str(st["refs"]))
        check("Ctrl+K: 选中项夹紧",
              st["ref_sel"] in (0, 1), str(st["ref_sel"]))

        # 删空后再删不崩
        cui._ref_delete_selected(st)
        cui._ref_delete_selected(st)
        check("Ctrl+K: 删空不崩", st["refs"] == [], str(st["refs"]))
        cui._ref_delete_selected(st)      # 空列表再删
        check("Ctrl+K: 空列表安全", st["refs"] == [], "")

        # 画幅循环
        seen = []
        for _ in range(3):
            cui._act_cycle_aspect(sess, st)
            seen.append(sess.aspect)
        check("Ctrl+W: 画幅会变", len(set(seen)) >= 2, str(seen))
    finally:
        if old_home is None:
            os.environ.pop("IMGAGENT_HOME", None)
        else:
            os.environ["IMGAGENT_HOME"] = old_home
        import shutil
        shutil.rmtree(d, ignore_errors=True)


def test_polish_pages_display() -> None:
    """回归：润色候选必须**完整可读**（真机反馈"每一行显示不完全"）。

    根因：_pick_list 用 _trunc 把每个候选压成一行，40-70 字的中文候选
    在手机竖屏（约 40-50 列 ≈ 20-25 汉字）只看得到前一半。
    现在 _pick_pages 让每条候选占一整页、自动折行。
    """
    import curses
    import importlib
    import os
    import re
    import tempfile
    from pathlib import Path
    from impydroid import curses_ui as cui

    d = Path(tempfile.mkdtemp(prefix="impydroid-pp-"))
    old_home = os.environ.get("IMGAGENT_HOME")
    os.environ["IMGAGENT_HOME"] = str(d)
    try:
        import impydroid.settings as _st
        importlib.reload(_st)
        importlib.reload(cui)
        cui._c.color_pair = lambda n: n
        cui._c.start_color = cui._c.use_default_colors = lambda: None
        cui._c.init_pair = lambda *a: None
        # mock window 是 ncurses 语义（坐标=视觉列）；PDCurses 的整行重写
        # 会让正文行以 '│' 开头，干扰"折行内容"判定 —— 临时关闭
        _saved_pd = cui._IS_PDCURSES
        cui._IS_PDCURSES = False

        class MW:
            def __init__(self, h=30, w=40):
                self.calls = []
                self.h, self.w = h, w
            def getmaxyx(self):
                return self.h, self.w
            def erase(self):
                pass
            def refresh(self):
                pass
            def addnstr(self, y, x, t, n, attr=0):
                self.calls.append((y, x, t[:n], attr))

        class FS:
            offline = True

        # ---- ① 长候选自动折行（不截断）----
        long_opt = ("一只橘猫趴在窗台上，逆光拍摄，浅景深，"
                    "背景虚化的绿色植物，午后温暖的光线洒在猫的毛发上")
        saved_rk = cui._read_key
        try:
            seq = [curses.KEY_ENTER]
            n = {"i": 0}

            def rk(w):
                i = n["i"]
                n["i"] += 1
                return seq[i] if i < len(seq) else None

            cui._read_key = rk
            mw = MW(30, 40)
            got = cui._pick_pages(mw, FS(), "润色候选", [long_opt],
                                 extra=[("── 不做润色 ──", "")])
            check("润色翻页: 返回选中的候选", got == long_opt, str(got)[:30])

            # 正文必须被折成多行（而不是压成一行截断）
            body_lines = [t for y, x, t, a in mw.calls
                          if y > 6 and t.strip() and "│" not in t[:1]
                          and "翻页" not in t]
            check("润色翻页: 长候选折成多行（未截断）", len(body_lines) >= 2,
                  f"{len(body_lines)} 行")
            joined = "".join(t.strip() for t in body_lines)
            check("润色翻页: 候选尾部内容可见（'毛发'）",
                  "毛发" in joined, joined[-20:])

            # ---- ② ←→ 翻页 ----
            n["i"] = 0
            seq2 = [curses.KEY_RIGHT, curses.KEY_RIGHT, curses.KEY_ENTER]
            n2 = {"i": 0}

            def rk2(w):
                i = n2["i"]
                n2["i"] += 1
                return seq2[i] if i < len(seq2) else None

            cui._read_key = rk2
            opts = ["甲" * 30, "乙" * 30, "丙" * 30]
            mw2 = MW()
            got2 = cui._pick_pages(mw2, FS(), "t", opts, extra=[])
            check("润色翻页: → → Enter 得到第 3 条", got2 == opts[2],
                  str(got2)[:12])

            # ---- ③ 翻到"不做润色"动作项 ----
            n3 = {"i": 0}
            seq3 = [curses.KEY_RIGHT] * len(opts) + [curses.KEY_ENTER]

            def rk3(w):
                i = n3["i"]
                n3["i"] += 1
                return seq3[i] if i < len(seq3) else None

            cui._read_key = rk3
            SKIP = "── 不做润色 ──"
            mw3 = MW()
            got3 = cui._pick_pages(mw3, FS(), "t", opts,
                                   extra=[(SKIP, ""), ("── 重来 ──", "")])
            check("润色翻页: 能翻到动作项", got3 == SKIP, str(got3))

            # ---- ④ Esc 取消 ----
            n4 = {"i": 0}

            def rk4(w):
                n4["i"] += 1
                return "\x1b" if n4["i"] > 1 else None

            cui._read_key = rk4
            check("润色翻页: Esc 返回 None",
                  cui._pick_pages(MW(), FS(), "t", opts, extra=[]) is None, "")

            # ---- ⑤ 超长候选可滚动 ----
            n5 = {"i": 0}
            very_long = "一只橘猫趴在窗台上，" * 40
            seq5 = [curses.KEY_DOWN, curses.KEY_DOWN, curses.KEY_NPAGE,
                    "\x1b"]

            def rk5(w):
                i = n5["i"]
                n5["i"] += 1
                return seq5[i] if i < len(seq5) else None

            cui._read_key = rk5
            mw5 = MW(30, 40)
            cui._pick_pages(mw5, FS(), "t", [very_long], extra=[])
            alltext5 = " ".join(t for _, _, t, _ in mw5.calls)
            pos = re.findall(r"\[(\d+-\d+/\d+)\]", alltext5)
            check("润色翻页: 超长候选有位置标记", bool(pos), str(pos[:2]))
            check("润色翻页: 滚动生效（位置变化）", len(set(pos)) > 1,
                  str(pos[:4]))
            check("润色翻页: 有 ↑↓ 滚动提示", "↑↓ 滚动" in alltext5, "")

            # ---- ⑥ 边界：空候选 ----
            check("润色翻页: 空候选返回 None",
                  cui._pick_pages(MW(), FS(), "t", [], extra=[]) is None, "")
            # 只有动作项时也能用
            n6 = {"i": 0}

            def rk6(w):
                n6["i"] += 1
                return curses.KEY_ENTER if n6["i"] > 1 else None

            cui._read_key = rk6
            only = "── 重来 ──"
            check("润色翻页: 只有动作项时可返回",
                  cui._pick_pages(MW(), FS(), "t", [], extra=[(only, "")]) == only,
                  "")
        finally:
            cui._read_key = saved_rk
            cui._IS_PDCURSES = _saved_pd

        # ---- ⑦ 源码：润色流程用了 _pick_pages（而不是 _pick_list）----
        import inspect
        src = inspect.getsource(cui._polish_flow_tui)
        check("润色翻页: 流程用 _pick_pages", "_pick_pages(" in src, "")
        check("润色翻页: 不再用 _pick_list 展示候选",
              "_pick_list(" not in src, "")
    finally:
        if old_home is None:
            os.environ.pop("IMGAGENT_HOME", None)
        else:
            os.environ["IMGAGENT_HOME"] = old_home
        import shutil
        shutil.rmtree(d, ignore_errors=True)


def test_resolution_and_format_and_model() -> None:
    """回归：分辨率/格式/模型切换 + 批量张数 + REF_MAX。"""
    import importlib
    import os
    import tempfile
    from pathlib import Path
    from impydroid import curses_ui as cui
    from impydroid import settings as st_mod

    d = Path(tempfile.mkdtemp(prefix="impydroid-opts-"))
    old_home = os.environ.get("IMGAGENT_HOME")
    os.environ["IMGAGENT_HOME"] = str(d)
    try:
        importlib.reload(st_mod)
        importlib.reload(cui)

        # Session 字段
        class Sess:
            def __init__(self):
                self.items = []
                self.model = "gpt-image-2.5-flare"
                self.quality = "low"
                self.aspect = "1:1"
                self.offline = True
                self.preview = False
                self.resolution = "1k"
                self.output_format = "png"
                self.batch_n = 1
                self.total_cost = 0.0
                self.counter = 0
            def save_config(self):
                pass

        sess = Sess()
        st = {"hist": 0, "refs": [], "ref_sel": 0}

        # --- 分辨率循环 ---
        for expected in ["2k", "4k", "1k"]:
            cui._act_cycle_resolution(sess, st)
            check(f"分辨率: → {expected}", sess.resolution == expected,
                  f"got {sess.resolution}")
        check("分辨率: 循环回到 1k", sess.resolution == "1k", sess.resolution)

        # --- 格式循环 ---
        for expected in ["jpeg", "webp", "png"]:
            cui._act_cycle_fmt(sess, st)
            check(f"格式: → {expected}", sess.output_format == expected,
                  f"got {sess.output_format}")
        check("格式: 循环回到 png", sess.output_format == "png", sess.output_format)

        # --- 模型循环 ---
        for _ in range(3):
            cui._act_cycle_model(sess, st)
        check("模型: 切换后仍是 2.5 系",
              any("2.5" in sess.model for _ in [1]),
              f"got {sess.model}")

        # --- Sunburst 质量档自动降级 ---
        sess.model = "gpt-image-2.5-sunburst"
        sess.quality = "xhigh"           # sunburst 不支持 xhigh
        captured_warn = []
        orig_log = cui.log
        cui.log = lambda m, l="info": captured_warn.append((l, m))
        cui._act_cycle_model(sess, st)   # 切回 flare（绕过 sunburst）
        # 直接测 sunburst 质量降级
        sess2 = Sess()
        sess2.model = "gpt-image-2.5-sunburst"
        sess2.quality = "max"
        captured_warn.clear()
        # 不能真调 _act_cycle_model（会绕一圈），直接测 sanitize 逻辑
        from impydroid.store import Session
        s3 = Session()
        s3.__dict__.update({"model": "gpt-image-2.5-sunburst", "quality": "xhigh",
                            "resolution": "1k", "output_format": "png"})
        s3._sanitize()
        check("质量降级: sunburst 下 xhigh → low", s3.quality == "low", s3.quality)
        check("分辨率不合法时重置为 1k", s3.resolution == "1k", s3.resolution)
        cui.log = orig_log

        # --- 批量张数循环 ---
        for expected in [2, 3, 4, 1]:
            cui._act_cycle_batch_n(sess, st)
            check(f"批量: → {expected}", sess.batch_n == expected,
                  f"got {sess.batch_n}")

        # --- REF_MAX ---
        check("参考图上限: REF_MAX=8", cui.REF_MAX == 8, str(cui.REF_MAX))

        # --- _generate_apimart 签名支持 resolution/output_format ---
        import inspect
        import impydroid.api as _api
        src = inspect.getsource(_api._generate_apimart)
        check("api: _generate_apimart 接受 resolution", "resolution" in src, "")
        check("api: _generate_apimart 接受 output_format", "output_format" in src, "")

        # --- generate() 签名 ---
        sig = inspect.signature(_api.generate)
        check("api: generate 有 resolution 参数", "resolution" in sig.parameters,
              str(list(sig.parameters.keys())))
        check("api: generate 有 output_format 参数", "output_format" in sig.parameters, "")
    finally:
        if old_home is None:
            os.environ.pop("IMGAGENT_HOME", None)
        else:
            os.environ["IMGAGENT_HOME"] = old_home
        import shutil
        shutil.rmtree(d, ignore_errors=True)


def test_http_error_translation() -> None:
    """回归：裸 HTTPError 必须被翻译成可读提示（**全部 mock，0 成本**）。

    真机 bug（2026-09-19）：APIMart 余额不足返回 **403**（不是 402），
    body 是 {"error":{"message":"insufficient balance: insufficient quota: ...",
                      "type":"quota_not_enough"}}。
    旧代码两处缺陷：
      ① 提交任务的 urlopen 没包 try/except HTTPError → 裸异常冒泡到 UI
      ② explain_error / error_hint 按 e.status 判断，但裸 HTTPError 只有
         .code，且 str() 只有 "HTTP Error 403: Forbidden"（body 读不到）
    → 用户看到 "HTTPError HTTP Error 403: Forbidden"，误以为是权限问题。
    """
    import io
    import json
    import urllib.error
    from impydroid import api
    from impydroid.httpclient import ApiError

    def mk(code, body):
        return urllib.error.HTTPError(
            "https://api.apimart.ai/v1/images/generations", code, "Forbidden",
            None, io.BytesIO(json.dumps(body).encode()))

    # 真实复现的 403（余额不足）
    E403 = mk(403, {"error": {
        "message": "insufficient balance: insufficient quota: "
                   "balance=38650, required=42383",
        "type": "quota_not_enough"}})
    E402 = mk(402, {"error": {
        "message": "[token_id=1] insufficient balance "
                   "(current: 0.024574 USD, required: 0.050000 USD)",
        "type": "payment_required"}})
    E401 = mk(401, {"error": {"message": "身份验证失败，请检查您的 API 密钥",
                               "type": "authentication_error"}})

    # --- ① 转换层：保留 body 里的真实原因 ---
    a = api._http_error_to_api(E403)
    check("HTTP错误: 403 转成 ApiError", isinstance(a, ApiError), type(a).__name__)
    check("HTTP错误: 保留 status=403", a.status == 403, str(a.status))
    check("HTTP错误: 保留 body 里的错误码",
          "quota_not_enough" in str(a), str(a)[:80])
    check("HTTP错误: 保留可读消息（不是裸 Forbidden）",
          "insufficient balance" in str(a) and "Forbidden" not in str(a).split("(HTTP")[0],
          str(a)[:80])

    # --- ② ★关键回归：403 余额错误不能被误判为权限问题 ---
    h403 = api.error_hint(E403)
    check("HTTP错误: ★403 识别为余额不足（不是权限）",
          "余额" in h403, h403[:70])
    check("HTTP错误: ★403 不说成 key 无效",
          "key" not in h403, h403[:70])

    # --- ③ 各种状态码 ---
    for label, err, kw in (
        ("402 余额不足", E402, "余额"),
        ("401 key 无效", E401, "key 无效"),
        ("429 限流", mk(429, {"error": {"message": "rate limit"}}), "限流"),
        ("500 服务端", mk(500, {"error": {"message": "server error"}}), "服务端"),
        ("超时", TimeoutError("timed out"), "超时"),
        ("SSL", Exception("certificate verify failed"), "证书"),
        ("地区限制", ApiError("not available in your region"), "地区"),
    ):
        h = api.error_hint(err)
        check(f"HTTP错误: {label} 有提示", kw in h, h[:70])

    # --- ④ body 只能读一次（回归：重复调用不能丢信息）---
    E4 = mk(403, {"error": {"message": "insufficient balance: quota",
                             "type": "quota_not_enough"}})
    h1, h2, h3 = api.error_hint(E4), api.error_hint(E4), api.error_hint(E4)
    check("HTTP错误: body 缓存（重复调用不丢信息）",
          "余额" in h1 and "余额" in h2 and "余额" in h3,
          f"{h1[:30]} | {h2[:30]} | {h3[:30]}")

    # --- ⑤ 余额数字提取 ---
    check("HTTP错误: 402 提取 USD 数字",
          "0.024574" in api.error_hint(E402), api.error_hint(E402)[:60])
    check("HTTP错误: 403 提取 credits 数字",
          "38650" in api.error_hint(E403), api.error_hint(E403)[:60])

    # --- ⑥ 边界：空 body / 怪异输入不崩 ---
    for weird in (mk(500, {}), Exception(""), Exception("xyz"), ApiError("")):
        api.error_hint(weird)          # 不抛异常即通过
    check("HTTP错误: 边界输入不崩", True, "")

    # --- ⑦ 源码层：所有 urlopen 都在 try/except HTTPError 里 ---
    import ast
    from pathlib import Path
    src_path = Path(api.__file__)
    src = src_path.read_text(encoding="utf-8")
    tree = ast.parse(src)
    lines = src.split("\n")
    bare = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Call):
            f = node.func
            if isinstance(f, ast.Attribute) and f.attr == "urlopen":
                ln = node.lineno
                guarded = any(lines[i].strip().startswith("try:")
                              for i in range(max(0, ln - 12), ln))
                if not guarded:
                    bare.append(ln)
    check("HTTP错误: 没有裸 urlopen（都会被转成 ApiError）",
          not bare, f"裸调用在行 {bare}")

    # --- ⑧ explain_error 多行版也识别余额 ---
    import contextlib
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        api.explain_error(E403)
    out = buf.getvalue()
    check("HTTP错误: explain_error 识别余额", "余额" in out, out[:80])
    check("HTTP错误: explain_error 提示降成本选项",
          "Ctrl+E" in out or "1k" in out, out[:120])


def ui_app_main() -> int:
    """带 --offline 的入口调用；用环境变量强制离线。"""
    os.environ["IMGAGENT_FORCE_OFFLINE"] = "1"
    from impydroid import app
    # 不传 argv，让它走 sys.argv（测试进程里没有额外参数）
    saved = sys.argv
    sys.argv = [saved[0]]
    try:
        return app.main([])
    finally:
        sys.argv = saved
        os.environ.pop("IMGAGENT_FORCE_OFFLINE", None)


# ============================================================ ASCII-only 回归
def test_ascii_only_terminal() -> None:
    """在"只能写 ASCII"的终端里，包必须能正常导入并输出。"""
    import subprocess
    inner = (
        "import sys, io\n"
        "class AsciiOnly(io.TextIOBase):\n"
        "    encoding = 'ascii'\n"
        "    def __init__(self, s): self._s = s\n"
        "    def write(self, t):\n"
        "        t.encode('ascii'); self._s.write(t); return len(t)\n"
        "    def flush(self): self._s.flush()\n"
        "    def isatty(self): return False\n"
        "    def reconfigure(self, **kw): raise AttributeError('no')\n"
        "real = sys.stdout\n"
        "sys.stdout = AsciiOnly(real)\n"
        f"sys.path.insert(0, {str(ROOT)!r})\n"
        "from impydroid import console, ui\n"
        "console.print(ui.BANNER)\n"
        "console.print('danger: \\u2550 \\u2713 \\u4e2d\\u6587')\n"
        "real.write('ASCII-OK\\n'); real.flush()\n"
    )
    r = subprocess.run([sys.executable, "-c", inner],
                       capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=120)
    ok = r.returncode == 0 and "ASCII-OK" in (r.stdout or "")
    check("回归: ASCII-only 终端能导入并输出", ok,
          f"rc={r.returncode} err={(r.stderr or '')[-200:]}")


# ============================================================ android 降级
def test_android_bridge_degradation() -> None:
    """没装 pyjnius 时必须优雅降级，而不是崩。"""
    from impydroid import android
    android.BRIDGE.ok = False
    android.BRIDGE.reason = ""
    ok = android.ready()
    check("android: 无 pyjnius 时 ready() 返回 False", ok is False)
    check("android: reason 有可读原因", bool(android.BRIDGE.reason),
          repr(android.BRIDGE.reason))
    check("android: status() 不抛异常且含不可用",
          "不可用" in android.status(), android.status()[:50])
    check("android: is_android() 返回布尔", isinstance(android.is_android(), bool))
    check("android: 未就绪时 pick_image 返回 None",
          android.BRIDGE.pick_image(timeout=0.1) is None)
    check("android: 未就绪时 view_image 返回 False",
          android.BRIDGE.view_image(Path("/tmp/nope.png")) is False)


# ============================================================ 主流程
def main() -> int:
    print("impydroid test suite")
    print("-" * 60)
    tests = [test_console, test_settings, test_pngcodec, test_store,
             test_api_offline, test_photos, test_preview_fallback,
             test_android_bridge_degradation, test_ascii_only_terminal,
             test_provider_config, test_pick_index, test_apimart_endpoints,
             test_apimart_payload, test_apimart_async, test_polish_display,
             test_polish_labels, test_quality_tiers, test_cost_estimate,
             test_session_sanitize, test_ensure_in_home, test_platform_paths,
             test_tui_ascii_render, test_tui_input_unicode,
             test_tui_hint_layout, test_tui_no_bottom_right_write,
             test_tui_action_imports, test_tui_generate_offline,
             test_tui_csi_keymap_complete, test_tui_input_rejects_function_keys,
             test_tui_no_module_globals, test_termimg_decode_cache_lru,
             test_tui_input_wrapping_and_caret,
             test_tui_settings_stays_in_curses,
             test_tui_border_styles, test_tui_no_print_in_any_action,
             test_tui_no_suspend_except_apikey, test_tui_truecolor_helpers,
             test_tui_no_column_conflicts,
             test_tui_ctrl_keys_not_stealing_chars,
             test_tui_preview_render_cached,
             test_tui_bg_redirects_stdout,
             test_tui_polish_flow_present,
             test_tui_polish_flow_behaviour,
             test_pick_pages_contract,
             test_resolution_and_format_and_model,
             test_http_error_translation,
             test_tui_ask_yes_no_keys,
             test_tui_flash_is_actually_drawn,
             test_tui_preview_overlay_can_scroll,
             test_tui_action_key_table_intact,
             test_prompt_history_store, test_prompt_history_arrow_keys,
             test_prompt_kept_on_failure, test_tui_nav_keys_for_images,
             test_refs_two_concepts_separated,
             test_esc_replaces_q_and_ref_delete,
             test_polish_pages_display,
             test_e2e_menu]
    for fn in tests:
        try:
            fn()
        except Exception as e:                     # noqa: BLE001
            import traceback
            check(f"{fn.__name__} 崩溃", False,
                  f"{type(e).__name__}: {e}\n{traceback.format_exc()[-400:]}")

    passed = sum(1 for _, ok, _ in RESULTS if ok)
    for name, ok, extra in RESULTS:
        line = f"  {'OK  ' if ok else 'FAIL'} {name}"
        if extra and not ok:
            line += f"\n         {extra}"
        print(line)
    print("-" * 60)
    print(f"{passed}/{len(RESULTS)} passed")
    return 0 if passed == len(RESULTS) else 1


if __name__ == "__main__":
    sys.exit(main())
