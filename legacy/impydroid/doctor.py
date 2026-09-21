"""环境诊断 —— 出问题时先跑它：`python3 main.py --doctor`。

**故意只用 ASCII 输出**：这个模块的存在就是为了查出"终端能不能正常打印"，
如果它自己用了装饰字符，就会死于同一个问题、什么也查不出来。

检查项：
    解释器 / 平台 / cwd / __file__
    stdout stderr 的编码与 errors
    能否打印 ASCII、汉字、符号、emoji（逐项单独尝试，失败那项就是元凶）
    PYTHONIOENCODING / PYTHONUTF8 环境变量
    可选模块：certifi / jnius / tkinter / PIL
    存储：/sdcard 等目录是否存在、可写
    数据目录候选：逐个试写
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

import os
import platform
import sys
from pathlib import Path


from . import settings




def _p(msg: str = "") -> None:
    """ASCII-only 输出，永不抛异常，永远 flush。"""
    text = str(msg).encode("ascii", "replace").decode("ascii")
    try:
        sys.stdout.write(text + "\n")
        sys.stdout.flush()
    except Exception:                                # noqa: BLE001
        try:
            sys.stderr.write(text + "\n")
            sys.stderr.flush()
        except Exception:                            # noqa: BLE001
            pass


def run(stream=None) -> int:
    """打印完整诊断。返回 0（诊断本身不判断好坏）。"""
    out = stream.write if stream is not None else None

    def emit(msg: str = "") -> None:
        if out is not None:
            out(msg + "\n")
        else:
            _p(msg)

    emit("=" * 62)
    emit("imgagent doctor  --  python %s" % sys.version.split()[0])
    emit("=" * 62)

    emit("")
    emit("[1] PLATFORM")
    emit("    python     : %s" % sys.version.replace("\n", " "))
    emit("    platform   : %s %s %s" % (platform.system(), platform.release(),
                                        platform.machine()))
    # 平台类型决定能力（Termux 没有 JVM；Pydroid 有；桌面靠 xdg-open）
    try:
        from . import android as _android
        emit("    运行环境   : %s" % _android.platform_name())
        emit("    外部存储   : %s" % ("可访问" if _android.storage_ready()
                                      else "不可访问（Termux 请跑 termux-setup-storage）"))
        if _android.is_termux():
            for tool in ("termux-open", "chafa", "timg", "viu"):
                emit("      %-10s: %s" % (tool, _android._which(tool) or "未安装"))
    except Exception as _e:                                   # noqa: BLE001
        emit("    平台探测   : 失败 %s" % _e)
    emit("    executable : %s" % sys.executable)
    emit("    is_android : %s" % (Path("/sdcard").exists() or
                                  Path("/system/build.prop").exists()))

    emit("")
    emit("[2] HOW IS IT BEING RUN?")
    emit("    cwd        : %s" % os.getcwd())
    pkg_file = Path(__file__).resolve()
    emit("    package    : %s" % pkg_file)
    emit("    script_dir : %s" % settings.SCRIPT_DIR)

    emit("")
    emit("[3] STDOUT ENCODING  (the usual cause of a black screen)")
    for name in ("stdout", "stderr", "stdin"):
        st = getattr(sys, name, None)
        if st is None:
            emit("    %-7s : <None>" % name)
            continue
        emit("    %-7s : encoding=%r errors=%r"
             % (name, getattr(st, "encoding", None), getattr(st, "errors", None)))
    try:
        import locale
        emit("    preferred encoding : %r" % locale.getpreferredencoding(False))
    except Exception as e:                            # noqa: BLE001
        emit("    preferred encoding : error %s" % type(e).__name__)
    emit("    PYTHONIOENCODING   : %r" % os.environ.get("PYTHONIOENCODING"))
    emit("    PYTHONUTF8         : %r" % os.environ.get("PYTHONUTF8"))

    emit("")
    emit("[4] CAN THE TERMINAL ACTUALLY PRINT THESE?")
    emit("    (each attempted separately; a FAIL is the smoking gun)")
    tests = [
        ("ascii letter", "A"),
        ("chinese char", "\u4e2d"),
        ("box char    ", "\u2550"),
        ("check mark  ", "\u2713"),
        ("cross mark  ", "\u2717"),
        ("warning sign", "\u26a0"),
        ("emoji       ", "\U0001F441"),
    ]
    for label, ch in tests:
        try:
            sys.stdout.write(ch)
            sys.stdout.flush()
            sys.stdout.write("  <- %s : OK\n" % label)
            sys.stdout.flush()
        except Exception as e:                        # noqa: BLE001
            try:
                sys.stderr.write("    %s : FAIL %s\n" % (label, type(e).__name__))
                sys.stderr.flush()
            except Exception:                         # noqa: BLE001
                pass

    emit("")
    emit("[5] WHAT ERROR DO WE GET IF PRINTING FAILS?")
    try:
        sys.stdout.write("\u2550")
    except Exception as e:                            # noqa: BLE001
        emit("    type : %s" % type(e).__name__)
        emit("    text : %s" % str(e).encode("ascii", "replace").decode("ascii"))
    else:
        emit("    no error raised")

    emit("")
    emit("[6] OPTIONAL MODULES")
    for mod in ("certifi", "jnius", "tkinter", "PIL", "PIL.ImageTk",
                "ssl", "urllib.request", "json", "zlib", "struct"):
        try:
            __import__(mod)
            emit("    %-16s : available" % mod)
        except Exception as e:                        # noqa: BLE001
            emit("    %-16s : MISSING (%s)" % (mod, type(e).__name__))

    emit("")
    emit("[7] ANDROID BRIDGE")
    try:
        from . import android
        emit("    status : %s" % android.status())
        if not android.BRIDGE.ok:
            if android.is_windows():
                emit("    hint   : Windows 上无需 pyjnius - 预览走系统看图器")
            else:
                emit("    hint   : install pyjnius via  Pydroid menu -> Pip -> pyjnius")
    except Exception as e:                            # noqa: BLE001
        emit("    error  : %s: %s" % (type(e).__name__, e))

    emit("")
    emit("[8] STORAGE")
    for d in ("/sdcard", "/sdcard/Download", "/sdcard/Pictures",
              "/storage/emulated/0", os.path.expanduser("~")):
        try:
            emit("    %-22s exists=%-5s writable=%s"
                 % (d, os.path.isdir(d), os.access(d, os.W_OK)))
        except Exception as e:                        # noqa: BLE001
            emit("    %-22s error %s" % (d, type(e).__name__))

    emit("")
    emit("[9] DATA DIRECTORY CANDIDATES")
    try:
        from .settings import _candidate_homes
        cands = _candidate_homes(settings.SCRIPT_DIR, Path.home())
    except Exception:                        # noqa: BLE001
        sdir = settings.SCRIPT_DIR
        cands = [sdir, sdir / "imgagent_data",
                 Path("/sdcard/Download/imgagent"),
                 Path("/storage/emulated/0/Download/imgagent"),
                 Path.home() / "imgagent"]
    for c in cands:
        try:
            c.mkdir(parents=True, exist_ok=True)
            probe = c / ".write_probe"
            probe.write_text("x", encoding="utf-8")
            probe.unlink()
            emit("    %-48s WRITABLE" % c)
        except Exception as e:                        # noqa: BLE001
            emit("    %-48s NO (%s)" % (c, type(e).__name__))
    emit("    -> chosen : %s" % settings.HOME)

    emit("")
    emit("=" * 62)
    emit("DONE.")
    emit("=" * 62)
    return 0


if __name__ == "__main__":
    sys.exit(run())
