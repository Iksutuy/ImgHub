"""入口：`python3 -m impydroid` 或 `python3 main.py`。

只做三件事：环境自检 -> 菜单循环 -> 退出。
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
from pathlib import Path
import sys


from . import android, api, polish, preview, settings, termimg, ui
from .console import (CJK_OK, STDIO_INFO, SYMBOLS_OK, dim, menu, ok, print,
                      section, warn)
from .httpclient import ApiError
from .store import Session, load_key



def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)

    # --doctor / --diag：环境诊断后直接退出（诊断本身不该受其它参数影响）
    if "--doctor" in argv or "--diag" in argv:
        from . import doctor
        return doctor.run()

    # --home 覆盖数据目录（要在建 Session 之前）
    if "--home" in argv:
        i = argv.index("--home")
        if i + 1 < len(argv):
            settings.set_home(argv[i + 1])
            print(f"  · 数据目录已改为：{settings.HOME}")
    if "--offline" in argv:
        os.environ["IMGAGENT_FORCE_OFFLINE"] = "1"
    if "--no-polish" in argv:
        settings.set_polish(enabled=False)
    for a in argv:
        if a.startswith("--polish-model="):
            settings.set_polish(model=a.split("=", 1)[1])
        elif a.startswith("--polish-url="):
            settings.set_polish(base_url=a.split("=", 1)[1])
        elif a.startswith("--polish-key="):
            settings.set_polish(api_key=a.split("=", 1)[1])
    # 加载已持久化的润色配置（config.json 里的 base_url/model + 独立 key 文件），
    # 让用户在设置里填过一次后下次启动自动生效（此前只能靠环境变量）。
    try:
        _pc = settings.load_polish_config()
        if _pc.get("base_url") and not settings.POLISH_BASE_URL:
            settings.set_polish(base_url=_pc["base_url"])
        if _pc.get("model") and not settings.POLISH_MODEL:
            settings.set_polish(model=_pc["model"])
        if not settings.POLISH_API_KEY:
            _pk = settings.load_polish_key()
            if _pk:
                settings.set_polish(api_key=_pk)
    except Exception:                         # noqa: BLE001
        pass
    # --color=always|never|auto   强制开关颜色（默认 auto：只在终端里上色）
    for a in argv:
        if a.startswith("--color="):
            from .console import set_color
            set_color(a.split("=", 1)[1])
        elif a.startswith("--term-img="):
            os.environ["IMGAGENT_TERM_IMG"] = a.split("=", 1)[1]

    # --tui：启动 curses TUI 模式（需要 curses 模块 + 终端支持）
    _tui_mode = "--tui" in argv or os.environ.get("IMGAGENT_TUI") == "1"

    # CANARY：任何情况下屏幕上都先有一行东西。
    # 连这行都看不到 => 问题不在代码里（文件没存上 / 跑的不是这个文件）。
    print("IMGAGENT-CANARY: started (stdout=%s symbols=%s cjk=%s)"
          % (STDIO_INFO.get("stdout"), SYMBOLS_OK, CJK_OK))

    # 必须**最先**建 Session：它会把上次保存的 provider 从 config.json 恢复回来。
    # 否则环境信息里的「生图 API」会显示默认值（OpenRouter），
    # 而后面 load_key() 又按错误的 provider 找 key —— 两个问题同源。
    sess = Session()

    # TUI 模式：交互式终端自动启动；非交互（管道/脚本）退回命令行菜单
    _interactive = sys.stdin.isatty() and sys.stdout.isatty()
    _want_tui = _tui_mode or _interactive
    if _want_tui:
        from . import curses_ui
        if curses_ui._HAS_CURSES and _interactive:
            try:
                # 恢复色彩设置
                for a in argv:
                    if a.startswith("--color="):
                        from .console import set_color
                        set_color(a.split("=", 1)[1])
                return curses_ui.run(sess, key=None, offline=bool(os.environ.get("IMGAGENT_FORCE_OFFLINE")))
            except Exception as _e:
                print(f"  · TUI 不可用（{_e}），退回命令行菜单")

    from .console import color_enabled
    print(menu(ui._banner()))
    section("环境")
    print(f"  {dim('数据目录')} {settings.HOME}")
    if settings.HOME == settings.SCRIPT_DIR:
        print(dim("              （= 脚本所在目录，key/设置/历史/图片都放一起）"))
    else:
        print(f"  {dim('脚本目录')} {settings.SCRIPT_DIR}")
    from . import __version__ as _ver
    print(f"  {dim('版本')} {_ver}    {dim('运行环境')} {android.platform_name()}")
    print(f"  {dim('系统')} {platform.system()} {platform.release()}  "
          f"Python {platform.python_version()}")
    from .settings import provider_label
    print(f"  {dim('颜色')} {'开' if color_enabled() else '关（管道/重定向时自动关闭）'}"
          f"   {dim('终端预览')} {termimg.describe()}")
    print(f"  {dim('生图 API')} {provider_label()}")
    if polish.enabled():
        print(f"  {dim('提示词润色')} 开（{polish.describe()}）")
    else:
        print(f"  {dim('提示词润色')} 关")

    # 平台能力探测（失败不阻断，只降级）。
    # 四种平台能力完全不同，提示语必须分开 —— 否则 Windows 上会看到
    # "安卓原生能力 / 装 pyjnius" 这种对它毫无意义的残留提示。
    _plat = android.platform_name()
    if _plat == "termux":
        tools = [t for t in ("termux-open", "chafa", "timg", "viu") if android._which(t)]
        if android.storage_ready():
            print(f"  {ok('[OK]')} 外部存储：可访问（{Path.home() / 'storage' / 'shared'}）")
        else:
            print(f"  {warn('[!]')} 外部存储：不可访问 —— 先在 Termux 里跑 "
                  f"termux-setup-storage")
        if tools:
            print(f"  {ok('[OK]')} Termux 工具：{'、'.join(tools)}")
        else:
            print(dim("  · 没装预览工具（可选）：pkg install chafa termux-api"))
    elif _plat == "pydroid":
        if android.ready():
            print(f"  {ok('[OK]')} 安卓原生能力：{android.status()}")
        else:
            print(f"  {dim('·')} 安卓原生能力：{android.status()}")
            print(dim("      （装 pyjnius 可用系统相册选择器 + 系统看图器；"
                      "不装也能用终端预览）"))
    elif _plat == "windows":
        # Windows 用 os.startfile 调系统看图器，无需任何额外依赖
        print(f"  {ok('[OK]')} 系统看图器：可用（os.startfile）"
              f"   终端预览 {termimg.describe()}")
        from . import curses_ui as _cui
        if not _cui._HAS_CURSES:
            print(dim("  · TUI 需要 windows-curses：pip install windows-curses"
                      "（不装则用本菜单）"))
    else:
        # 桌面 Linux / 其它
        print(f"  {dim('·')} 桌面环境：系统看图器{'可用' if android.ready() else '不可用'}"
              f"   终端预览 {termimg.describe()}")

    key = None
    force_offline = bool(os.environ.get("IMGAGENT_FORCE_OFFLINE"))
    if not force_offline:
        # 只有在非强制离线时才问 key —— 用户明确要求离线，还追问 key 是骚扰
        key = load_key()
    else:
        print(dim("  · 已指定 --offline：跳过 API key（不联网、不花钱）"))

    offline = sess.offline or key is None or force_offline

    if key and not force_offline:
        try:
            info = api.check_key(key)
            if info.get("_provider") == "apimart":
                # APIMart 没有余额查询接口，只能确认 key 有效 + 列出模型数
                n = info.get("_model_count", 0)
                print(f"  {ok('[OK]')} key 已就绪（{settings.key_file_for().name}），"
                      f"账号可用模型 {n} 个")
            else:
                d = info.get("data", {})
                used = d.get("usage", 0)
                limit = d.get("limit")
                print(f"  {ok('[OK]')} key 已就绪（{settings.key_file_for().name}），"
                      f"累计已用 ${used:.4f}"
                      f"{f'，额度上限 ${limit}' if limit else ''}")
        except ApiError as e:
            print(f"  {warn('[!]')} key 校验失败：{e}")
            if not ui.confirm("改用离线模式继续？"):
                return 1
            offline = True
            sess.offline = True
            sess.save_config()
    else:
        print(dim("  · 没配 key —— 已自动进入离线模式（生成占位图，不联网不花钱）"))

    if offline and key:
        print(dim("  · 当前是离线模式（设置里可切回在线）"))

    while True:
        section("当前状态")
        ui.show_current(sess)
        ui.show_status(sess, offline, len(sess.items))
        section("菜单")
        ui.show_menu()
        try:
            c = ui.ask("选").strip()
        except (EOFError, KeyboardInterrupt):
            break
        except StopIteration:
            break

        if c in ("0", "q", "quit", "exit"):
            break
        elif c == "1":
            ui.do_generate(sess, key, offline)
        elif c == "2":
            ui.do_edit(sess, key, offline)
        elif c == "3":
            ui.do_upload(sess)
        elif c == "4":
            ui.do_history(sess)
        elif c == "5":
            ui.do_import_from_home(sess)
        elif c == "6":
            cur = sess.current
            if not cur:
                print(f"  {warn('还没有图')} 先用 1 生成，或 3 上传一张。")
            else:
                preview.show_menu(cur.path, cur.prompt)
        elif c == "7":
            offline = ui.do_settings(sess, offline)
        elif c == "8":
            ui.do_data_dir()
        elif c == "9":
            ui.do_android_status()
        elif c == "u":
            # 撤回最近一张（undo）
            ui.do_undo(sess)
            print(dim("  按任意键继续…"))
            try:
                input()
            except (EOFError, KeyboardInterrupt):
                break
        elif c == "p":
            # 快捷键：查看所有近期提示词（方便复用）
            ui.show_recent_prompts(sess)
            print(dim("  按任意键继续…"))
            try:
                input()
            except (EOFError, KeyboardInterrupt):
                break
        elif c in ("d", "doctor", "diagnose"):
            from . import doctor
            doctor.run()
        else:
            print(f"  {warn('没这个选项')}，输入 0-9、u、p 或 --tui 启动 TUI 模式")

    section("结束")
    print(f"  图片都在：{settings.HOME}")
    return 0


def cli() -> int:
    """给 console_scripts 入口用。"""
    try:
        return main()
    except KeyboardInterrupt:
        print("\n  已中断")
        return 130


if __name__ == "__main__":
    sys.exit(cli())
