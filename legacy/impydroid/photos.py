"""相册图片发现与选择。

优先调系统选择器（需要 pyjnius）；不可用则扫描常见相册目录让你输序号。
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

import time
from pathlib import Path


from . import android, settings
from .console import dim, print, prompt as cprompt
from .pngcodec import ext_for, png_size, sniff_media_type
from .store import safe_filename


# 照片目录候选。
#
# 三个平台路径不同，所以全部列出来，扫不到的会自动跳过（`is_dir()` 检查）：
#   · Pydroid 3：/sdcard/... 需要"文件和媒体"权限
#   · Termux   ：跑过 `termux-setup-storage` 后才有 ~/storage/shared/...
#                （它是指向 /storage/emulated/0 的符号链接）
#   · 桌面 Linux：~/Pictures、~/Downloads
def _photo_dirs() -> list[str]:
    """按当前环境动态生成目录清单。

    ⚠️ 顺序按平台分：Termux 下 `/sdcard` 常常**存在但遍历起来很慢/被拒**
    （SELinux），而 `~/storage/shared/...` 才是官方软链。把后者放前面
    能更快命中、也避免在 6 秒预算里被无用的 `/sdcard` 遍历耗光。

    扫描本身有总时间预算（见 scan 的 budget 参数），所以宁可把
    "最可能命中的目录"排前面。
    """
    from pathlib import Path as _P
    try:
        from .android import is_termux
        on_termux = is_termux()
    except Exception:                                        # noqa: BLE001
        on_termux = False

    home = _P.home()
    # DCIM/Camera 排在最前：系统相册必扫此目录，用户最容易找到
    subdirs = ["DCIM/Camera", "DCIM", "Pictures", "Pictures/Screenshots",
               "Download", "Download/Telegram", "Download/imgagent",
               "Documents", "WeiXin", "tencent/MicroMsg/Download"]

    termux_dirs = [str(home / "storage" / "shared" / s) for s in subdirs]
    termux_dirs += [str(home / "storage" / n)
                    for n in ("shared", "dcim", "pictures", "downloads")]
    android_dirs = [f"/sdcard/{s}" for s in subdirs]
    android_dirs += [f"/storage/emulated/0/{s}" for s in subdirs]
    desktop_dirs = [
        str(home / "Pictures"), str(home / "Downloads"),
        str(home / "Desktop"), str(home / "图片"),
        str(home / "Pictures" / "Screenshots"),
    ]

    if on_termux:
        return termux_dirs + android_dirs + desktop_dirs
    return android_dirs + termux_dirs + desktop_dirs


PHOTO_DIRS = _photo_dirs()


def scan(limit: int = 40, budget: float = 6.0) -> list[Path]:
    """扫描常见目录，按修改时间倒序返回最近的图片。

    `budget` 是**总时间预算（秒）**：某些目录可能是挂载点或巨大目录，
    无脑遍历会让用户盯着屏幕等半天。超预算就带着已找到的结果返回，
    而不是让人干等。Pydroid 里 `/sdcard` 被 SELinux 挡住时也可能很慢。
    """
    started = time.monotonic()
    found: list[tuple[float, Path]] = []
    seen: set[str] = set()
    for d in PHOTO_DIRS:
        if time.monotonic() - started > budget:
            break
        p = Path(d)
        try:
            if not p.is_dir():
                continue
            for f in p.iterdir():
                if time.monotonic() - started > budget:
                    break
                if not f.is_file() or f.suffix.lower() not in settings.IMG_EXT:
                    continue
                real = str(f.resolve()) if f.exists() else str(f)
                if real in seen:
                    continue
                seen.add(real)
                try:
                    found.append((f.stat().st_mtime, f))
                except OSError:
                    continue
        except (PermissionError, OSError):
            continue
    found.sort(key=lambda t: -t[0])
    return [f for _, f in found[:limit]]


def pick(title: str = "选哪一张", extra_dir: Path | None = None) -> Path | None:
    """选一张图。

    返回**本地 Path**：系统选择器给的是 content://，我们会读成字节再落盘到 HOME，
    这样后续流程只认普通文件，简单可靠。
    """
    # ---------- 优先：系统选择器
    if android.ready():
        print(f"\n  正在调系统相册选择器...（{android.status()}）")
        data = android.BRIDGE.pick_image()
        if data:
            media = sniff_media_type(data)
            if media is None:
                print("  [X] 选中的不是图片（或格式不支持）")
            elif len(data) > settings.MAX_UPLOAD:
                print(f"  [X] 超过 {settings.MAX_UPLOAD // 1048576}MB")
            else:
                name = safe_filename("picked", f"picker_{int(time.time())}",
                                     "." + ext_for(media))
                dest = settings.HOME / name
                try:
                    dest.write_bytes(data)
                    wh = png_size(data) if media == "image/png" else None
                    print(f"  [OK] 已从系统选择器取得 {len(data) / 1024:.0f}KB"
                          f"{f'  {wh[0]}x{wh[1]}' if wh else ''}")
                    return dest
                except OSError as e:
                    print(f"  [X] 写入失败：{e}")
        elif android.BRIDGE.reason:
            print(f"  · 系统选择器没成功（{android.BRIDGE.reason}），改用扫描目录")
        else:
            print("  · 你取消了选择，改用扫描目录")
    elif android.BRIDGE.reason:
        print(f"\n  · 系统选择器不可用（{android.BRIDGE.reason}）")
        plat = android.platform_name()
        if plat == "pydroid":
            print("    想用它：Pydroid 里装 pyjnius（Pip 菜单搜 pyjnius）")
        elif plat == "termux":
            # Termux 没有 JVM，pyjnius 帮不上忙；这里能做的就是扫描目录
            print("    Termux 下不用装 pyjnius（没有 JVM）。")
            print("    直接用「扫描目录 + 序号选择」即可 ——")
            print("    首次使用请先跑一次 termux-setup-storage 授权存储。")
        print("    现在改用「扫描目录 + 序号选择」")

    # ---------- 降级：扫描目录
    print("\n  正在扫描相册目录...")
    photos = scan(40)
    if extra_dir and extra_dir.is_dir():
        for f in sorted(extra_dir.iterdir(), key=lambda p: -p.stat().st_mtime):
            if f.is_file() and f.suffix.lower() in settings.IMG_EXT and f not in photos:
                photos.insert(0, f)

    if not photos:
        android.request_storage_hint()
        manual = input(f"{cprompt('直接输入图片的完整路径')}{dim('（留空返回）')}: ").strip()
        if manual:
            p = Path(manual.strip("'\"")).expanduser()
            if p.is_file():
                return p
            print(f"  [X] 找不到文件：{p}")
        return None

    print(f"  找到 {len(photos)} 张最近的图片：")
    for i, f in enumerate(photos, 1):
        try:
            kb = f.stat().st_size / 1024
            ts = time.strftime("%m-%d %H:%M", time.localtime(f.stat().st_mtime))
        except OSError:
            kb, ts = 0, "?"
        print(f"   {i:2}) {f.name[:38]:40} {kb:7.0f}KB  {ts}  ({f.parent})")

    # 取消必须是取消：这里用 input() 而不是 ui.ask()（后者会把空输入当默认值）
    try:
        raw = input(f"{cprompt(title)}{dim('（序号，留空取消）')}: ").strip()
    except (EOFError, KeyboardInterrupt):
        print()
        return None
    if not raw:
        return None
    try:
        idx = int(raw)
        if 1 <= idx <= len(photos):
            return photos[idx - 1]
        print(f"  [X] 序号要在 1..{len(photos)} 之间")
        return None
    except ValueError:
        p = Path(raw.strip().strip("'\"")).expanduser()
        if p.is_file():
            return p
        print(f"  [X] 无效的输入：{raw}")
        return None
