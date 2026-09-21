"""命令行界面：菜单 + 各个动作。

约定：本模块只管"问什么、显示什么"，实际工作交给 api / store / photos / preview。
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
import unicodedata
from pathlib import Path


from . import (android, api, concurrency, photos, polish, preview,
               settings, termimg)
from .console import (dim, err, info, menu, ok, print, section, warn)
from .console import prompt as cprompt
from .httpclient import ApiError
from .pngcodec import ext_for, png_size, shrink_for_reference, sniff_media_type
from .store import (Item, Session, album_dirs, load_key, safe_filename,
                    save_key)


# 横幅按平台变（宽度按**显示宽度**算 —— 中文/全角算 2 列，否则框线会歪）
_BANNER_W = 58


def _disp_width(t: str) -> int:
    """字符串的终端显示宽度（东亚宽字符算 2）。"""
    import unicodedata
    return sum(2 if unicodedata.east_asian_width(c) in ("W", "F") else 1 for c in t)


def _banner() -> str:
    from . import __version__ as _v
    from .android import platform_name
    ed = {"termux": "Termux edition", "pydroid": "Pydroid 3 edition",
          "windows": "Windows edition",
          "desktop": "desktop edition"}.get(platform_name(), "edition")
    rows = [f"imgagent v{_v} - {ed}",
            "generate / edit a photo / iterate / view"]
    line = "+" + "-" * _BANNER_W + "+"
    body = []
    for r in rows:
        pad = _BANNER_W - _disp_width(r) - 2      # 两侧各留 1 空格
        body.append("| " + r + " " * max(0, pad) + " |")
    return "\n".join([line, *body, line])


BANNER = """
+----------------------------------------------------------+
|  imgagent                                                 |
|  generate / edit a photo / iterate / view                 |
+----------------------------------------------------------+"""

def menu_text() -> str:
    """主菜单文本（第 9 项按平台改名，因为 Termux 下它显示的是 Termux 状态）。"""
    from .android import is_termux
    nine = ("Termux 环境状态   （存储 / 预览工具 / 磁盘）" if is_termux()
            else "安卓支持状态    （相册选择器 / 看图器 能不能用）")
    return f"""
 1) 生成新图        （文生图，可选 AI 润色提示词）
 2) 修改当前图      （多步迭代，可选 AI 润色）
 3) 上传图片        （从本机相册/目录选一张）
 4) 看历史 / 回退   （回到之前任意一张继续改）
 5) 用脚本目录的图  （读不到相册时的备选）
 6) 预览当前图      （终端字符画 / 看图器 / 内置窗口）
 7) 设置            （模型 / 质量 / 画幅 / 离线 / 自动预览）
 8) 查看数据目录    （key、设置、历史、图片都在哪）
 9) {nine}
 u) 撤回最近一张      （撤销上一步生成/编辑，历史记录保留）
 p) 近期提示词       （方便复制/复用）
 0) 退出
"""


MENU = menu_text()      # 兼容旧引用


# ---------------------------------------------------------------- 输入原语
def ask(prompt: str, default: str | None = None) -> str:
    """带默认值的询问。空输入 = 取默认值。

    交互终端下 input() 自动启用 readline，方向键 / 退格天然支持。
    注意：需要"留空 = 取消"语义的地方**不要**用它，用 input() 直接问。
    """
    suffix = dim(f" [{default}]") if default else ""
    prefix = f"{cprompt(prompt)}{suffix}: "
    try:
        s = input(prefix).strip()
    except (EOFError, KeyboardInterrupt):
        print()
        return default or ""
    return s or (default or "")


def confirm(prompt: str, default_yes: bool = True) -> bool:
    d = "Y/n" if default_yes else "y/N"
    s = ask(f"{prompt} [{d}]").strip().lower()
    if not s:
        return default_yes
    return s in ("y", "yes", "1", "是")


# ---------------------------------------------------------------- 显示
def show_current(sess: Session) -> None:
    cur = sess.current
    if not cur:
        print("  （还没有图。选 1 生成，或选 3 上传一张）")
        return
    size = ""
    try:
        wh = png_size(cur.path.read_bytes()[:32])
        if wh:
            size = f" {wh[0]}x{wh[1]}"
    except Exception:                          # noqa: BLE001
        pass
    try:
        kb = cur.path.stat().st_size / 1024
    except OSError:
        kb = 0
    print(f"  当前：{cur.path.name}{size}  {kb:.0f}KB")
    print(f"        类型={cur.kind} 模型={cur.model or '-'} 质量={cur.quality or '-'}")
    cost = f" ${cur.cost:.4f}" if cur.cost else ""
    print(f"        提示词：{cur.prompt[:70]}{'...' if len(cur.prompt) > 70 else ''}{cost}")


def _term_width() -> int:
    """终端可用宽度（Pydroid 里 COLUMNS 常缺失，给个保守默认）。"""
    try:
        import shutil
        w = shutil.get_terminal_size((80, 24)).columns
    except Exception:                                  # noqa: BLE001
        w = 80
    return max(40, min(w, 120))


def _wrap_cjk(text: str, width: int, indent: str = "       ") -> list[str]:
    """按**终端显示宽度**折行（中文算 2 列），返回带缩进的行列表。

    为什么需要这个：直接用 text[:80] 截断，中文会显示不全；
    而且 `…` 省略号会让用户看不到后半句的关键信息（风格、光线等）。
    """
    def w(ch: str) -> int:
        # 东亚宽字符占 2 列
        return 2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1

    lines: list[str] = []
    cur = ""
    cur_w = 0
    limit = width - len(indent)
    for ch in text:
        cw = w(ch)
        if cur_w + cw > limit and cur:
            lines.append(indent + cur)
            cur, cur_w = "", 0
        cur += ch
        cur_w += cw
    if cur:
        lines.append(indent + cur)
    return lines or [indent]


def _show_picks(label: str, options: list[str]) -> None:
    """把选项**完整**打印出来（按显示宽度折行，不截断）。"""
    width = _term_width()
    print(f"\n  {dim(label)}")
    for i, opt in enumerate(options, 1):
        lines = _wrap_cjk(opt, width, indent="       ")
        # 第一行接在序号后面，后续行对齐缩进
        print(f"    {i}) {lines[0].lstrip()}")
        for extra in lines[1:]:
            print(extra)
        print()                                        # 选项之间空一行，便于阅读
    if len(options) < 4:
        print(dim(f"  （只返回了 {len(options)} 条，用 LLM 实际给的）"))


def polish_flow(text: str, *, kind: str = "gen", aspect: str = "",
                subject: str = "") -> str:
    """问要不要 AI 润色；调一次 LLM，拿到 4 个选项让用户挑（Y=n 选 / n=取消）。

    一次调用拿 4 个，比反复 Y/n/r 省 token 和时间（实测节省 60–80%）。
    任何失败都退回原提示词，绝不阻断主流程。
    """
    if not polish.enabled():
        return text
    if not confirm("要用 AI 润色/扩写提示词吗？（一次生成 4 个选项让你挑）", False):
        return text

    # 推理模型会先"想"再"答"，实测 6~23s。先告知预期，不然用户以为卡死。
    print(dim("  · 正在生成润色选项，通常 3~25 秒，请稍候…"))
    t0 = time.time()
    try:
        options = polish.picks(text, kind=kind, aspect=aspect, subject=subject)
    except Exception as e:                     # noqa: BLE001
        polish.explain_error(e)
        return text                            # 失败就用原提示词
    dt = time.time() - t0
    print(f"  {ok('[OK]')} 用时 {dt:.1f}s，得到 {len(options)} 个选项")
    if not options:
        return text

    _show_picks(f"原文：{text}", options)
    print(f"\n  {cprompt('要哪个？')} "
          f"{dim('输入 1-4 选对应选项  n=取消（用原提示词）  r=重新生成 4 个')}")
    try:
        ans = input("> ").strip().lower()
    except (EOFError, KeyboardInterrupt):
        print()
        return text
    if ans in ("n", "no", "0", "取消"):
        print(dim("  · 已取消润色，使用原提示词"))
        return text
    if ans in ("r", "redo", "重来", "再来"):
        return polish_flow(text, kind=kind, aspect=aspect)  # 递归重新生成
    try:
        idx = int(ans)
        if 1 <= idx <= len(options):
            print(f"  {ok('[OK]')} 选择了第 {idx} 条")
            return options[idx - 1]
    except ValueError:
        pass
    print(dim("  · 没这个选项，按「取消」处理"))
    return text


def show_menu() -> None:
    """打印菜单（整块用菜单色）。第 9 项文案随平台变，所以每次现算。"""
    for line in menu_text().strip("\n").split("\n"):
        print(menu(line) if line.strip() else line)


def show_status(sess: Session, offline: bool, n_items: int) -> None:
    mode = "离线(占位图)" if offline else "在线"
    print(f"\n  {dim('模型')} {sess.model}   {dim('质量')} {sess.quality}   "
          f"{dim('画幅')} {sess.aspect}   {dim('模式')} {mode}")
    print(f"  {dim('历史')} {n_items} 张   {dim('累计')} "
          f"${sess.total_cost:.4f}   {dim('终端预览')} {termimg.describe()}")
    cur = sess.current
    if cur and cur.prompt:
        preview = cur.prompt.replace("\n", " ")[:55]
        ellipsis = "…" if len(cur.prompt) > 55 else ""
        print(f"  {dim('当前提示词')} {preview}{ellipsis}")
    if offline:
        print(f"  {warn('离线模式')}：生成的是本地占位图，不联网、不花钱")


def show_recent_prompts(sess: Session, n: int = 5) -> None:
    """展示最近 N 条提示词（方便用户复用或参考）。"""
    items = list(reversed(sess.items[:n]))    # 最近的在前
    if not items:
        print("  （还没有生成记录）")
        return
    print(f"\n  {dim('最近 ' + str(len(items)) + ' 条提示词')}：")
    for i, it in enumerate(items, 1):
        p = (it.prompt or "").replace("\n", " ")[:60]
        ellipsis = "…" if (it.prompt or "") and len(it.prompt) > 60 else ""
        tag = f" [{it.kind}]" if it.kind != "gen" else ""
        cost_str = f" ${it.cost:.4f}" if it.cost else ""
        print(f"    {i:2}) {p}{ellipsis}{tag}{cost_str}")
    print(f"  {dim('想复用某条？在「生成新图」时粘贴，或在「修改当前图」时选 复用上次提示词')}")


# ---------------------------------------------------------------- 动作
def do_generate(sess: Session, key: str | None, offline: bool) -> None:
    section("生成新图")
    prompt = ask(cprompt("提示词（想画什么）"))
    if not prompt:
        print("  已取消（提示词为空）")
        return
    prompt = polish_flow(prompt, kind="gen", aspect=sess.aspect)
    n = 1
    if offline:
        n = 1                     # 离线占位图没必要一次多张，省一次交互
    elif confirm("一次生成多张？", False):
        try:
            n = max(1, min(4, int(ask("生成几张（1-4）", "2") or "2")))
        except ValueError:
            n = 2
    cost, tokens = settings.estimate_detail(sess.quality, "1k", n)
    print(f"  -> 将用 {sess.model} / {sess.quality} / {sess.aspect} x {n}")
    warn_auto = ""
    if sess.quality == "auto":
        warn_auto = dim("（auto 实测落在 low 档；服务端预扣按 max，完成后退回差额）")
    med_cost, med_tok = settings.estimate_detail(sess.quality, "2k", n)
    print(f"     预估花费 ≈ ${cost:.4f}（{tokens} tokens，1:1 ≈ {tokens//n} tok/张）{warn_auto}")
    print(dim(f"     若切 2k：≈ ${med_cost:.4f}（{med_tok} tokens，{med_tok//n} tok/张）"))
    if not offline and not confirm("开始？"):
        return

    t0 = time.time()
    try:
        res = api.generate(prompt, api_key=key or "", model=sess.model,
                           quality=sess.quality, aspect=sess.aspect, n=n,
                           offline=offline, step=sess.counter)
    except ApiError as e:
        api.explain_error(e)
        return

    # 落盘是阻塞 IO：多张时并发写（n=1 时 gather 会自动走单线程，无额外开销）
    items = [(i, data, media) for i, (data, media) in enumerate(res.images)]

    def _land(entry):
        idx, data, media = entry
        path = _save(data, media, prompt, sess.counter + 1 + idx)
        home_path = _ensure_in_home(path, data)
        if home_path is None:
            # 图片写进了相册，但没能放进 HOME —— 不能记成 Item（否则下次启动会消失）
            # 抛出去让上层报错并提示用户"去相册找"
            raise RuntimeError(
                f"图片已存入相册 {path.parent}，但未能放进工作目录"
                f"（{settings.HOME}），无法加入历史。可在菜单 8 查看目录。")
        return idx, Item(file=home_path.name, prompt=prompt,
                         kind="offline" if offline else "gen",
                         model=sess.model, quality=sess.quality,
                         cost=res.cost / max(1, len(res.images)), tokens=res.tokens)

    landed = concurrency.gather(_land, items)
    done, failed = 0, []
    for r in landed:
        if isinstance(r, BaseException):
            failed.append(r)
            continue
        _, it = r
        sess.push(it)
        sess.log(it)
        done += 1
    sess.total_cost += res.cost
    sess.save_state()

    if done and not failed:
        print(f"  {ok('[OK]')} 生成 {done} 张，用时 {time.time() - t0:.1f}s"
              f"{'' if offline else f'，花费 ${res.cost:.4f}'}")
        show_current(sess)
    elif done:
        print(f"  {warn('[!]')} 部分成功：{done}/{len(items)} 张已保存")
        for e in failed:
            print(f"      {err('[X]')} {e}")
        show_current(sess)
    else:
        print(f"  {err('[X]')} 生成失败：图片未能保存")
        if not offline:
            print(f"      服务端已扣费 ${res.cost:.4f}，但落盘失败。")
        print("      排查：菜单 8 看数据目录是否可写 / 空间是否够")
        return
    if done and sess.preview and confirm("现在预览？", True):
        _preview_current(sess)


def do_edit(sess: Session, key: str | None, offline: bool) -> None:
    section("修改当前图")
    cur = sess.current
    if not cur:
        print("  还没有可修改的图。先用 1 生成，或 3 上传一张。")
        return
    # 问是否复用上次提示词作为编辑基础
    reuse_prompt = ""
    if cur.prompt and confirm("复用上次提示词？（会作为编辑指令的基础）", False):
        reuse_prompt = cur.prompt + "\n\n编辑要求："
        print(f"  {dim('复用：')}{reuse_prompt.strip()[:50]}...")
    prompt = ask(cprompt("要改什么（例如：把背景换成雪原，主体不变）"), reuse_prompt)
    if not prompt:
        print("  已取消")
        return
    # ⚠️ 注意：不传 subject。
    # 原因：当用户编辑的编辑指令里已经描述了画面（"保留现在的人物，左边 jet..."），
    # 再把当前图的提示词（可能是完全不同的主题）塞进去会误导模型，
    # 产生"保持橘猫姿势不变 + 让 jet 皱眉"这种牛头不对马嘴的结果。
    # 模型会从用户自己的指令里推断主体，不需要额外上下文。
    # 如果编辑指令极短（比如只写了"换个背景"），用户可以选不用润色。
    prompt = polish_flow(prompt, kind="edit", aspect=sess.aspect)
    try:
        raw = cur.path.read_bytes()
    except OSError as e:
        print(f"  {err('[X]')} 读不到图片：{e}")
        return
    ref, media = shrink_for_reference(raw, 1024)
    ratio = len(ref) / max(1, len(raw))
    print(f"  -> 参考图 {len(raw) / 1024:.0f}KB"
          f"{f' -> 压到 {len(ref) / 1024:.0f}KB' if ratio < 0.95 else ''}（{media}）")
    print(f"     将用 {sess.model} / {sess.quality}")
    if not offline and not confirm("开始修改？"):
        return

    t0 = time.time()
    try:
        res = api.generate(prompt, api_key=key or "", model=sess.model,
                           quality=sess.quality, aspect=sess.aspect,
                           refs=[(ref, media)], offline=offline, step=sess.counter)
    except ApiError as e:
        api.explain_error(e)
        return

    items = [(i, data, m) for i, (data, m) in enumerate(res.images)]

    def _land_edit(entry):
        idx, data, media = entry
        path = _save(data, media, prompt, sess.counter + 1 + idx)
        home_path = _ensure_in_home(path, data)
        if home_path is None:
            raise RuntimeError(
                f"图片已存入相册 {path.parent}，但未能放进工作目录"
                f"（{settings.HOME}），无法加入历史。可在菜单 8 查看目录。")
        return idx, Item(file=home_path.name, prompt=prompt,
                         kind="offline" if offline else "edit",
                         model=sess.model, quality=sess.quality,
                         cost=res.cost / max(1, len(items)),
                         tokens=res.tokens, note=f"基于 {cur.file}")

    n_edited = len(items)
    _cost_per_img = res.cost / max(1, n_edited)
    saved, failed = 0, []
    for r in concurrency.gather(_land_edit, items):
        if isinstance(r, BaseException):
            failed.append(r)
            continue
        _, it = r
        it.cost = _cost_per_img                  # 单张 cost 从总 cost 摊分
        sess.push(it)
        sess.log(it)
        saved += 1
    sess.total_cost += res.cost
    sess.save_state()

    # 分开报告：全成功 / 部分成功 / 全失败
    # 旧代码不管成败都打印"修改完成"，用户以为改好了 —— 实际上当前图还是旧的
    if saved and not failed:
        print(f"  {ok('[OK]')} 修改完成，用时 {time.time() - t0:.1f}s"
              f"{'' if offline else f'，花费 ${res.cost:.4f}'}")
        _show_compare(cur, sess.current)
        show_current(sess)
    elif saved:
        print(f"  {warn('[!]')} 部分完成：{saved}/{len(items)} 张已保存"
              f"，用时 {time.time() - t0:.1f}s")
        for e in failed:
            print(f"      {err('[X]')} {e}")
        show_current(sess)
    else:
        print(f"  {err('[X]')} 修改失败：所有图片都未能保存，当前图未改变")
        print(f"      生成是成功的（已扣费 ${res.cost:.4f}），但落盘失败。")
        print("      排查：菜单 8 看数据目录是否可写 / 空间是否够")
        return                                  # 不放预览（没有新图可看）
    if sess.preview and confirm("现在预览？", True):
        _preview_current(sess)


def do_upload(sess: Session) -> None:
    section("上传图片")
    p = photos.pick("选哪一张", settings.HOME)
    if not p:
        print(dim("  · 已取消，当前图没有改变"))
        return
    try:
        data = p.read_bytes()
    except OSError as e:
        print(f"  {err('[X]')} 读不到：{e}")
        return
    if len(data) > settings.MAX_UPLOAD:
        print(f"  {err('[X]')} 超过 {settings.MAX_UPLOAD // 1048576}MB，先压缩或裁剪")
        return
    media = sniff_media_type(data)
    if media is None:
        print(f"  {err('[X]')} 这不是 PNG/JPEG/WebP/GIF/BMP（文件头不匹配）")
        return
    wh = png_size(data) if media == "image/png" else None
    note = ask("给这张图起个备注（可留空）", p.stem)
    dest_name = safe_filename(time.strftime("%m%d_%H%M%S_import"), p.stem,
                              "." + ext_for(media), limit=28)
    try:
        (settings.HOME / dest_name).write_bytes(data)
    except OSError as e:
        print(f"  {err('[X]')} 无法写入 {settings.HOME}：{e}")
        return
    it = Item(file=dest_name, prompt=note or f"（导入 {p.name}）",
              kind="import", note=str(p))
    sess.push(it)
    sess.log(it)
    print(f"  {ok('[OK]')} 已导入并设为当前图：{dest_name}"
          f"{f'  {wh[0]}x{wh[1]}' if wh else ''}  {len(data) / 1024:.0f}KB")
    print("       -> 现在选 2) 就是基于这张图修改")
    show_current(sess)


def do_history(sess: Session) -> None:
    section("历史记录")
    if not sess.items:
        print("  （历史为空）")
        return
    print(f"\n  {info('历史')} {len(sess.items)} 张（[0] 是当前，序号越大越旧）：")
    for i, it in enumerate(sess.items[:20]):
        preview = (it.prompt or "").replace("\n", " ")[:36]
        ellipsis = "…" if (it.prompt or "") and len(it.prompt) > 36 else ""
        print(f"   {it.title(i)}  ${it.cost:.4f}  {dim(preview + ellipsis)}")
    # 取消必须是取消（不要用 ask）
    try:
        raw = input(f"{cprompt('回到第几号')}{dim('（留空取消）')}: ").strip()
    except (EOFError, KeyboardInterrupt):
        return
    if not raw:
        print(dim("  · 已取消（当前图没变）"))
        return
    try:
        idx = int(raw)
    except ValueError:
        print(f"  {err('[X]')} 无效的序号：{raw}")
        return
    if 0 < idx < len(sess.items):
        it = sess.items.pop(idx)
        sess.items.insert(0, it)
        sess.save_state()
        print(f"  {ok('[OK]')} 已回到 {it.file}")
        show_current(sess)
    elif idx == 0:
        print("  这已经是当前图了")
    else:
        print(f"  {err('[X]')} 序号要在 1..{len(sess.items) - 1} 之间")


def do_import_from_home(sess: Session) -> None:
    """菜单 5：从脚本目录导入（相册读不到时的备选）。"""
    section("从脚本目录导入")
    print(f"\n  脚本目录：{settings.HOME}")
    files = [f for f in sorted(settings.HOME.iterdir(),
                               key=lambda p: -p.stat().st_mtime)
             if f.is_file() and f.suffix.lower() in settings.IMG_EXT]
    if not files:
        print("  （这里没有图片。把图片拷进来再用）")
        return
    for i, f in enumerate(files[:20], 1):
        print(f"   {i:2}) {f.name}")
    try:
        raw = input(f"{cprompt('导入第几号')}{dim('（留空取消）')}: ").strip()
    except (EOFError, KeyboardInterrupt):
        print()
        return
    if not raw:
        print(dim("  · 已取消"))
        return
    try:
        idx = int(raw)
        if not 1 <= idx <= len(files):
            print(f"  {err('[X]')} 序号要在 1..{len(files)} 之间")
            return
        f = files[idx - 1]
        data = f.read_bytes()
        media = sniff_media_type(data)
        if media is None:
            print(f"  {err('[X]')} 这不是支持的图片格式")
            return
        dest = safe_filename(f"import_{int(time.time())}", f.stem,
                             "." + ext_for(media), limit=30)
        (settings.HOME / dest).write_bytes(data)
        it = Item(file=dest, prompt=f"（导入 {f.name}）", kind="import", note=str(f))
        sess.push(it)
        sess.log(it)
        print(f"  {ok('[OK]')} 已导入并设为当前图：{dest}")
    except ValueError:
        print(f"  {err('[X]')} 无效的输入：{raw}")
    except OSError as e:
        print(f"  {err('[X]')} 读/写失败：{e}")



def _pick_index(raw: str, n: int) -> int | None:
    """把用户输入解析成 1-based 索引（返回 0-based），越界返回 None。

    为什么不用 `int(s) - 1` 直接索引：
      `int("0") - 1 == -1`，而 `items[-1]` 在 Python 里**合法**（取最后一项），
      `int("9") - 1 == 8` 对长度 2 的列表会 IndexError 被 except 吞掉，
      但 -1 会静默选错 —— 用户按「取消」却切了 provider。
    """
    try:
        i = int(str(raw).strip())
    except (TypeError, ValueError):
        return None
    if 1 <= i <= n:
        return i - 1
    return None


def _configure_polish() -> None:
    """交互式配置润色：端点 / 模型 / key。全部可留空跳过。

    为什么需要它：润色此前只能靠环境变量配置（重启就没了），且分发版曾内置
    私有 key/端点。现在改为用户自填，并持久化到数据目录（config.json + 独立
    key 文件），下次启动自动恢复。
    """
    print(f"\n  {info('配置提示词润色')}")
    print(dim("    （留空表示不改动该项；key 会存到数据目录，与生图 key 分开）"))
    print(f"    {dim('当前端点')} {polish._endpoint()}")
    print(f"    {dim('当前模型')} {polish._model()}")
    _k = polish._key()
    print(f"    {dim('当前 key')} "
          f"{(_k[:6] + '...' + _k[-4:]) if _k and len(_k) > 10 else (_k or '（未配置）')}")

    base = ask("端点 base_url（留空跳过）", "").strip()
    model = ask("模型名（留空跳过）", "").strip()
    new_key = ask("粘贴 key（留空跳过）", "").strip()

    if base:
        settings.set_polish(base_url=base, persist=True)
        print(f"  {ok('[OK]')} 端点 -> {settings.POLISH_BASE_URL}")
    if model:
        settings.set_polish(model=model, persist=True)
        print(f"  {ok('[OK]')} 模型 -> {settings.POLISH_MODEL}")
    if new_key:
        settings.set_polish(api_key=new_key, persist=True)
        f = settings._POLISH_KEY_FILE
        print(f"  {ok('[OK]')} key 已保存到 {f.name}，本次会话已生效")
    if not (base or model or new_key):
        print(dim("  · 未做任何改动"))
        return
    print(f"  {dim('·')} 润色当前："
          f"{'可用' if polish.enabled() else '仍不可用（缺 key 或已关闭）'}")


def _input_key_for_current_provider(sess: Session) -> bool:
    """为当前 provider 输入并保存 key。返回是否成功输入。

    与旧版的区别：不再硬编码 sk-or- 前缀校验，改为按 provider 判断，
    并且明确报告保存失败（而不是静默成功）。
    """
    from .settings import provider_label, key_file_for, key_hint, looks_like_key
    label = provider_label()
    cur = load_key(silent=True)
    masked = (cur[:6] + "..." + cur[-4:]) if cur and len(cur) > 10 else (cur or "")
    print(f"\n  {info(label + ' API Key')}")
    print(f"    {dim('端点')} {settings.provider_base()}")
    print(f"    {dim('当前')} {masked or '（未设置）'}")
    print(f"    {dim('提示')} {key_hint()}")
    new_key = ask("粘贴 key（留空取消）", "").strip()
    if not new_key:
        print(dim("  · 已取消"))
        return False
    if not looks_like_key(new_key):
        print(f"  {warn('[!]')} 这看起来不像 {label} 的 key（{key_hint()}）")
        if not confirm("仍然保存？", False):
            return False
    saved = save_key(new_key)          # 按 provider 路由到正确的文件
    if saved:
        f = key_file_for()
        print(f"  {ok('[OK]')} key 已保存到 {f.name}，本次会话已生效")
        return True
    print(f"  {warn('[!]')} key 未能写入磁盘，本次会话仍可用")
    return True


def do_settings(sess: Session, offline: bool) -> bool:
    """设置。返回新的 offline 状态。"""
    section("设置")
    while True:
        cur = sess.current
        prompt_line = ""
        if cur and cur.prompt:
            p = cur.prompt[:45] + "…" if len(cur.prompt) > 45 else cur.prompt
            prompt_line = f"  当前提示词：{p}"
        print(f"\n  当前：模型={sess.model}")
        print(f"        质量={sess.quality}  画幅={sess.aspect}")
        print(f"        离线={'是' if offline else '否'}"
              f"  生成后自动问预览={'是' if sess.preview else '否'}")
        if prompt_line:
            print(prompt_line)
        print("   1) 换模型（白名单内清单）   2) 手输模型名")
        print("   3) 质量                     4) 画幅")
        print("   5) 切换离线模式             6) 切换「生成后自动问预览」")
        from .settings import provider_label
        print(f"   当前 provider={provider_label()}  "
              f"{dim('7) 换 provider')}  "
              f"8) 设置 API key（当前 provider）")
        print(f"   9) 列出账号可用模型         "
              f"0) 返回  10) 查看润色配置  11) 润色开关（{'开' if polish.enabled() else '关'}）")
        print(f"   12) 配置润色（端点/模型/key）")
        c = ask("选", "0")
        if c == "1":
            from .settings import provider_label
            choices = settings.model_choices()
            print(f"    {dim('当前 provider=' + provider_label() + ' 的预设清单')}")
            for i, m in enumerate(choices, 1):
                print(f"    {i}) {m}")
            s = ask("选哪个", "1")
            idx = _pick_index(s, len(choices))
            if idx is None:
                print(dim("  · 无效选择，未更改"))
            else:
                sess.model = choices[idx]
                print(f"  {ok('[OK]')} 模型 -> {sess.model}")
                # 换了模型可能导致质量档位失效（xhigh/max 只 2.5 系支持）
                if not settings.quality_supported(sess.quality, sess.model):
                    old_q = sess.quality
                    sess.quality = "low"
                    print(f"  {dim('·')} 该模型不支持 {old_q}，质量已重置为 low")
            sess.save_config()
        elif c == "2":
            from .settings import provider_label
            if settings.API_PROVIDER == "apimart":
                print("    提示：APIMart 用**裸模型名**，如 gpt-image-2.5-flare")
            else:
                print("    提示：OpenRouter 用 provider/model，如 openai/gpt-image-1-mini")
            v = ask("模型名", sess.model)
            if v:
                sess.model = v.strip()
                if not settings.model_matches_provider(sess.model):
                    print(f"  {warn('[!]')} 这个模型名和 {provider_label()} 的命名习惯不符，"
                          f"生成时可能报错")
                if not settings.quality_supported(sess.quality, sess.model):
                    old_q = sess.quality
                    sess.quality = "low"
                    print(f"  {dim('·')} 该模型不支持 {old_q}，质量已重置为 low")
                sess.save_config()
        elif c == "3":
            choices = settings.quality_choices(sess.model)
            for i, q in enumerate(choices, 1):
                cost = settings.estimate_cost(q, "1k", 1)
                note = ""
                if q == "auto":
                    note = dim("  由模型自选（实测=low）；预扣额度按 max 计，完成后退回")
                elif q == "max":
                    note = dim("  最贵，约 low 的 40 倍")
                print(f"    {i}) {q:<7} ≈${cost:.4f}/张{note}")
            if len(choices) < len(settings.QUALITIES_APIMART):
                print(dim("    （当前 provider/模型只支持这些档位；"
                          "xhigh/max 需要 APIMart + gpt-image-2.5 系）"))
            s = ask("选哪个", "1")
            idx = _pick_index(s, len(choices))
            if idx is None:
                print(dim("  · 无效选择，未更改"))
            else:
                sess.quality = choices[idx]
                print(f"  {ok('[OK]')} 质量 -> {sess.quality}")
            sess.save_config()
        elif c == "4":
            for i, a in enumerate(settings.ASPECTS, 1):
                print(f"    {i}) {a}")
            s = ask("选哪个", "1")
            idx = _pick_index(s, len(settings.ASPECTS))
            if idx is None:
                print(dim("  · 无效选择，未更改"))
            else:
                sess.aspect = settings.ASPECTS[idx]
                print(f"  {ok('[OK]')} 画幅 -> {sess.aspect}")
            sess.save_config()
        elif c == "5":
            offline = not offline
            sess.offline = offline
            sess.save_config()
            print(f"  {ok('[OK]')} 已切换为 "
                  f"{'离线（生成占位图，不联网不花钱）' if offline else '在线'}")
        elif c == "6":
            sess.preview = not sess.preview
            sess.save_config()
            print(f"  {ok('[OK]')} 生成后{'会' if sess.preview else '不会'}问你要不要预览")
        elif c == "7":
            # 切换 provider
            providers = settings.provider_choices()
            for i, (k, v) in enumerate(providers, 1):
                mark = " ← 当前" if k == settings.API_PROVIDER else ""
                print(f"    {i}) {v}{mark}")
            pc = ask("选 provider", "1")
            pi = _pick_index(pc, len(providers))
            if pi is None:
                print(dim("  · 无效选择，未更改"))
            else:
                choice, choice_label = providers[pi]
                settings.set_provider(provider=choice)
                print(f"  {ok('[OK]')} 已切换到 {choice_label}")

                # 模型名跟着 provider 走：旧名字在新 provider 下一定无效
                if not settings.model_matches_provider(sess.model):
                    old_model = sess.model
                    sess.model = settings.default_model()
                    print(f"  {dim('·')} 模型名与新 provider 不匹配"
                          f"（{old_model} -> {sess.model}）")
                # 质量档位也要跟着纠正：xhigh/max 在 OpenRouter 上直接 400
                if not settings.quality_supported(sess.quality, sess.model):
                    old_q = sess.quality
                    sess.quality = "low"
                    print(f"  {dim('·')} 质量档位在新 provider/模型下不支持"
                          f"（{old_q} -> {sess.quality}）")
                # 该 provider 的 key 还没配？直接引导
                if not load_key(silent=True):
                    print(f"  {warn('[!]')} {choice_label} 还没配 key")
                    if confirm("现在输入 key？", True):
                        _input_key_for_current_provider(sess)
                sess.save_config()
                # 切换后自动列出可用模型（有 key 才有意义）
                if load_key(silent=True):
                    do_list_models(sess)
        elif c == "8":
            _input_key_for_current_provider(sess)
            sess.save_config()
        elif c == "9":
            print(f"\n  {info('账号可用模型')}")
            do_list_models(sess)
        elif c == "10":
            # 查看润色配置
            print(f"\n  {info('提示词润色配置')}")
            print(f"    {dim('状态')} {'开（每次生成前会问）' if polish.enabled() else '关'}")
            print(f"    {dim('端点')} {polish._endpoint()}")
            print(f"    {dim('模型')} {polish._model()}")
            k = polish._key()
            if k:
                print(f"    {dim('key')} {k[:6]}...{k[-4:] if len(k) > 10 else ''}")
            else:
                print(f"    {dim('key')} {warn('（未配置）')} —— 润色不可用，可用 12) 配置")
            print(dim("    想改：12) 配置润色（也可用环境变量 "
                      "IMGAGENT_POLISH_BASE_URL / _API_KEY / _MODEL）"))
        elif c == "11":
            settings.set_polish(enabled=not polish.enabled())
            print(f"  {ok('[OK]')} 提示词润色已"
                  f"{'开启' if polish.enabled() else '关闭'}")
            return offline
        elif c == "12":
            _configure_polish()
        else:
            sess.offline = offline
            sess.save_config()
            return offline


def do_list_models(sess: Session) -> None:
    """列出**账号实际可用**的模型（真实请求，能暴露 key / 白名单 / 地区限制）。

    注意：旧版在这里递归调用 do_settings()，导致「查模型」变成「进设置」，
    用户看到的就是一个套娃菜单。现在改成**就地**提示输入 key，不跳转。
    """
    from .settings import provider_label
    label = provider_label()
    key = load_key(silent=True)
    if not key:
        print(f"\n  {warn('[未配 key]')} 查模型需要先配置 {label} 的 API key")
        print(f"  {dim('key 会存到：' + str(settings.key_file_for()))}")
        if not confirm("现在输入 key？", True):
            print(dim("  · 已取消"))
            return
        if not _input_key_for_current_provider(sess):
            return
        sess.save_config()
        key = load_key(silent=True)
        if not key:
            print(dim("  · 仍未配置 key，取消查询"))
            return

    print(dim("  正在查询..."))
    try:
        ids = api.list_models(key)
    except ApiError as e:
        api.explain_error(e)
        return
    except Exception as e:                             # noqa: BLE001
        print(f"  {err('[X]')} 查询失败：{type(e).__name__}: {e}")
        return

    choices = settings.model_choices()
    print(f"\n  {info(label)} 可用模型共 {len(ids)} 个：")
    for i, m in enumerate(ids[:40], 1):
        mark = " *" if m in choices else ""
        print(f"   {i:2}) {m}{mark}")
    if len(ids) > 40:
        print(f"    ... 还有 {len(ids) - 40} 个")
    print(f"  {dim('* = 该 provider 预设清单里的')}")
    print(f"  {dim('用「设置 -> 2) 手输模型名」可以指定清单外的任意一个')}")


def do_data_dir() -> None:
    section("数据目录")
    print(f"\n  {info('数据目录')} {settings.HOME}")
    print(f"    {settings.HOME}  <- 图片 + key + 设置 + 历史都在这里")
    print("    文件清单：")
    try:
        entries = sorted(settings.HOME.iterdir())
    except OSError as e:
        print(f"    （读不到：{e}）")
        return
    for f in entries:
        tag = " (隐藏)" if f.name.startswith(".") else ""
        try:
            sz = f.stat().st_size
        except OSError:
            sz = 0
        if f.suffix.lower() in settings.IMG_EXT:
            continue
        print(f"      {f.name:44} {sz:9} B{tag}")
    imgs = [f for f in entries if f.suffix.lower() in settings.IMG_EXT]
    print(f"    图片：{len(imgs)} 张")
    # 显示**真的会用到**的那个（第一个可写的），而不是第一个候选
    # （Termux 下首选 /sdcard/... 往往不可写，显示它会误导用户）
    usable = None
    for d in album_dirs():
        try:
            d.mkdir(parents=True, exist_ok=True)
        except (OSError, PermissionError):
            continue
        usable = d
        break
    if usable is None:
        print(dim("    相册目录：没有可写目录（图片只会留在工作目录）"))
    elif usable == settings.HOME:
        print(f"    相册目录：{dim('无（图片只存工作目录，外部相册不可见）')}")
    else:
        print(f"    相册目录（手机相册/文件管理器可见）：{usable}")
    print("    提示：换手机时把整个目录拷走，历史和设置都在。")


def do_android_status() -> None:
    from .android import is_termux, storage_ready, _which
    if is_termux():
        section("Termux 环境状态")
        print("\n  运行环境：Termux")
        print(f"  外部存储：{'可访问' if storage_ready() else '不可访问'}")
        if not storage_ready():
            print(dim("    修法：在 Termux 里执行一次 termux-setup-storage"))
        print("\n  预览工具：")
        for t, desc in (("termux-open", "调系统看图器"),
                        ("chafa", "终端高清图"),
                        ("timg", "终端高清图"),
                        ("viu", "终端高清图")):
            got = _which(t)
            mark = f"可用 {got}" if got else "未安装"
            print(f"    {t:<12} {mark}   {dim(desc)}")
        if not _which("chafa") and not _which("timg") and not _which("viu"):
            print(dim("\n  装一个终端看图工具体验更好：pkg install chafa"))
        print(f"\n  工作目录：{settings.HOME}")
        try:
            import shutil
            u = shutil.disk_usage(str(settings.HOME))
            print(f"  磁盘剩余：{u.free / 1073741824:.1f} GB / "
                  f"{u.total / 1073741824:.1f} GB")
        except Exception:                                  # noqa: BLE001
            pass
        return
    section("安卓支持状态")
    ready = android.ready()
    print(f"    相册选择器 / 看图器：{android.status()}")
    if not ready:
        print("    -> 想启用：Pydroid 3 菜单 -> Pip -> 搜 pyjnius -> Install")
        print("       （需要先装 Pydroid repository plugin）")
        print("       装好后重跑，选 3 时会自动优先用系统选择器。")
    else:
        print("    -> 选 3 会用系统相册选择器；生成后预览会用系统看图器。")
    print(f"    终端预览：{termimg.describe()}")
    print(f"    内置预览（Tkinter）："
          f"{'可用' if preview.tk_available() else '不可用'}"
          f"   系统看图器：{'可用' if ready else '不可用'}")
    print(f"    工作目录：{settings.HOME}")
    print(f"    脚本目录：{settings.SCRIPT_DIR}")


# ---------------------------------------------------------------- 内部小工具
def _save(data: bytes, media: str, prompt: str, seq: int) -> Path:
    from .store import save_to_album
    return save_to_album(data, media, prompt, seq)


def _ensure_in_home(p: Path, data: bytes) -> Path | None:
    """确保 HOME 里也有这张图（菜单 5 要能列出它）。返回 HOME 里的路径；失败返回 None。

    优先用**硬链接**：同一份数据两个名字，省一次写盘、省一半存储。
    硬链接失败时退回复制。

    ⚠️ 两个已修的坑：
    ① `os.link` 在 **Pydroid 3 里不存在**（不是 OSError 而是 AttributeError），
       旧代码只 catch OSError，异常直接冒泡出去 → 调用方打印"有一张没能保存"。
    ② 旧代码失败时**仍然返回 dest**，但那个文件根本不存在 →
       Item.file 指向不存在的路径 → 下次启动 load() 的 `it.path.exists()` 把它过滤掉
       → 表现就是"图片消失了/当前图还是旧的"。
       现在失败返回 None，调用方据此决定怎么记录（而不是写一条坏记录）。
    """
    if settings.HOME in p.parents:
        return p
    dest = settings.HOME / p.name
    try:
        dest.parent.mkdir(parents=True, exist_ok=True)
        if dest.exists():
            dest.unlink()
        try:
            os.link(p, dest)                 # 硬链接：0 拷贝
        except (OSError, AttributeError):    # OSError=跨文件系统；AttributeError=Pydroid 无此函数
            dest.write_bytes(data)           # 退路：完整复制
    except OSError:
        pass
    # 关键：只有真的落地了才返回路径
    try:
        if dest.exists() and dest.stat().st_size > 0:
            return dest
    except OSError:
        pass
    return None


def _term_cols() -> int:
    """获取终端可用列数（COLUMNS 环境变量优先，否则退到 80）。"""
    try:
        return int(os.environ.get("COLUMNS", "80"))
    except (ValueError, TypeError):
        return 80


def _show_compare(old_item: Item, new_item: Item) -> None:
    """并排打印两张图的终端字符画，自适应终端宽度。

    - 窄屏（≤80列）：单图 22 列，两图并排 + 分隔符 ≈ 46 列，标题居中
    - 标准屏（81–120列）：单图 30 列，≈ 62 列，标题完整显示
    - 宽屏（>120列）：单图 40 列，≈ 83 列

    如果任意一张图读不到，退化为只打新图。
    """
    from . import termimg
    import unicodedata

    def disp_w(t):
        return sum(2 if unicodedata.east_asian_width(c) in ("W", "F") else 1
                   for c in t)

    def render_one(path: Path, cols: int) -> list[str] | None:
        try:
            _ = path.stat()
        except OSError:
            return None
        return termimg.render(path, cols=cols)

    # 计算单图可用列数：总宽 - 两边各 2 空格 - 分隔符 1 列 - 标题预留空间
    term_w = _term_cols()
    # 标题行约 40 字符宽（含文件名），留出间距
    title_w = 44
    avail = max(20, (term_w - title_w - 4) // 2)  # 至少 20 列
    # 取偶数（ASCII 图宽高比要求）
    cols = avail - (avail % 2)

    old_lines = render_one(old_item.path, cols)
    new_lines = render_one(new_item.path, cols)

    old_name = old_item.file[:14] + ("…" if len(old_item.file) > 14 else "")
    new_name = new_item.file[:14] + ("…" if len(new_item.file) > 14 else "")

    max_h = max(len(old_lines or []), len(new_lines or []), 1)
    # 用等宽 ASCII 分隔线（不受 CJK 宽度影响）
    sep_len = cols * 2 + 3   # 左图 cols + │ + 右图 cols
    print()
    print("  " + "─" * sep_len)
    print(f"  原图 ({old_name:<14})  │  改图 ({new_name:<14})")
    print("  " + "─" * sep_len)
    for i in range(max_h):
        o = (old_lines[i] if old_lines and i < len(old_lines) else "")
        n = (new_lines[i] if new_lines and i < len(new_lines) else "")
        print(f"  {o:<{cols}} │ {n}")
    print()


def do_undo(sess: Session) -> None:
    """撤回最近一张图（undo）。

    只 pop 掉最新一条 Item，不删文件（方便需要时手动恢复）。
    历史 0 张或当前图是上传/导入的（kind != gen/edit）时提示用户。
    """
    if not sess.items:
        print("  · 没有可撤回的图")
        return
    top = sess.items[0]
    if top.kind in ("upload", "offline"):
        print(f"  · {top.file} 是导入/占位图，不能撤回（文件仍在磁盘）")
        return
    print(f"  撤回：{top.file}（{top.kind} · ${top.cost:.4f} · {top.prompt[:40]}…）")
    sess.items.pop(0)
    sess.save_state()
    print(f"  {ok('[OK]')} 已撤回。当前图：{sess.current.file if sess.current else '（无）'}")


def _preview_current(sess: Session) -> None:
    cur = sess.current
    if cur:
        preview.show(cur.path, cur.prompt)
