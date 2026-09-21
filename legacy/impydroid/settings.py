"""常量与运行期状态（路径、模型清单、限额）。

设计约定：本模块的变量是**唯一真源**。其他模块一律用
`from . import settings` + `settings.HOME` 这种属性访问，**不要**
`from .settings import HOME` —— 否则运行期修改（测试、--home 参数）
不会传播，那正是上一版踩过的坑。
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
from pathlib import Path

# ---------------------------------------------------------------- 端点
API_BASE = os.environ.get("OPENROUTER_BASE_URL", "https://openrouter.ai/api/v1")
DEFAULT_MODEL = os.environ.get("IMGAGENT_MODEL", "openai/gpt-image-2.5-flare")

# 备选清单：都在你账号的 provider 白名单内
# （google-vertex / nvidia / openai / google-ai-studio）
MODEL_CHOICES = [
    "openai/gpt-image-2.5-flare",
    "openai/gpt-image-2.5-sunburst",
    "openai/gpt-image-2",
    "google/gemini-2.5-flash-image",
    "google/gemini-3.1-flash-image",
    "google/gemini-3.1-flash-lite-image",
    "openai/gpt-image-1-mini",
]

# APIMart 的模型清单（裸模型名，端点是 /images/generations）
# ⚠️ 这些 ID 是 2026-09-18 用真实 key 调 GET /v1/models 实测拿到的，
#    与官网文档里写的名字有出入（例如官方写 wan-2.7-image，实际是 wan2.7-image）。
#    加 "*" 的是当前主推/最常用的。
MODEL_CHOICES_APIMART = [
    # --- OpenAI 系 ---
    "gpt-image-2.5-flare",             # ★ 快，日常
    "gpt-image-2.5-sunburst",          # ★ 慢，编辑精度
    "gpt-image-2.5-ext",
    "gpt-image-2",
    "gpt-image-1.5",
    "gpt-image-1",
    "gpt-image-1-mini",                # 便宜
    "chatgpt-image-latest",
    "dall-e-3",
    # --- Google 系 ---
    "gemini-3.1-flash-image",          # Nano Banana 新一代
    "gemini-3.1-flash-lite-image",
    "gemini-3-pro-image-preview",
    "gemini-2.5-flash-image-preview",
    "imagen-4.0-apimart",
    # --- 国产 ---
    "seedream-5-0-pro",                # 即梦 Seedream 5.0 Pro
    "seedream-5-0-lite",
    "seedream-4-5",
    "seedream-4-0",
    "wan2.7-image",                    # 阿里 Wan 2.7
    "wan2.7-image-pro",
    "qwen-image-3.0-pro",
    "qwen-image-3.0",
    "qwen-image-2.0-pro",
    "qwen-image-2.0",
    "z-image-turbo",
    # --- 其他 ---
    "flux-2-pro",
    "flux-2-max",
    "flux-2-flex",
    "flux-kontext-pro",
    "flux-kontext-max",
    "grok-imagine-image-2.0",
    "grok-imagine-image-quality",
]

# 各 provider 的默认模型（切 provider 时一起切，否则模型名会不匹配）
DEFAULT_MODEL_OPENROUTER = "openai/gpt-image-2.5-flare"
DEFAULT_MODEL_APIMART = "gpt-image-2.5-flare"


def model_choices() -> list[str]:
    """当前 provider 的模型清单。"""
    if API_PROVIDER == "apimart":
        return MODEL_CHOICES_APIMART
    return MODEL_CHOICES


def default_model() -> str:
    """当前 provider 的默认模型。"""
    if API_PROVIDER == "apimart":
        return DEFAULT_MODEL_APIMART
    return DEFAULT_MODEL_OPENROUTER


def model_matches_provider(model: str) -> bool:
    """模型名是否与当前 provider 的命名习惯相符。

    OpenRouter 用 "provider/model"，APIMart 用裸模型名。
    这是判断「切 provider 后模型名失效」的依据。
    """
    if API_PROVIDER == "apimart":
        return "/" not in model
    return "/" in model

# 质量档位 —— 两个 provider 支持的范围**不同**，不能共用一份清单：
#   OpenRouter：auto / low / medium / high        （传 xhigh/max 会 400）
#   APIMart   ：auto / low / medium / high / xhigh / max
#               其中 xhigh/max 只有 gpt-image-2.5 系支持，传给 gpt-image-2 也是 400
QUALITIES_OPENROUTER = ["auto", "low", "medium", "high"]
QUALITIES_APIMART = ["auto", "low", "medium", "high", "xhigh", "max"]
# 兼容旧引用（有些地方还在用 settings.QUALITIES）
QUALITIES = QUALITIES_APIMART

# xhigh/max 只在这些模型上有效（APIMart 文档明确：传给 gpt-image-2 会 400）
_APIMART_HQ_MODEL_PREFIXES = ("gpt-image-2.5",)


def quality_choices(model: str = "") -> list[str]:
    """当前 provider（+ 指定模型）下**可用**的质量档位。

    为什么需要它：把 xhigh 传给 OpenRouter、或把 xhigh 传给 gpt-image-2，
    都会直接 400 报错（文档明确"不会自动降级"）。菜单里不应该给出会报错的选项。
    """
    if API_PROVIDER != "apimart":
        return list(QUALITIES_OPENROUTER)
    if model and not model.startswith(_APIMART_HQ_MODEL_PREFIXES):
        # 非 2.5 系模型：去掉 xhigh/max
        return [q for q in QUALITIES_APIMART if q not in ("xhigh", "max")]
    return list(QUALITIES_APIMART)


def quality_supported(quality: str, model: str = "") -> bool:
    """校验某个质量档位在当前 provider+模型下是否合法。"""
    return quality in quality_choices(model)
ASPECTS = ["1:1", "16:9", "9:16", "3:2", "2:3", "4:3", "3:4", "21:9", "auto"]

# 分辨率档位（^R 循环，与画幅独立）
RESOLUTIONS = ["1k", "2k", "4k"]

# 输出格式（^J 循环）
OUTPUT_FORMATS = ["png", "jpeg", "webp"]

# ---------------------------------------------------------------- 成本估算
# 来源：APIMart 官方文档的「输出 token 参考表」+ 实测校准。
# 计价：图片输出 $30 / 1M tokens ⇒ 每 token $0.00003
# 实测（有账号折扣，约比官方价低 15~20%）：
#   low    → $0.004768（196 tokens）     medium → $0.0106（439 tokens）
# 下面这张表存的是**每张图的 USD 估算**，直接用实测/官方值填，
# 避免"估算 0.02 实际 0.21"这种差 10 倍的情况（旧版就是这样）。
COST_PER_IMAGE = {
    # 1k 分辨率（默认）
    ("1k", "low"): 0.0048,
    ("1k", "medium"): 0.0106,
    ("1k", "high"): 0.0450,
    ("1k", "xhigh"): 0.0800,
    ("1k", "max"): 0.1800,
}
# auto 档：实测 3/3 次都落在 low（output_tokens 恒为 196），
# 说明 APIMart 当前对 gpt-image-2.5-flare 的 auto 策略 = low。
# 所以估算按 low 给（用户看到的是接近真实的数）。
# 注意：**提交时**服务端会按 max 档预留额度（$0.21/张），完成后退回差额 ——
# 这要求余额至少够预扣，余额紧张时建议显式选 low/medium。
COST_AUTO_FALLBACK = 0.0048

# 各分辨率相对 1k 的倍数（实测/官方表：2k≈2x，4k≈4x）
RES_MULTIPLIER = {"1k": 1.0, "2k": 2.0, "4k": 4.0}


def estimate_cost(quality: str, resolution: str = "1k", n: int = 1) -> float:
    """估算 n 张图的花费（USD）。

    注意这是**估算**，不是报价：实际扣费按 token 用量结算，
    且受账号分组倍率/折扣影响（实测比官方价低约 15~20%）。
    """
    # 表里只存 1k 的基准价，其它分辨率按倍数换算
    # （别用 (resolution, quality) 去查表 —— 查不到会落到 fallback，
    #   再乘一遍倍数就成了重复计算，2k 会算成 1k 的 4 倍）
    if quality == "auto":
        base = COST_AUTO_FALLBACK
    else:
        base = COST_PER_IMAGE.get(("1k", quality), 0.02)
    return base * RES_MULTIPLIER.get(resolution, 1.0) * max(1, n)


def estimate_detail(quality: str, resolution: str = "1k",
                    n: int = 1) -> tuple[float, int]:
    """估算 n 张图的 **花费 + 预估 token 数**（tuple）。

    返回 (cost, tokens)：
      - cost 是 USD（与 estimate_cost 一致）
      - tokens 是基于实测的参考值（不同提示词长度不同，这里是按
        标准 1:1 low 档 196 tokens 为基准推的）

    档位间的 token 比例来自官方文档「输出 token 参考表」：
      low=196, medium=439, high=1756, xhigh=3122, max=7024
    auto 实测落在 low，token 取 196。

    分辨率倍数：1k→2k 是 2x，1k→4k 是 4x（官方表数据）。
    总 tokens ≈ base_tokens × res_multiplier × n。
    """
    cost = estimate_cost(quality, resolution, n)
    token_table = {"low": 196, "medium": 439, "high": 1756,
                   "xhigh": 3122, "max": 7024}
    base = token_table.get(quality, token_table["low"])  # auto 也按 low
    tokens = int(base * RES_MULTIPLIER.get(resolution, 1.0) * max(1, n))
    return cost, tokens


# ---------------------------------------------------------------- 环境可调参数
# 任务轮询超时上限（秒）。APIMart 实测 low/1:1 ≈ 13s，high/16:9 ≈ 20s+。
# 超时后抛 ApiError，用户可以调大。默认 300s（5 分钟）已经非常宽松。
APIMART_TIMEOUT: float = float(
    os.environ.get("IMGAGENT_APIMART_TIMEOUT", "300").strip())

# 历史记录裁剪上限：保留最近 N 条，超出就从 JSONL 头部截掉。
# 理由：用户长期用会积累几百上千条历史，每次打开 state.json 都变大。
# 200 条 ≈ 够用很久，且日志文件 < 50KB，读写都很快。
HISTORY_MAX: int = int(os.environ.get("IMGAGENT_HISTORY_MAX", "200"))

# 参考图上传 URL 的 in-memory 缓存条目上限（同一会话内同一文件的 url）
# 防止内存泄漏（正常用户不会同时处理几十万张参考图）。
UPLOAD_CACHE_MAX: int = 256

TIMEOUT = 300.0          # 单次生成超时；high 档 16:9 实测可到 20s+
MAX_ATTEMPTS = 3         # 网络类错误重试次数
BACKOFF_BASE = 1.5
MAX_UPLOAD = 12 * 1024 * 1024    # 参考图上限
MAX_REFS = 16                    # input_references 上限
IMG_EXT = {".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"}

# ---------------------------------------------------------------- 提示词润色
# 一个 OpenAI 兼容的 chat/completions 端点，用来在生成前润色/扩写提示词。
# 端点 / 模型 / key 全部**由用户配置**（不内置任何凭据）：
#   · 端点/模型：环境变量优先，其次数据目录的 config.json
#   · key      ：环境变量 IMGAGENT_POLISH_API_KEY 优先，其次数据目录的 .imgagent_polish_key
import os as _os
POLISH_ENABLED: bool = _os.environ.get("IMGAGENT_POLISH", "1").strip().lower() \
    not in ("0", "off", "no", "false")
POLISH_BASE_URL: str = _os.environ.get("IMGAGENT_POLISH_BASE_URL", "")
POLISH_API_KEY: str = _os.environ.get("IMGAGENT_POLISH_API_KEY", "")
POLISH_MODEL: str = _os.environ.get("IMGAGENT_POLISH_MODEL", "")


# ---------------------------------------------------------------- 生图 API 提供商
# "openrouter" | "apimart"
_PROVIDER: str = os.environ.get("IMGAGENT_API_PROVIDER", "openrouter").strip().lower()
API_KEY: str = os.environ.get("IMGAGENT_API_KEY", "")
API_PROVIDER: str = _PROVIDER

_APIMART_BASE = os.environ.get(
    "IMGAGENT_APIMART_BASE_URL", "https://api.apimart.ai/v1")
_APIMART_KEY: str = os.environ.get("IMGAGENT_APIMART_API_KEY", "")

def set_provider(*, provider: str | None = None,
                 api_key: str | None = None, apimart_key: str | None = None) -> None:
    """运行期改 API 配置。"""
    global API_PROVIDER, API_KEY, _APIMART_KEY
    if provider is not None:
        API_PROVIDER = provider.lower()
    if api_key is not None:
        API_KEY = api_key
    if apimart_key is not None:
        _APIMART_KEY = apimart_key


def key_hint(provider: str | None = None) -> str:
    """该 provider 的 key 长什么样的**格式描述**（不含 provider 名，便于拼句子）。"""
    p = (provider or API_PROVIDER).lower()
    if p == "apimart":
        return "形如 sk-xxxxxxxx"
    return "形如 sk-or-v1-xxxxxxxx"


def looks_like_key(k: str) -> bool:
    """粗略判断 key 格式是否像当前 provider 的。只做提示，不做拦截。"""
    k = k.strip()
    if not k:
        return False
    if API_PROVIDER == "apimart":
        return k.startswith("sk-")
    return k.startswith("sk-or-")


def provider_base() -> str:
    if API_PROVIDER == "apimart":
        return _APIMART_BASE.rstrip("/")
    return API_BASE


def provider_key() -> str:
    if API_PROVIDER == "apimart":
        return _APIMART_KEY or API_KEY
    return API_KEY


# provider 清单（(key, 显示名)）—— 只在这里定义，UI 层都引用它，
# 避免 ui.py 与 curses_ui.py 各写一份导致不一致。
PROVIDERS: list[tuple[str, str]] = [
    ("openrouter", "OpenRouter"),
    ("apimart", "APIMart（API Mart AI）"),
]


def provider_choices() -> list[tuple[str, str]]:
    """返回 provider 清单（供 UI 显示选择列表）。"""
    return list(PROVIDERS)


def provider_label() -> str:
    label = {"openrouter": "OpenRouter", "apimart": "APIMart"}.get(API_PROVIDER, API_PROVIDER)
    return label


def key_file_for(provider: str | None = None) -> Path:
    """当前（或指定）provider 的 key 文件。"""
    p = (provider or API_PROVIDER).lower()
    if p == "apimart":
        return _APIMART_KEY_FILE
    return KEY_FILE


def save_apimart_key(k: str) -> Path | None:
    """保存 APIMart key。返回写入的路径；失败返回 None。"""
    try:
        _APIMART_KEY_FILE.parent.mkdir(parents=True, exist_ok=True)
        tmp = _APIMART_KEY_FILE.with_suffix(".tmp")
        tmp.write_text(k.strip() + "\n", encoding="utf-8")
        os.replace(tmp, _APIMART_KEY_FILE)
        try:
            os.chmod(_APIMART_KEY_FILE, 0o600)
        except OSError:
            pass                                   # FAT/exFAT 上可能不支持
        return _APIMART_KEY_FILE
    except OSError:
        return None


def load_apimart_key() -> str | None:
    """读取已保存的 APIMart key。"""
    try:
        return _APIMART_KEY_FILE.read_text(encoding="utf-8").strip() or None
    except OSError:
        return None


def save_polish_key(k: str) -> Path | None:
    """保存润色用的 key（独立文件，与生图 key 分开）。失败返回 None。"""
    try:
        _POLISH_KEY_FILE.parent.mkdir(parents=True, exist_ok=True)
        tmp = _POLISH_KEY_FILE.with_suffix(".tmp")
        tmp.write_text((k or "").strip() + "\n", encoding="utf-8")
        os.replace(tmp, _POLISH_KEY_FILE)
        try:
            os.chmod(_POLISH_KEY_FILE, 0o600)
        except OSError:
            pass                                   # FAT/exFAT/Windows 上无 POSIX 权限位
        return _POLISH_KEY_FILE
    except OSError:
        return None


def load_polish_key() -> str | None:
    """读取已保存的润色 key（没有则 None）。"""
    try:
        return _POLISH_KEY_FILE.read_text(encoding="utf-8").strip() or None
    except OSError:
        return None


def set_polish(*, enabled: bool | None = None, base_url: str | None = None,
               api_key: str | None = None, model: str | None = None,
               persist: bool = False) -> None:
    """运行期改润色配置（命令行参数 / 设置菜单用）。

    persist=True 时把 base_url/model 写入 config.json，api_key 写入独立 key 文件，
    以便下次启动自动恢复（此前只能靠环境变量，重启就丢）。
    """
    global POLISH_ENABLED, POLISH_BASE_URL, POLISH_API_KEY, POLISH_MODEL
    if enabled is not None:
        POLISH_ENABLED = bool(enabled)
    if base_url is not None:
        POLISH_BASE_URL = base_url
    if api_key is not None:
        POLISH_API_KEY = api_key
    if model is not None:
        POLISH_MODEL = model
    if persist:
        _persist_polish(base_url=base_url, model=model, api_key=api_key)


def _persist_polish(*, base_url: str | None = None, model: str | None = None,
                    api_key: str | None = None) -> None:
    """把润色配置落盘：base_url/model 进 config.json，key 进独立文件。"""
    try:
        import json
        d: dict = {}
        try:
            d = json.loads(CONF_FILE.read_text(encoding="utf-8"))
        except Exception:                          # noqa: BLE001
            d = {}
        if base_url is not None:
            d["polish_base_url"] = base_url
        if model is not None:
            d["polish_model"] = model
        CONF_FILE.parent.mkdir(parents=True, exist_ok=True)
        tmp = CONF_FILE.with_suffix(".tmp")
        tmp.write_text(json.dumps(d, ensure_ascii=False, indent=1), encoding="utf-8")
        os.replace(tmp, CONF_FILE)
    except OSError:
        pass
    if api_key is not None:
        save_polish_key(api_key)


def load_polish_config() -> dict:
    """从 config.json 读取润色配置（base_url / model）。返回 dict（可能为空）。"""
    try:
        import json
        d = json.loads(CONF_FILE.read_text(encoding="utf-8"))
    except Exception:                              # noqa: BLE001
        return {}
    out = {}
    if d.get("polish_base_url"):
        out["base_url"] = d["polish_base_url"]
    if d.get("polish_model"):
        out["model"] = d["polish_model"]
    return out


# ---------------------------------------------------------------- 定位自己
def script_dir() -> Path:
    """找到**包所在目录的上一层**（也就是 run.py 应该在的地方）。

    为什么要试这么多办法：Pydroid 3 用
        exec(open(mainpyfile).read(), __main__.__dict__)
    来跑脚本，这种情况下：
      * 本模块的 __file__ 是正常的（它在文件系统里），但
      * 用户"打开的那个文件"的 __file__ 可能没被设进 __main__，
        而且脚本目录**不会**自动进 sys.path。
    所以这里用尽办法：本模块 __file__ → 父目录 → cwd 逐层向上找 run.py。
    """
    # ① 最可靠：本文件在 <PKG>/settings.py，所以上一层就是包目录
    try:
        here = Path(os.path.abspath(__file__)).resolve()
        if here.name == "settings.py":
            return here.parent.parent            # .../impydroid/ -> ...
        return here.parent
    except NameError:
        pass

    # ② 退路：从 cwd 向上找含 run.py 或 impydroid/ 的目录
    try:
        cwd = Path.cwd().resolve()
    except OSError:
        return Path(".")
    for cand in [cwd, *cwd.parents][:6]:
        if (cand / "run.py").is_file() or (cand / "impydroid").is_dir():
            return cand
    return cwd


def _writable(p: Path) -> bool:
    try:
        p.mkdir(parents=True, exist_ok=True)
        probe = p / ".write_test"
        probe.write_text("x", encoding="utf-8")
        probe.unlink()
        return True
    except OSError:
        return False


def _candidate_homes(sdir: Path, home: Path) -> list[Path]:
    """按平台构造数据目录候选（优先级从高到低）。

    Windows 上 `~` 与 Android 完全不同，且常见情况是脚本目录在只读路径
    （如 Program Files），所以：
      ① IMGAGENT_HOME（显式指定，最高优先）
      ② 脚本同目录 imgagent_data/（项目哲学：数据跟代码一起，迁移最简单）
      ③ %LOCALAPPDATA%/imgagent（Windows 惯例：用户级应用数据）
      ④ 用户目录 imgagent_data/
      ⑤ 临时目录兜底
    Android/Termux 保持原有候选。
    """
    cands: list[Path] = []
    if os.environ.get("IMGAGENT_HOME"):
        cands.append(Path(os.environ["IMGAGENT_HOME"]).expanduser())
    if os.name == "nt":
        local = os.environ.get("LOCALAPPDATA")
        cands += [
            sdir / "imgagent_data",                          # ① 脚本同目录
            (Path(local) / "imgagent") if local else (home / "AppData" / "Local" / "imgagent"),  # ③ Windows 惯例
            home / "imgagent_data",                          # ④ 用户目录
        ]
        return cands
    # Termux/Android/桌面 POSIX：沿用原优先顺序
    cands += [
        sdir / "imgagent_data",                          # ① 脚本同目录的独立子目录
        sdir,                                            # ①' 脚本同目录（首选，简单）
        home / "imgagent_data",                          # ② Termux/桌面的家目录
        home / "storage" / "shared" / "Download" / "imgagent",   # ③ Termux 共享存储
        home / "storage" / "shared" / "Pictures" / "imgagent",
        Path("/sdcard/Download/imgagent"),               # ④ 文件管理器可见
        Path("/storage/emulated/0/Download/imgagent"),
        Path("/sdcard/Pictures/imgagent"),               # ⑤ 相册可见
        Path("/data/user/0/ru.iiec.pydroid3/files/imgagent"),   # ⑥ Pydroid 私有
        Path("/data/data/ru.iiec.pydroid3/files/imgagent"),
    ]
    return cands


def pick_home() -> Path:
    """按优先级挑一个**可写**的数据目录。

    为什么要试这么多次：Pydroid 里 /sdcard 常被 SELinux 挡住，而 Pydroid 自己的
    私有目录一般能写。写死一个路径的话，换台设备就跑不起来。
    Windows 上则优先脚本同目录，其次是 %LOCALAPPDATA%（见 _candidate_homes）。
    """
    sdir = script_dir()
    home = Path.home()
    for c in _candidate_homes(sdir, home):
        try:
            if _writable(c):
                return c
        except Exception:                 # noqa: BLE001
            continue
    import tempfile
    t = Path(tempfile.gettempdir()) / "imgagent"
    t.mkdir(parents=True, exist_ok=True)
    return t


# 运行期状态（可被 tests / --home 修改）
SCRIPT_DIR: Path = script_dir()
HOME: Path = pick_home()
KEY_FILE: Path = HOME / ".imgagent_key"
_APIMART_KEY_FILE: Path = HOME / ".imgagent_apimart_key"
_POLISH_KEY_FILE: Path = HOME / ".imgagent_polish_key"
CONF_FILE: Path = HOME / "config.json"
STATE_FILE: Path = HOME / "state.json"
LOG_FILE: Path = HOME / "history.jsonl"


def set_home(path: str | Path) -> Path:
    """切换数据目录，并同步派生路径。测试与 --home 都用它。"""
    global HOME, KEY_FILE, CONF_FILE, STATE_FILE, LOG_FILE
    global _APIMART_KEY_FILE, _POLISH_KEY_FILE
    HOME = Path(path).expanduser()
    KEY_FILE = HOME / ".imgagent_key"
    _APIMART_KEY_FILE = HOME / ".imgagent_apimart_key"   # 必须一起改，否则 --home 后写错地方
    _POLISH_KEY_FILE = HOME / ".imgagent_polish_key"
    CONF_FILE = HOME / "config.json"
    STATE_FILE = HOME / "state.json"
    LOG_FILE = HOME / "history.jsonl"
    return HOME
