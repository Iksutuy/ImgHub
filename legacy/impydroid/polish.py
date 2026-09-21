"""提示词润色 —— 调用 OpenAI 兼容的 chat/completions 端点。

两种模式（默认 "batch"）：

    batch   一次调用 LLM，让它同时给出 4 种略有差异的润色版本，
            用 "---DIVIDER---" 分隔，返回时拆成 4 条供用户选择。
            优势：一次请求、一次延迟、用户可比较后再选，比反复 Y/n/r 省很多。
    single  原来的单条模式（默认不用，但保留给调试用）。

用法：
    polish.picks(prompt, kind="gen", aspect="")   -> list[str]   (4 条选项)
    polish.run(prompt, kind="gen", aspect="")     -> str         (单条模式)
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
import urllib.error

from . import settings
from .console import dim, err, print
from .httpclient import ApiError, ssl_context

# 默认端点 / model 仅作**建议值**（不含任何 key）。
# ⚠️ 这里**不得**内置 API key —— key 必须由用户自己配置：
#     ① 环境变量 IMGAGENT_POLISH_API_KEY
#     ② 数据目录里的 .imgagent_polish_key 文件（设置菜单里输入）
#    未配置 key 时润色自动关闭（enabled() 返回 False），不会去调任何端点。
_DEFAULT_BASE = "https://api.openai.com/v1"
_DEFAULT_MODEL = "gpt-4o-mini"

TIMEOUT = float(os.environ.get("IMGAGENT_POLISH_TIMEOUT", "120"))
# ⚠️ 实测教训：某些模型是**推理模型**，会先把 token 花在 `reasoning` 字段上。
# 预算 1500 时：reasoning 吃掉 ~1100，content 只剩 15 字符就 finish_reason=length 被截断，
# 表现为"4 条只回来 1 条"或"显示不全"。
# 实测 3000 能稳定拿到 4 条完整结果（completion_tokens 约 275~340）。
MAX_TOKENS = int(os.environ.get("IMGAGENT_POLISH_MAX_TOKENS", "3000"))

# 四个选项之间的约定分隔符（LLM 必须原样输出，解析时按这个切）
DIVIDER = "---DIVIDER---"

# 给 LLM 的系统指令
_SYS_BATCH = (
    "你是一个图像生成提示词专家。用户给一个想法（可能是简短描述，也可能是完整提示词），"
    "你要写出 4 条基于用户原意的、完整可用的中文提示词。\n\n"
    "【核心原则】你的任务是**扩写和优化**用户的原意，不是重新创作：\n"
    "  - 如果用户已经写了很长的详细描述（>30字），**必须保留用户描述的所有关键内容**"
    "    （主体、场景、氛围、风格等），只能在其基础上补充细节（光线、构图、材质、镜头等）。\n"
    "  - 如果用户写得很短（<15字），可以充分发挥补充细节。\n"
    "  - 禁止替换、删除、忽略用户已描述的内容。用户说「丧尸围攻清朝僵尸」，你的结果里必须有丧尸和清朝僵尸。\n\n"
    "直接输出 4 条提示词，中间用下面这行分隔，不要有任何其他文字：\n"
    f"{DIVIDER}\n\n"
    "示例（只看格式，内容另写）：\n"
    "雨夜中孤独灯塔的水彩插画，暖色台灯映在湿漉漉的岩石上，画面柔和安静\n"
    f"{DIVIDER}\n"
    "姜黄色猫咪面部写实特写，翠绿眼睛，浅景深虚化背景，影棚柔光\n"
    f"{DIVIDER}\n"
    "赛博朋克霓虹风格的城市夜景插画，雨夜街道，冷暖对比强烈\n"
    f"{DIVIDER}\n"
    "黎明山脉的极简线条速写，单线连续勾勒，大量留白\n\n"
    "要求：\n"
    "1. 每条提示词直接以内容开头，不要写「提示词1：」「润色结果1」「1.」之类的编号或标签。\n"
    "2. 4 条各用不同风格：写实摄影 / 插画 / 电影感 / 其他，彼此差异明显。\n"
    "3. 每条 50~100 字，说清主体、环境、光线、风格、构图，可直接生图。\n"
    "4. **全部用中文**。\n"
    "5. 只输出这 4 条和 3 个分隔符，其他什么都不要。\n"
)

_SYS_EDIT_BATCH = (
    "你是一个图像编辑提示词专家。用户有一张照片要修改，你要写出 4 条完整可用的中文编辑指令。\n\n"
    "【核心原则】编辑指令必须**保留原图的核心内容**：\n"
    "  - 用户说「丧尸围攻清朝僵尸」，编辑结果里丧尸和清朝僵尸必须还在。\n"
    "  - 用户说「把背景换成雪原」，只能改背景，不能换主体、不能加新人物。\n"
    "  - 如果当前图的提示词里描述了特定主体（如「橘猫」「清朝僵尸」），"
    "编辑指令必须明确保留该主体。\n\n"
    "直接输出 4 条，中间用下面这行分隔，不要有任何其他文字：\n"
    f"{DIVIDER}\n\n"
    "示例（只看格式，内容另写）：\n"
    "保持主体外观和姿势不变，把背景换成冬季雪原，冷调光线，地面覆盖积雪\n"
    f"{DIVIDER}\n"
    "保留主体和构图，改为黄昏逆光，暖橙色调，加长影子，空气中有金色微尘\n"
    f"{DIVIDER}\n"
    "主体不变，替换为柔和米白色影棚背景，加自然投影，商业产品摄影风格\n"
    f"{DIVIDER}\n"
    "保留主体，整体转为黑白胶片质感，提高对比度，加轻微颗粒\n\n"
    "要求：\n"
    "1. 每条直接以内容开头，不要写编号或标签。\n"
    "2. 4 条各用不同方向：季节 / 光线 / 背景 / 风格 / 天气等。\n"
    "3. 每条都要明确「保持不变的是什么」和「要改的是什么」。\n"
    "4. 不要添加新的主体。\n"
    "5. **全部用中文**。40~70 字一条。只输出这 4 条和 3 个分隔符。\n"
)

_SYS_SINGLE_GEN = (
    "You are an expert prompt engineer for text-to-image models.\n"
    "Rewrite the user's idea into ONE high-quality image prompt.\n"
    "Output ONLY the final prompt text. No explanations, no quotes, no markdown."
)

_SYS_SINGLE_EDIT = (
    "You are an expert prompt engineer for IMAGE-EDITING models.\n"
    "Rewrite the user's instruction into ONE precise editing prompt.\n"
    "Output ONLY the instruction text. No explanations, no quotes, no markdown."
)


def _base_url() -> str:
    return (settings.POLISH_BASE_URL or _DEFAULT_BASE).rstrip("/")


def _model() -> str:
    """润色使用的模型名（用户配置优先，否则建议值）。"""
    return settings.POLISH_MODEL or _DEFAULT_MODEL


def _endpoint() -> str:
    b = _base_url()
    if b.endswith("/chat/completions"):
        return b
    return b + "/chat/completions"


def _key() -> str:
    """润色用的 key。

    三种来源，按优先级：运行期/环境变量（settings.POLISH_API_KEY）
    → 数据目录里的 key 文件 → 空字符串（表示未配置）。
    **绝不返回内置 key** —— 分发版不含任何凭据。
    """
    k = settings.POLISH_API_KEY
    if k:
        return k
    loader = getattr(settings, "load_polish_key", None)
    if loader is not None:
        try:
            return loader() or ""
        except Exception:                                # noqa: BLE001
            return ""
    return ""


def configured() -> bool:
    """润色是否**已配置可用**：有 key、有端点、有模型。"""
    return bool(_key()) and bool(_base_url()) and bool(_model())


def enabled() -> bool:
    """润色是否启用。必须同时满足：开关开着 + 已配置（有 key）。"""
    return settings.POLISH_ENABLED and configured()


# ---------------------------------------------------------------- 核心 API
def _call(messages: list[dict], *, temperature: float = 0.7,
          max_tokens: int | None = None) -> dict:
    """发一次 POST，返回整个 response JSON。失败抛 ApiError。"""
    if not _key():
        # 未配置 key：绝不发请求（也避免把空 Bearer 打出去）
        raise ApiError("润色未配置：请在 设置 → 配置润色 里填端点/模型/key，"
                       "或设环境变量 IMGAGENT_POLISH_API_KEY")
    payload = {
        "model": _model(),
        "messages": messages,
        "temperature": temperature,
        "max_tokens": max_tokens or MAX_TOKENS,
        "stream": False,
    }
    req = urllib.request.Request(
        _endpoint(),
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        method="POST",
        headers={
            "Authorization": f"Bearer {_key()}",
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT, context=ssl_context()) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raise ApiError(f"HTTP {e.code}: {e.read().decode('utf-8', 'replace')[:300]}", e.code) from None
    except urllib.error.URLError as e:
        raise ApiError(f"网络错误：{e.reason}") from None
    except TimeoutError:
        raise ApiError(f"超时（>{TIMEOUT:.0f}s）") from None
    except Exception as e:                           # noqa: BLE001
        raise ApiError(f"{type(e).__name__}: {e}") from None


def _pick_text(resp: dict) -> str:
    choices = resp.get("choices") or []
    if not choices:
        raise ApiError(f"空 choices：{json.dumps(resp)[:200]}")
    ch = choices[0]
    msg = ch.get("message") or {}
    finish = (ch.get("finish_reason") or "").lower()
    text = ""
    for field in ("content", "reasoning_content", "reasoning", "text"):
        v = msg.get(field)
        if isinstance(v, str) and v.strip():
            text = v.strip()
            break
    if not text:
        v = ch.get("text") or ""
        if isinstance(v, str) and v.strip():
            text = v.strip()
    if not text:
        if finish == "length":
            raise ApiError(
                "模型把 token 全用在思考上了，正文没写出来。"
                "请调大 IMGAGENT_POLISH_MAX_TOKENS（当前=" + str(MAX_TOKENS) + ")")
        raise ApiError(f"模型返回空内容（finish_reason={finish or '?'}）")
    return _clean(text)


# 模型可能自作主张加的标签前缀（中文/英文），统一剥掉
_LABEL_RE = re.compile(
    r"^\s*(?:\[|【)?\s*"
    r"(?:润色结果|编辑方向|提示词|版本|方案|风格|选项|prompt|option|variant|version|result)"
    r"\s*[A-Za-z0-9一二三四五]*\s*(?:\]|】)?\s*[:：.、\)\-—–]?\s*",
    re.IGNORECASE)
# 纯编号前缀："1. " / "1) " / "(1) " / "第1条："
_NUM_RE = re.compile(r"^\s*(?:\(|（|\[)?\s*(?:第)?\s*[1-9一二三四五]\s*(?:\)|）|\]|\.|、|:|：)?\s+")


def _strip_label(line: str) -> str:
    """剥掉行首的编号/标签（模型经常无视指令照抄示例里的标签）。"""
    out = line.strip()
    for _ in range(3):                    # 最多剥三层，防止 "1. 版本1：xxx"
        before = out
        out = _LABEL_RE.sub("", out).strip()
        out = _NUM_RE.sub("", out).strip()
        if out == before:
            break
    return out


def _clean(text: str) -> str:
    t = text.strip()
    if t.startswith("```"):
        lines = t.split("\n")
        if len(lines) >= 2:
            lines = lines[1:]
        if lines and lines[-1].strip().startswith("```"):
            lines = lines[:-1]
        t = "\n".join(lines).strip()
    for pre in ("Here is the prompt:", "Here is the final prompt:", "Prompt:",
                "Here is the rewritten prompt:", "Sure, here is", "Sure! Here is",
                "润色后的提示词：", "提示词："):
        if t.lower().startswith(pre.lower()):
            t = t[len(pre):].strip()
    if len(t) >= 2 and t[0] in "\"'“”" and t[-1] in "\"'“”":
        t = t[1:-1].strip()
    t = _strip_label(t)                   # 剥掉 "润色结果1：" 这类前缀
    return " ".join(t.split())


# ---------------------------------------------------------------- 公开接口
def picks(prompt: str, *, kind: str = "gen", aspect: str = "",
          subject: str = "") -> list[str]:
    """一次调用返回 **4 条**润色选项（可能少于 4，取决于 LLM 响应）。"""
    sys_text = (_SYS_EDIT_BATCH if kind == "edit" else _SYS_BATCH).format(DIVIDER=DIVIDER)
    user_text = build_user_text(prompt, kind, aspect, subject)
    resp = _call([{"role": "system", "content": sys_text},
                  {"role": "user", "content": user_text}],
                 temperature=0.9)                   # 高 temperature 增加多样性
    raw = _pick_text(resp)
    # 模型有时会无视指令，照抄示例里的 [润色结果 N] 标签 —— 这里逐条再剥一次
    out = []
    for opt in raw.split(DIVIDER):
        cleaned = _strip_label(opt)
        if cleaned:
            out.append(cleaned)
    return out                            # 去掉空段


def run(prompt: str, *, kind: str = "gen", aspect: str = "") -> str:
    """单条模式（原有行为，用于回退）。"""
    sys_text = (_SYS_SINGLE_EDIT if kind == "edit" else _SYS_SINGLE_GEN)
    resp = _call([{"role": "system", "content": sys_text},
                  {"role": "user", "content": build_user_text(prompt, kind, aspect)}])
    return _pick_text(resp)


def build_user_text(prompt: str, kind: str, aspect: str = "",
                    subject: str = "") -> str:
    """构造 user 消息。

    `subject` 是编辑模式下的**画面主体描述**（来自上一张图的提示词）。
    为什么需要它：不给上下文时，模型看到「把背景换成雪原，主体不变」会自己
    脑补主体 —— 实测会写出「保持**人物**主体和姿势不变」，但用户图里是一只猫。
    这就是「牛头不对马嘴」的根因。
    """
    if kind == "edit":
        ctx = ""
        if subject:
            ctx = ("【当前照片的内容（来自生成该图时的提示词，供你判断主体是什么）】\n"
                   f"{subject}\n\n")
        return (f"{ctx}"
                f"用户的编辑要求：\n{prompt}\n\n"
                f"生成 4 个不同方向的中文编辑指令。\n"
                f"重要：主体到底是什么请依据上面给出的照片内容判断"
                f"（可能是猫、狗、人物、商品、风景…），不要凭空假设成「人物」。\n"
                f"每条要明确：保持不变的（主体外观/姿势/构图）与要修改的。")
    extra = f"\n目标画幅：{aspect}。" if aspect else ""
    return (f"用户想画的图（简短描述）：\n"
            f"{prompt}{extra}\n\n"
            f"生成 4 个不同风格的中文提示词（每条要详细、可直接用于生图）。")


def describe() -> str:
    """一行描述润色配置（给环境信息 / 设置面板用）。"""
    if not _key():
        return "未配置（设置里填端点/模型/key 后可用）"
    return f"{_model()} @ {_endpoint()}"


def explain_error(e: Exception) -> None:
    msg = str(e)
    low = msg.lower()
    status = getattr(e, "status", None)
    print(f"  {err('[润色失败]')} {msg[:200]}")
    if status == 401 or "unauthorized" in low or "invalid" in low and "key" in low:
        print(dim("    -> key 无效或过期。在设置里换，或用环境变量 IMGAGENT_POLISH_API_KEY=..."))
    elif status == 404:
        print(dim("    -> 端点或模型名不对。检查设置里的 base_url 与 model。"))
    elif status == 429:
        print(dim("    -> 触发限流，稍后再试。"))
    elif "timed out" in low or "超时" in msg:
        print(dim("    -> 超时。可增大 IMGAGENT_POLISH_TIMEOUT（秒）和 "
                  "IMGAGENT_POLISH_MAX_TOKENS。"))
    else:
        print(dim("    -> 可以先不润色直接生成（选 n），不影响主流程。"))
