"""本地存储：图片落盘 + 设置 + 历史（全部在脚本同目录）。

文件布局（HOME 由 settings.pick_home() 决定）：
    .imgagent_key     你的 OpenRouter key（chmod 600）
    config.json       设置：模型 / 质量 / 画幅 / 离线 / 自动预览 / 累计花费
    state.json        历史（菜单 4 回退用；只存文件指针，不复制图片）
    history.jsonl     流水账（每次生成一行，含花费）
    *.png/jpg...      所有图片
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

import json
import os
import re
import time
from dataclasses import dataclass, field
from pathlib import Path


from . import settings
from .console import print
from .pngcodec import ext_for



def _atomic_write(path: Path, text: str) -> bool:
    """先写临时文件再 rename —— 断电/崩溃不会留下半个 JSON。"""
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp = path.with_suffix(".tmp")
        tmp.write_text(text, encoding="utf-8")
        os.replace(tmp, path)
        return True
    except OSError as e:
        print(f"  [!] 写入失败 {path.name}：{e}")
        return False


def safe_filename(prefix: str, stem: str, suffix: str, limit: int = 40) -> str:
    """生成安全文件名。三道防线：

      ① 只保留 字母数字/下划线/连字符/点/中文；
      ② 取 basename —— 挡住 "../../etc/passwd" 这类（文件名来自用户输入或相册）；
      ③ 剥掉首尾的点并截断，空则退回 "image"。
    """
    base = re.sub(r"[^\w\u4e00-\u9fff.\-]+", "_", stem, flags=re.UNICODE)
    base = os.path.basename(base).strip("._-")[:limit] or "image"
    suf = suffix if suffix.startswith(".") else "." + suffix
    suf = re.sub(r"[^A-Za-z0-9.]", "", suf)[:8] or ".png"
    return os.path.basename(f"{prefix}_{base}{suf}")


@dataclass
class Item:
    """一张图（AI 生成 / 修改 / 导入）。"""

    file: str                  # 相对 HOME 的文件名
    prompt: str
    kind: str                  # gen | edit | import | offline
    ts: float = field(default_factory=time.time)
    model: str = ""
    quality: str = ""
    cost: float = 0.0
    tokens: int = 0
    note: str = ""

    @property
    def path(self) -> Path:
        return settings.HOME / self.file

    def title(self, i: int) -> str:
        mark = ">" if i == 0 else " "
        p = self.prompt.replace("\n", " ")[:38]
        return f"{mark}[{i}] {self.kind:7} {p}"


class Session:
    """当前图 + 历史栈 + 设置。

    注意：路径一律通过 `settings.XXX` 属性访问（不用 from ... import），
    这样 set_home() / --home 在运行期改目录能立刻生效。
    """

    def __init__(self) -> None:
        self.items: list[Item] = []      # items[0] = 当前
        self.model = settings.DEFAULT_MODEL
        self.quality = "low"
        self.aspect = "1:1"
        self.offline = False             # 离线模式
        self.preview = True              # 生成后自动问"要预览吗"
        self.resolution = "1k"           # 分辨率（1k/2k/4k）
        self.output_format = "png"       # 输出格式（png/jpeg/webp）
        self.batch_n = 1                  # 批量生成张数（1-4）
        self.total_cost = 0.0
        self.counter = 0
        self.load()

    # ------------------------------------------------------------ 读
    def load(self) -> None:
        """先读设置（config.json），再读历史（state.json）。"""
        self._load_files()
        self._sanitize()

    def _sanitize(self) -> None:
        """纠正**配置文件与当前 provider/模型不一致**的字段。

        为什么放在 load 之后统一做：provider 和 model 可能来自不同的地方
        （环境变量 / config.json / state.json），组合起来可能非法。
        典型场景：上次在 APIMart 选了 xhigh，但 config.json 里 provider 写着
        openrouter —— 启动后质量仍是 xhigh，一生成就 400。
        """
        if not settings.quality_supported(self.quality, self.model):
            self.quality = "low"
        if not settings.model_matches_provider(self.model):
            self.model = settings.default_model()
        # 模型切换时质量档可能不合法（如 sunburst 不支持 xhigh/max），降级
        if not settings.quality_supported(self.quality, self.model):
            self.quality = "low"
        if getattr(self, "resolution", "1k") not in settings.RESOLUTIONS:
            self.resolution = "1k"
        if getattr(self, "output_format", "png") not in settings.OUTPUT_FORMATS:
            self.output_format = "png"
        n = getattr(self, "batch_n", 1)
        if not isinstance(n, int) or n < 1 or n > 4:
            self.batch_n = 1

    def _load_files(self) -> None:
        try:
            cfg = json.loads(settings.CONF_FILE.read_text(encoding="utf-8"))
            self.model = cfg.get("model", self.model)
            self.quality = cfg.get("quality", self.quality)
            self.aspect = cfg.get("aspect", self.aspect)
            self.offline = bool(cfg.get("offline", False))
            self.preview = bool(cfg.get("preview", True))
            self.total_cost = float(cfg.get("total_cost", 0.0))
            if "provider" in cfg:
                settings.set_provider(provider=cfg["provider"])
        except Exception:                 # noqa: BLE001
            pass
        try:
            d = json.loads(settings.STATE_FILE.read_text(encoding="utf-8"))
        except Exception:                 # noqa: BLE001
            return
        self.model = d.get("model", self.model)
        self.quality = d.get("quality", self.quality)
        self.aspect = d.get("aspect", self.aspect)
        self.offline = bool(d.get("offline", self.offline))
        self.preview = bool(d.get("preview", self.preview))
        self.total_cost = float(d.get("total_cost", self.total_cost))
        self.counter = int(d.get("counter", 0))
        for raw in d.get("items", []):
            it = Item(file=raw.get("file", ""), prompt=raw.get("prompt", ""),
                      kind=raw.get("kind", "gen"), ts=raw.get("ts", time.time()),
                      model=raw.get("model", ""), quality=raw.get("quality", ""),
                      cost=raw.get("cost", 0.0), tokens=raw.get("tokens", 0),
                      note=raw.get("note", ""))
            if it.file and it.path.exists():
                self.items.append(it)

    # ------------------------------------------------------------ 写
    def save_config(self) -> None:
        d = {"model": self.model, "quality": self.quality, "aspect": self.aspect,
             "offline": self.offline, "preview": self.preview,
             "total_cost": round(self.total_cost, 6),
             "key_saved": settings.key_file_for().exists(),
             "provider": settings.API_PROVIDER}
        _atomic_write(settings.CONF_FILE, json.dumps(d, ensure_ascii=False, indent=1))

    def save_state(self) -> None:
        d = {
            "model": self.model, "quality": self.quality, "aspect": self.aspect,
            "offline": self.offline, "preview": self.preview,
            "total_cost": round(self.total_cost, 6), "counter": self.counter,
            "items": [it.__dict__ for it in self.items[:60]],
        }
        _atomic_write(settings.STATE_FILE, json.dumps(d, ensure_ascii=False, indent=1))
        self.save_config()               # 设置跟着刷一遍，保持两处一致
        # 历史 JSONL 超过阈值时才裁剪（append-only 为主，避免每次 log 都重写文件）
        self.trim_log()

    def push(self, it: Item) -> None:
        self.items.insert(0, it)
        self.counter += 1
        self.save_state()

    def log(self, it: Item) -> None:
        """追加一条历史记录（append-only），只在 JSONL 行数超过阈值时才裁剪。

        为什么这样做：每次 log() 都读全文重写太浪费（虽然 200 行很快，但
        长期跑下来会累积不必要的 IO）。改成"只追加，文件超了才一次性裁"，
        正常路径 O(1)，裁剪路径仍然 O(n) 但频率很低。
        """
        try:
            with settings.LOG_FILE.open("a", encoding="utf-8") as f:
                f.write(json.dumps({**it.__dict__,
                                    "total": round(self.total_cost, 6)},
                                   ensure_ascii=False) + "\n")
        except OSError:
            pass

    def trim_log(self) -> None:
        """裁剪历史 JSONL 文件，只保留最近 HISTORY_MAX 条。

        只在 log() 后定期调用，不在每次 log() 里调（否则变成全量读写）。
        调用方（save_state）在检测到 items 数超出阈值时调用一次即可。
        """
        try:
            lines = settings.LOG_FILE.read_text(encoding="utf-8").splitlines()
            if len(lines) <= settings.HISTORY_MAX:
                return
            # 只保留末尾 N 行，一次性覆盖写
            settings.LOG_FILE.write_text(
                "\n".join(lines[-settings.HISTORY_MAX:]),
                encoding="utf-8")
        except OSError:
            pass

    @property
    def current(self) -> Item | None:
        return self.items[0] if self.items else None


# ---------------------------------------------------------------- 图片落盘
# ---------------------------------------------------------------- 提示词历史
# 为什么单独存一个文件而不复用 history.jsonl：
#   ① history.jsonl 是"每次生成一条"的流水账，同一提示词会重复出现
#   ② 用户真正想要的是"我打过的提示词"，需要**去重**且**最近的在前**
#   ③ 还要能记住"输入了但生成失败"的（这种情况 history.jsonl 里没有）
# 所以单独一个 prompt_history.jsonl：每次提交就追加，读取时按时间倒序去重。
PROMPT_HISTORY_MAX = 200          # 最多保留多少条


def prompt_history_file() -> Path:
    return settings.HOME / "prompt_history.jsonl"


def push_prompt_history(prompt: str, kind: str = "gen") -> None:
    """记录一条用户输入过的提示词（提交时调用，无论成败）。

    append-only，读取时去重。这样"打了一大段结果 401 失败"也不会丢。
    """
    text = (prompt or "").strip()
    if not text:
        return
    try:
        with prompt_history_file().open("a", encoding="utf-8") as f:
            f.write(json.dumps({"ts": time.time(), "kind": kind,
                                "prompt": text}, ensure_ascii=False) + "\n")
    except OSError:
        pass


def load_prompt_history(limit: int = PROMPT_HISTORY_MAX,
                        kind: str = "") -> list[str]:
    """读提示词历史：**最近的在前**，按内容去重（保留最近一次的位置）。

    kind 非空时只返回该类型（gen / edit）。
    """
    path = prompt_history_file()
    if not path.exists():
        return []
    try:
        raw = path.read_text(encoding="utf-8").splitlines()
    except OSError:
        return []
    seen: set[str] = set()
    out: list[str] = []
    for line in reversed(raw):                # 倒序 = 最近的先看
        line = line.strip()
        if not line:
            continue
        try:
            rec = json.loads(line)
        except ValueError:
            continue
        text = (rec.get("prompt") or "").strip()
        if not text or text in seen:
            continue
        if kind and rec.get("kind") != kind:
            continue
        seen.add(text)
        out.append(text)
        if len(out) >= limit:
            break
    return out


def trim_prompt_history(max_lines: int = PROMPT_HISTORY_MAX * 2) -> None:
    """文件过大时裁剪（只留最近的 max_lines 行）。"""
    path = prompt_history_file()
    if not path.exists():
        return
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
        if len(lines) <= max_lines:
            return
        path.write_text("\n".join(lines[-max_lines:]) + "\n", encoding="utf-8")
    except OSError:
        pass


def album_dirs() -> list[Path]:
    """存图目录候选（按优先级，第一个**真正可写**的会被用上）。

    ⚠️ 顺序很重要，而且 **Termux 与 Pydroid 必须分开排**：

    Pydroid：`/sdcard/...` 就是正确路径（申请"文件和媒体"权限后可用）。
    Termux  ：`~/storage/shared/...` 才是指向 `/storage/emulated/0` 的**官方软链**，
              `/sdcard` 在 Termux 里通常**存在但不可写**（SELinux 限制）。
              如果把 `/sdcard` 排在前面，会白试一次；更糟的是某些设备上
              `/sdcard` 恰好可写却指向 App 私有作用域 —— 图片会存进
              一个"相册看不见"的地方，用户以为图丢了。

    所以：Termux 时把 ~/storage/shared 放在最前；其它平台优先 /sdcard。
    最后一个永远是 HOME —— 保证总有一个能写（不会丢图的前提）。
    """
    home = Path.home()
    try:
        from .android import is_termux
        on_termux = is_termux()
    except Exception:                                        # noqa: BLE001
        on_termux = False

    # DCIM/Camera 是系统相册必扫目录，优先存到这里图片一定进相册
    termux_cands = [
        home / "storage" / "shared" / "DCIM" / "Camera" / "imgagent",
        home / "storage" / "shared" / "DCIM" / "imgagent",
        home / "storage" / "shared" / "Pictures" / "imgagent",
        home / "storage" / "shared" / "Download" / "imgagent",
    ]
    android_cands = [
        Path("/sdcard/DCIM/Camera/imgagent"),
        Path("/sdcard/DCIM/imgagent"),
        Path("/sdcard/Pictures/imgagent"),
        Path("/storage/emulated/0/DCIM/Camera/imgagent"),
        Path("/storage/emulated/0/Pictures/imgagent"),
        Path("/sdcard/Download/imgagent"),
    ]
    desktop_cands = [
        home / "Pictures" / "imgagent",                       # 桌面 Linux
        home / "Downloads" / "imgagent",
    ]

    cands = (termux_cands + android_cands + desktop_cands) if on_termux \
        else (android_cands + termux_cands + desktop_cands)

    # Windows：/sdcard 那些候选全部不可用（会白试若干次），
    # 直接把桌面目录（图片/下载）排到最前 —— 用户能直观看到生成的图。
    if os.name == "nt":
        cands = desktop_cands

    cands.append(settings.HOME)          # 兜底：一定可写
    return cands


def save_to_album(data: bytes, media: str, prompt: str, seq: int) -> Path:
    """写进相册可见的目录，文件名带时间戳+提示词摘要（相册里好认）。"""
    name = safe_filename(f"{time.strftime('%m%d_%H%M%S')}_{seq:03d}",
                         prompt, ext_for(media), limit=24)
    for d in album_dirs():
        try:
            d.mkdir(parents=True, exist_ok=True)
            p = d / name
            p.write_bytes(data)
            return p
        except (OSError, PermissionError):
            continue
    p = settings.HOME / name
    p.write_bytes(data)
    return p


# ---------------------------------------------------------------- key
def save_key(k: str) -> str | None:
    """把 key 写进本地文件（权限 600）。写不了就只放内存，不阻断使用。

    按当前 provider 决定写哪个文件，并把 key 同步进 settings 供本次会话使用。
    """
    target = settings.key_file_for()
    if settings.API_PROVIDER == "apimart":
        written = settings.save_apimart_key(k)
        if written is None:
            print("  [!] 无法保存 APIMart key —— 本次仍可用，但下次要重填")
            print("      想持久化：把 key 写进环境变量 IMGAGENT_APIMART_API_KEY")
        settings.set_provider(apimart_key=k)
        return k
    try:
        target.parent.mkdir(parents=True, exist_ok=True)
        tmp = target.with_suffix(".tmp")
        tmp.write_text(k + "\n", encoding="utf-8")
        os.replace(tmp, target)
        try:
            os.chmod(target, 0o600)                # 只有本 App 能读
        except OSError:
            pass                                   # FAT/exFAT 上可能不支持
        print(f"  [OK] key 已保存到 {target.name}（下次启动自动读取）")
        settings.set_provider(api_key=k)
        return k
    except OSError as e:
        print(f"  [!] 无法保存 key（{e}）—— 本次仍可用，但下次要重填")
        print("      想持久化：把 key 写进环境变量 OPENROUTER_API_KEY")
        return k


def load_key(silent: bool = False) -> str | None:
    """key 读取顺序：环境变量 -> 本地文件 -> （可选）交互输入并保存。"""
    # 根据当前 provider 选择 key 来源
    if settings.API_PROVIDER == "apimart":
        k = os.environ.get("IMGAGENT_APIMART_API_KEY")
        if k:
            return k.strip()
        from .settings import load_apimart_key
        k = load_apimart_key()
        if k:
            return k
    else:
        k = os.environ.get("OPENROUTER_API_KEY")
        if k:
            return k.strip()
        if settings.KEY_FILE.exists():
            try:
                k = settings.KEY_FILE.read_text(encoding="utf-8").strip()
            except OSError:
                k = ""
            if k:
                return k
    if silent:
        return None

    from .ui import ask, confirm           # 延迟导入，避免循环依赖
    from .settings import provider_label
    label = provider_label()
    target = settings.key_file_for()
    if settings.API_PROVIDER == "apimart":
        print(f"\n首次使用需要 {label} 的 API key。")
        print("获取：https://apimart.ai （控制台 -> API Keys）")
    else:
        print(f"\n首次使用需要 {label} 的 API key（形如 sk-or-v1-...）。")
        print("获取：https://openrouter.ai/settings/keys")
    print(f"它会持久保存在：{target}")
    env_name = ("IMGAGENT_APIMART_API_KEY" if settings.API_PROVIDER == "apimart"
                else "OPENROUTER_API_KEY")
    print(f"（换 key：删掉那个文件重跑；更安全：改用环境变量 {env_name}）")
    k = ask("粘贴 key").strip()
    if not k:
        print("  没填 key，将只能使用「离线模式」跑流程。")
        return None
    if not settings.looks_like_key(k):
        print(f"  [!] 这看起来不像 {label} 的 key（{settings.key_hint()}）")
        if not confirm("仍然保存？", False):
            return None
    return save_key(k)
