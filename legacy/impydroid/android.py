"""Android 原生能力（可选，全部有降级）。

干两件事：
  ① 调系统相册选择器（ACTION_OPEN_DOCUMENT）
  ② 调系统看图器预览（ACTION_VIEW）

三条诚实前提：
  1. 需要 Pydroid 3 里装了 **pyjnius**（Pip 菜单搜 pyjnius；需先装 repository plugin）。
     没装就静默降级成"扫描目录 + 输路径"和"打印文件路径"，不影响其它功能。
  2. Pydroid 的 Activity 类名**没有公开文档**。p4a 的 org.kivy.android.PythonActivity
     是 Kivy 专用；Pydroid 用自己的包名 ru.iiec.pydroid3。所以这里**按顺序试多个
     候选类名**，哪个能拿到 mActivity 就用哪个。全失败也不报错。
  3. 拿到 content:// 后用 ContentResolver.openInputStream 读字节（最稳，不依赖文件路径）。
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
import time
from pathlib import Path


from . import settings
from .console import print


# Pydroid / 常见 Android Python 运行时的 Activity 候选
ACTIVITY_CANDIDATES = [
    "ru.iiec.pydroid3.MainActivity",
    "ru.iiec.pydroid3.activity.MainActivity",
    "ru.iiec.pydroid3.PythonActivity",
    "ru.iiec.pydroid3.MainPythonActivity",
    "org.kivy.android.PythonActivity",          # 万一装的是 Kivy 启动器
]

PICK_REQUEST = 0x494D47          # "IMG"


class AndroidBridge:
    """封装所有 pyjnius 调用。任何一步失败 -> self.ok = False，调用方走降级。"""

    def __init__(self) -> None:
        self.ok = False
        self.reason = ""
        self.mode = ""
        self.activity = None
        self._pending = None
        self._intent_cls = None
        self._uri_cls = None
        self._python_activity = None

    # ------------------------------------------------------------ 初始化
    def init(self) -> bool:
        try:
            from jnius import autoclass, cast            # type: ignore
        except Exception as e:                           # noqa: BLE001
            self.reason = f"未安装 pyjnius（{type(e).__name__}）"
            return False

        # 1) 找一个能用的 PythonActivity
        pa = None
        tried: list[str] = []
        for name in ACTIVITY_CANDIDATES:
            try:
                cls = autoclass(name)
                if cls.mActivity is not None:
                    pa = cls
                    self._python_activity = cls
                    break
                tried.append(name + "(mActivity=None)")
            except Exception as e:                       # noqa: BLE001
                tried.append(f"{name}({type(e).__name__})")
        if pa is None:
            self.reason = "找不到可用的 Activity 类：" + ", ".join(tried[:3])
            return False

        try:
            self.activity = cast("android.app.Activity", pa.mActivity)
        except Exception as e:                           # noqa: BLE001
            self.reason = f"cast Activity 失败：{type(e).__name__}"
            return False

        try:
            self._intent_cls = autoclass("android.content.Intent")
            self._uri_cls = autoclass("android.net.Uri")
        except Exception as e:                           # noqa: BLE001
            self.reason = f"取 Intent/Uri 类失败：{type(e).__name__}"
            return False

        self._try_bind_result()
        # 回调注册失败时，看图器(ACTION_VIEW)仍然可用（它不需要返回值），
        # 所以 not ok 只影响"选图"，不影响"预览"。这里保持 ok=True，
        # 但把原因说清楚，由 pick_image 自己判断。
        self.ok = True
        return True

    def _try_bind_result(self) -> None:
        """三种可能的回调注册方式，能注册上哪种就用哪种。"""
        # A: android.activity.bind（p4a 风格）
        try:
            from android import activity as _a           # type: ignore
            _a.bind(on_activity_result=self._on_result)
            self.mode = "android.activity.bind"
            return
        except Exception:                                # noqa: BLE001
            pass
        # B: PythonActivity.registerActivityResultListener（p4a 老风格）
        try:
            self._python_activity.registerActivityResultListener(self._make_listener())
            self.mode = "registerActivityResultListener"
            return
        except Exception:                                # noqa: BLE001
            pass
        # 两种回调注册都失败了。
        # ⚠️ 这里**不能**假装"轮询模式还能work"：Android 的 onActivityResult
        # 没有可轮询的接口，拿不到回调就永远等不到结果（旧版会白等 180 秒然后超时）。
        # 诚实的做法是标记为不可用，让调用方降级到"扫描目录"。
        self.mode = "unavailable"

    def _make_listener(self):
        from jnius import PythonJavaClass, java_method   # type: ignore

        bridge = self

        class _Listener(PythonJavaClass):                # type: ignore
            __javainterfaces__ = [
                "org/kivy/android/PythonActivity$ActivityResultListener"]
            __javacontext__ = "app"

            @java_method("(IILandroid/content/Intent;)V")
            def onActivityResult(self, requestCode, resultCode, intent):
                bridge._on_result(requestCode, resultCode, intent)

        return _Listener()

    def _mode_unavailable(self) -> bool:
        """回调通道是否不可用（拿不到结果）。"""
        return self.mode == "unavailable"

    def _on_result(self, request_code, result_code, intent) -> None:
        if request_code == PICK_REQUEST:
            self._pending = (result_code, intent)

    # ------------------------------------------------------------ 选图
    def pick_image(self, timeout: float = 180.0) -> bytes | None:
        """调系统选择器，返回选中图片的字节；取消/失败返回 None。

        用 ACTION_OPEN_DOCUMENT（SAF 标准入口）：不需要存储权限，
        返回的 content:// 能用 ContentResolver 直接读。
        """
        if not self.ok:
            return None
        self._pending = None
        try:
            intent = self._intent_cls(self._intent_cls.ACTION_OPEN_DOCUMENT)
            intent.addCategory(self._intent_cls.CATEGORY_OPENABLE)
            intent.setType("image/*")
            intent.addFlags(self._intent_cls.FLAG_GRANT_READ_URI_PERMISSION)
        except Exception:                                # noqa: BLE001
            try:
                intent = self._intent_cls(self._intent_cls.ACTION_GET_CONTENT)
                intent.setType("image/*")
            except Exception as e:                       # noqa: BLE001
                self.reason = f"构造 Intent 失败：{type(e).__name__}"
                return None

        # 没有回调通道就别启动选择器 —— 启动了也拿不到结果，只会让用户白等
        if self._mode_unavailable():
            self.reason = ("无法注册选择结果回调（onActivityResult），"
                           "系统选择器拿不到返回值")
            return None
        try:
            self.activity.startActivityForResult(intent, PICK_REQUEST)
        except Exception as e:                           # noqa: BLE001
            self.reason = f"startActivityForResult 失败：{type(e).__name__}: {e}"
            return None

        waited, step = 0.0, 0.2
        while waited < timeout:
            if self._pending is not None:
                break
            time.sleep(step)
            waited += step
        if self._pending is None:
            self.reason = "等待选择结果超时（回调可能没注册上）"
            return None

        result_code, intent = self._pending
        self._pending = None
        if result_code != -1:                            # Activity.RESULT_OK == -1
            return None                                  # 用户取消
        try:
            uri = intent.getData()
        except Exception:                                # noqa: BLE001
            uri = None
        if uri is None:
            return None

        data = self._read_uri(uri)
        if data is None:
            path = self._path_from_intent(intent)
            if path and Path(path).is_file():
                try:
                    return Path(path).read_bytes()
                except OSError:
                    return None
        return data

    def _read_uri(self, uri) -> bytes | None:
        """用 ContentResolver.openInputStream 读 content:// 的字节。"""
        try:
            resolver = self.activity.getContentResolver()
            stream = resolver.openInputStream(uri)
            if stream is None:
                return None
            buf = bytearray()
            chunk = self._new_byte_array(64 * 1024)
            n = stream.read(chunk)
            while n > 0:
                buf.extend(bytes(chunk[:n]))
                if len(buf) > settings.MAX_UPLOAD:
                    break
                n = stream.read(chunk)
            stream.close()
            return bytes(buf) if buf else None
        except Exception as e:                           # noqa: BLE001
            self.reason = f"读取选中文件失败：{type(e).__name__}: {e}"
            return None

    @staticmethod
    def _new_byte_array(size: int):
        try:
            from jnius import autoclass                  # type: ignore
            JArray = autoclass("java.lang.reflect.Array")
            Byte = autoclass("java.lang.Byte")
            return JArray.newInstance(Byte.TYPE, size)
        except Exception:                                # noqa: BLE001
            return bytearray(size)

    @staticmethod
    def _path_from_intent(intent) -> str | None:
        for key in ("android.intent.extra.STREAM",
                    "android.provider.MediaStore.EXTRA_OUTPUT"):
            try:
                u = intent.getParcelableExtra(key)
                if u is not None:
                    return str(u.getPath())
            except Exception:                            # noqa: BLE001
                continue
        return None

    # ------------------------------------------------------------ 预览
    def view_image(self, path: Path, mime: str = "image/*") -> bool:
        """用系统看图器打开本地图片。

        Android 7+ 不允许把 file:// 抛给别的 App（FileUriExposedException），
        所以优先试 FileProvider 的 content://；拿不到再退回 file://——
        某些国产 ROM 上 file:// 仍能用，成败都无所谓，失败会走 Tkinter/打印路径。
        """
        if not self.ok:
            return False
        for uri in (self._content_uri(path), self._file_uri(path)):
            if uri is None:
                continue
            try:
                intent = self._intent_cls(self._intent_cls.ACTION_VIEW)
                intent.setDataAndType(uri, mime)
                intent.addFlags(self._intent_cls.FLAG_GRANT_READ_URI_PERMISSION)
                intent.addFlags(self._intent_cls.FLAG_ACTIVITY_NEW_TASK)
                self.activity.startActivity(intent)
                return True
            except Exception:                            # noqa: BLE001
                continue
        return False

    def _file_uri(self, path: Path):
        try:
            return self._uri_cls.parse("file://" + str(path))
        except Exception:                                # noqa: BLE001
            return None

    def _content_uri(self, path: Path):
        """尝试通过 FileProvider 把本地文件变成 content://。

        Pydroid 自己的 provider authority 未知，所以依次试几个常见写法；
        都不行就返回 None（调用方退回 file://）。
        """
        try:
            from jnius import autoclass                  # type: ignore
            FileProvider = autoclass("androidx.core.content.FileProvider")
            File = autoclass("java.io.File")
            ctx = self.activity
            pkg = ctx.getPackageName()
            for suffix in (".fileprovider", ".provider", ".FileProvider"):
                try:
                    return FileProvider.getUriForFile(ctx, pkg + suffix, File(str(path)))
                except Exception:                        # noqa: BLE001
                    continue
        except Exception:                                # noqa: BLE001
            pass
        return None


BRIDGE = AndroidBridge()          # 全局单例；首次用到时 init()


def ready(auto: bool = True) -> bool:
    """懒初始化，并让失败原因只打印一次。

    ⚠️ Termux 里**没有 JVM**，pyjnius 装了也 import 不了 —— 直接短路，
    避免浪费时间尝试、也避免给出"装 pyjnius"这种在 Termux 下毫无意义的建议。
    """
    if is_termux():
        BRIDGE.ok = False
        BRIDGE.reason = "Termux 没有 JVM，用 termux-open / chafa 代替"
        return False
    if BRIDGE.ok or BRIDGE.reason:
        return BRIDGE.ok
    if not auto:
        return False
    BRIDGE.init()
    return BRIDGE.ok


def status() -> str:
    if not BRIDGE.reason and not BRIDGE.ok:
        ready()
    if BRIDGE.ok:
        return f"可用（{BRIDGE.mode or '?'}）"
    return f"不可用：{BRIDGE.reason}"


def is_android() -> bool:
    return Path("/sdcard").exists() or Path("/system/build.prop").exists()


def is_termux() -> bool:
    """是否跑在 Termux 里。

    判据：`$PREFIX` 指向 com.termux 的应用私有目录 —— 这是 Termux 特有的。
    不能只看 `is_android()`：Termux 里 `/sdcard` 也存在，但权限模型和 Pydroid 完全不同。
    """
    prefix = os.environ.get("PREFIX", "")
    if "com.termux" in prefix:
        return True
    return Path("/data/data/com.termux/files/usr").exists()


def platform_name() -> str:
    """给用户看的平台名（影响提示语和可用能力）。"""
    if os.name == "nt":
        return "windows"
    if is_termux():
        return "termux"
    if is_android():
        return "pydroid"          # Pydroid 3（或其它带 JVM 的 Android Python）
    return "desktop"


def is_windows() -> bool:
    """是否跑在 Windows（桌面）。"""
    return os.name == "nt"


def windows_open(path: Path) -> bool:
    """Windows 系统看图器/资源管理器打开（os.startfile）。

    成功返回 True。失败（文件不存在等）返回 False，调用方走下一级预览。
    """
    if not is_windows():
        return False
    try:
        import os as _os
        _os.startfile(str(path))              # type: ignore[attr-defined]
        return True
    except Exception:                         # noqa: BLE001
        return False


def storage_ready() -> bool:
    """外部存储是否可读可写。Termux 要先跑 termux-setup-storage。"""
    from pathlib import Path as _P
    if is_termux():
        shared = _P.home() / "storage" / "shared"
        try:
            return shared.is_dir() and any(shared.iterdir())
        except (OSError, PermissionError):
            return False
    for d in ("/sdcard", "/storage/emulated/0"):
        try:
            if _P(d).is_dir() and any(_P(d).iterdir()):
                return True
        except (OSError, PermissionError):
            continue
    return False


def request_storage_hint() -> None:
    """读不到外部存储时的可操作提示（按平台给不同步骤）。"""
    plat = platform_name()
    print("  [!] 读不到外部存储里的照片。按你的环境操作：")
    if plat == "termux":
        print("      在 Termux 里执行一次（只需一次，会弹权限框）：")
        print("          termux-setup-storage")
        print("      完成后确认这个目录存在：")
        print("          ls ~/storage/shared/DCIM")
        print("      也可以用菜单 5) 从脚本目录导入（不需要额外权限）")
    elif plat == "pydroid":
        print("      1) 系统设置 -> 应用 -> Pydroid 3 -> 权限 -> 文件和媒体：允许")
        print("      2) 或把图片拷到脚本同目录，用菜单 5) 导入：")
        print(f"         {settings.HOME}")
    else:
        print("      把图片放到这些目录之一，再用菜单 5) 导入：")
        print(f"         {Path.home() / 'Pictures'}")
        print(f"         {settings.HOME}")
    print(f"      当前工作目录：{settings.HOME}")

# ---------------------------------------------------------------- Termux 专用
# Termux 里没有 JVM，pyjnius 装了也用不了；但它有另一套工具：
#   · termux-open   —— 调系统看图器打开文件（termux-tools 自带）
#   · chafa / timg / viu —— 终端里画图，效果远好于我们自带的 ASCII
# 这些都不需要额外 Python 依赖，所以优先用它们。

def _which(cmd: str) -> str | None:
    """找可执行文件（Termux 的 $PREFIX/bin 不一定在 PATH 里全乎）。"""
    import shutil
    found = shutil.which(cmd)
    if found:
        return found
    prefix = os.environ.get("PREFIX", "")
    if prefix:
        cand = Path(prefix) / "bin" / cmd
        if cand.exists():
            return str(cand)
    return None


def termux_open(path: Path) -> bool:
    """用 termux-open 调系统看图器。成功返回 True。

    ⚠️ 注意：Android 的其它 App 读不到 Termux 私有目录（/data/data/com.termux/...），
    所以如果文件在私有目录里，先复制一份到 ~/storage/shared 再打开。
    """
    exe = _which("termux-open")
    if not exe:
        return False
    import subprocess
    import shutil as _sh

    target = path
    tmp_copy: Path | None = None
    home = Path.home()
    shared = home / "storage" / "shared"
    try:
        in_private = str(path.resolve()).startswith("/data/data/com.termux")
    except OSError:
        in_private = False
    if in_private:
        # 需要先放到共享目录，否则系统看图器会"无法打开"
        dest_dir = shared / "Download" / "imgagent-open"
        try:
            dest_dir.mkdir(parents=True, exist_ok=True)
            tmp_copy = dest_dir / path.name
            _sh.copy2(path, tmp_copy)
            target = tmp_copy
        except OSError:
            tmp_copy = None
    try:
        r = subprocess.run([exe, str(target)], capture_output=True, timeout=20)
        return r.returncode == 0
    except Exception:                                     # noqa: BLE001
        return False


def termux_terminal_viewer(path: Path, cols: int | None = None) -> bool:
    """用 chafa / timg / viu 在终端里画图（比自带 ASCII 好看得多）。"""
    import subprocess
    for cmd, args in (("chafa", ["-s", str(cols or 60)]),
                      ("timg", ["-g", f"{cols or 60}x"]),
                      ("viu", ["-w", str(cols or 60)])):
        exe = _which(cmd)
        if not exe:
            continue
        try:
            r = subprocess.run([exe, *args, str(path)],
                               capture_output=True, timeout=30)
            if r.returncode == 0:
                out = r.stdout.decode("utf-8", "replace")
                if out.strip():
                    print(out)
                    return True
        except Exception:                                 # noqa: BLE001
            continue
    return False
