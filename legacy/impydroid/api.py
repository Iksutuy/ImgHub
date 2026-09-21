"""OpenRouter Images API 封装 —— 生成 / 修改 / 查模型 / 查 key。

多步修改的原理：`/api/v1/images` 是**无状态**的，它不记得你上一张画了什么。
所以基于上一张图修改，必须把图编码成 data URL 塞进 `input_references`。
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

import base64
import hashlib
import json
import os
import random
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field


from . import settings
from .settings import provider_base, provider_key
from .console import print
from .httpclient import (ApiError, err_from_body, get_json, post_json,
                         ssl_context)
from .placeholder import placeholder_png



@dataclass
class GenResult:
    images: list[tuple[bytes, str]]        # (字节, media_type)
    cost: float = 0.0
    tokens: int = 0
    raw_usage: dict = field(default_factory=dict)


def _apimart_resolution(aspect: str) -> str:
    """APIMart 的 resolution 参数。

    ⚠️ 旧版按画幅"智能"提到 2k —— 那是个坏设计：
    16:9 会悄悄变成 2k，**成本翻倍而用户完全不知情**（1k low $0.0048 vs 2k low $0.0119）。
    现在固定用 1k，让花费可预期。要 2k/4k 请走显式配置
    （环境变量 IMGAGENT_APIMART_RESOLUTION）。
    """
    return os.environ.get("IMGAGENT_APIMART_RESOLUTION", "1k").strip().lower()


def _build_payload(prompt: str, model: str, quality: str, aspect: str,
                   n: int, refs: list[tuple[bytes, str]] | None,
                   resolution: str = "1k",
                   output_format: str = "png") -> tuple[str, dict]:
    """按当前 provider 构造请求体。返回 (endpoint_path, payload)。"""
    # APIMart 单次最多生成 4 张；OpenRouter 最多 10 张
    max_n = 4 if settings.API_PROVIDER == "apimart" else 10
    n = max(1, min(max_n, n))
    if settings.API_PROVIDER == "apimart":
        # APIMart: /v1/images/generations
        # - size 替代 aspect_ratio
        # - resolution: 1k/2k/4k（大画幅自动 2k）
        # - image_urls: 公网 HTTP(S) URL（本地图需先上传）
        payload: dict = {"model": model, "prompt": prompt,
                         "quality": quality, "size": aspect, "n": n}
        if resolution and resolution != "1k":
            payload["resolution"] = resolution
        payload["output_format"] = output_format
        # 注意：APIMart 只接受公网 URL，不接受 base64 data URL。
        # 参考图的上传在 _generate_apimart() 里做（先 POST /uploads/images 拿 URL）。
        # 所以这里**不**往 payload 里塞 refs，避免塞了不该塞的字段。
        return "/images/generations", payload
    else:
        # OpenRouter: /api/v1/images
        payload = {"model": model, "prompt": prompt,
                   "quality": quality, "aspect_ratio": aspect, "n": n}
        if refs:
            payload["input_references"] = [
                {"type": "image_url",
                 "image_url": {"url": f"data:{media};base64,"
                                      + base64.b64encode(b).decode("ascii")}}
                for b, media in refs[:settings.MAX_REFS]
            ]
        return "/images", payload


def _parse_openrouter_response(data: dict) -> GenResult:
    """解析 OpenRouter 同步响应。"""
    items = data.get("data") or []
    out: list[tuple[bytes, str]] = []
    for item in items:
        b64 = item.get("b64_json")
        if not b64:
            continue
        out.append((base64.b64decode(b64), item.get("media_type", "image/png")))
    if not out:
        raise ApiError(f"响应里没有图像数据：{json.dumps(data)[:200]}")
    usage = data.get("usage") or {}
    return GenResult(out,
                     cost=float(usage.get("cost") or 0.0),
                     tokens=int(usage.get("total_tokens") or 0),
                     raw_usage=usage)


def _http_error_to_api(e: "urllib.error.HTTPError") -> "ApiError":
    """把裸 HTTPError 转成带可读信息的 ApiError。

    ⚠️ 为什么必须做这个转换（真机 bug，2026-09-19）：
      APIMart 的提交端点返回 403 + body {"error":{"message":
      "insufficient balance: insufficient quota: ...", "type":"quota_not_enough"}}
      —— 这是**余额不足**，不是权限问题。
      但旧代码没包 try/except HTTPError，裸异常冒泡到 UI，
      用户只看到 "HTTPError HTTP Error 403: Forbidden"，
      完全丢失了 body 里的真实原因（quota_not_enough）。

      另外 APIMart 对"余额不足"用**两种状态码**（不同网关）：
        · 402 → "[token_id=x] insufficient balance (current: ..., required: ...)"
        · 403 → "insufficient balance: insufficient quota: balance=..., required=..."
      所以不能只按状态码判断，必须看 body。
    """
    # ⚠️ HTTPError 的 body 是**流**，只能读一次。第二次 read() 返回空，
    #    导致同样的异常第二次转换时丢失真实原因（测试中踩过）。
    #    所以把读到的 body 缓存在异常对象上，重复调用也安全。
    raw = getattr(e, "_cached_body", None)
    if raw is None:
        try:
            raw = e.read()
        except Exception:                                # noqa: BLE001
            raw = b""
        try:
            e._cached_body = raw                         # 缓存供重复调用
        except Exception:                                # noqa: BLE001
            pass
    msg, code = err_from_body(raw) if raw else (str(e), None)
    tail = f"/{code}" if code not in (None, "") else ""
    return ApiError(f"{msg} (HTTP {e.code}{tail})", e.code)


def _download_image(url: str, api_key: str, timeout: float = 60) -> bytes:
    """从 URL 下载图片二进制数据。"""
    req = urllib.request.Request(url, headers={
        "Authorization": f"Bearer {api_key}",
        "Accept": "image/*",
    })
    try:
        with urllib.request.urlopen(req, timeout=timeout,
                                    context=ssl_context()) as r:
            return r.read()
    except urllib.error.HTTPError as e:
        raise _http_error_to_api(e) from None


def poll_task(task_id: str, *, api_key: str,
              max_wait: float = 180.0, poll_interval: float = 3.0) -> dict:
    """轮询 APIMart 任务状态，直到 completed/failed 或超时。

    返回完整的任务响应 dict（含 result.images、cost、usage 等）。
    """
    from .settings import provider_base
    base = provider_base()
    url = f"{base}/tasks/{task_id}"
    elapsed = 0.0
    while elapsed < max_wait:
        req = urllib.request.Request(url, headers={
            "Authorization": f"Bearer {api_key}",
            "Accept": "application/json",
        })
        try:
            with urllib.request.urlopen(req, timeout=30,
                                        context=ssl_context()) as r:
                resp = json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            raise _http_error_to_api(e) from None
        if resp.get("code") != 200:
            err = resp.get("error", {})
            raise ApiError(f"查询任务失败：{err.get('message', resp)}")
        data = resp.get("data", {})
        status = data.get("status", "")
        if status == "completed":
            return data
        if status == "failed":
            err_msg = data.get("error", {}).get("message", "未知错误")
            raise ApiError(f"任务失败 [{task_id}]：{err_msg}")
        # submitted / processing → 继续轮询
        # 每 ~10 秒打一行进度提示，让用户知道程序没卡死
        # （尤其 high/xhigh/max 档，生成可能要 30s+）
        if elapsed > 0 and int(elapsed) % 10 == 0 and int(elapsed) <= max_wait * 0.9:
            from .console import dim
            print(dim(f"  · 还在生成中（已等 {int(elapsed)}s…）"))
        time.sleep(poll_interval + random.uniform(0, 0.5))
        elapsed += poll_interval + 0.5
    raise ApiError(f"任务超时（>{max_wait:.0f}s）：{task_id}")


def generate(prompt: str, *, api_key: str, model: str, quality: str, aspect: str,
             n: int = 1, refs: list[tuple[bytes, str]] | None = None,
             offline: bool = False, step: int = 0,
             resolution: str = "1k",
             output_format: str = "png") -> GenResult:
    """生成或修改一张图（支持 OpenRouter 同步 / APIMart 异步）。"""
    if offline:
        time.sleep(0.4)
        return GenResult([(placeholder_png(prompt, step=step), "image/png")])

    key = api_key or provider_key()
    if settings.API_PROVIDER == "apimart":
        return _generate_apimart(prompt, model, quality, aspect, n, refs, key,
                                 resolution=resolution,
                                 output_format=output_format)
    else:
        endpoint, payload = _build_payload(prompt, model, quality, aspect, n, refs,
                                           resolution=resolution,
                                           output_format=output_format)
        base = provider_base()
        data = post_json(f"{base}{endpoint}", payload, key)
        return _parse_openrouter_response(data)



# ---------------------------------------------------------------- 参考图 URL 缓存
# 同一会话内，相同图片只上传一次。Key = SHA-1 of (data[:512] + data[-512:]),
# 头尾各 512 字节拼一起 hash，足以区分不同图片，又不需要 hash 全文件。
# 用普通 dict + 手动 evict 头（FIFO），避免 functools.cache 在 Pydroid 里
# 碰到的奇怪行为（某些 Pydroid 版本 functools 模块缺失）。
_upload_cache: dict[str, str] = {}   # hash -> url
_UPLOAD_CACHE_MAX = 256              # settings.UPLOAD_CACHE_MAX 运行时覆盖


def _upload_key(data: bytes) -> str:
    """图片的轻量哈希：头尾各 512 字节拼一起 hash。"""
    return hashlib.sha1(data[:512] + data[-512:]).hexdigest()


def _upload_cache_get(key: str) -> str | None:
    return _upload_cache.get(key)


def _upload_cache_set(key: str, url: str) -> None:
    if len(_upload_cache) >= _UPLOAD_CACHE_MAX:
        _upload_cache.popitem(last=False)   # FIFO：删最早的一条
    _upload_cache[key] = url


def _ext_for_media(media: str) -> str:
    """media_type -> 文件扩展名（上传时 filename 要用）。"""
    return {"image/jpeg": "jpg", "image/webp": "webp",
            "image/gif": "gif", "image/bmp": "bmp"}.get(media, "png")


def upload_image(data: bytes, media: str, api_key: str) -> str:
    """把本地图片上传到 APIMart，返回**公网可访问的 URL**。

    ⚠️ 实测教训：这个端点**只接受 multipart/form-data**。
    传 JSON（无论 base64 还是 data URL）都会 400：
        "missing or invalid file field: request Content-Type isn't multipart/form-data"
    成功响应是**扁平结构**（没有 data 包裹）：
        {"bytes":..., "content_type":"image/png", "url":"https://...", ...}

    重试策略：网络类错误最多重试 3 次（指数退避），客户端错误直接抛。
    命中缓存时直接返回（避免重复上传同一张图浪费流量和时间）。
    """
    from .httpclient import ssl_context

    # 查缓存：同一张图片（头尾 hash 相同）在本会话内不重复上传
    cache_key = _upload_key(data)
    cached = _upload_cache_get(cache_key)
    if cached is not None:
        return cached

    base = provider_base()
    boundary = "----impydroid" + str(int(time.time() * 1000))
    fname = "ref." + _ext_for_media(media)
    head = (f"--{boundary}\r\n"
            f'Content-Disposition: form-data; name="file"; filename="{fname}"\r\n'
            f"Content-Type: {media}\r\n\r\n").encode("utf-8")
    body = head + data + f"\r\n--{boundary}--\r\n".encode("utf-8")

    # 重试 3 次（指数退避 1s / 2s / 4s）
    last_err: Exception | None = None
    for attempt in range(3):
        try:
            req = urllib.request.Request(
                f"{base}/uploads/images",
                data=body, method="POST",
                headers={"Authorization": f"Bearer {api_key}",
                         "Content-Type": f"multipart/form-data; boundary={boundary}",
                         "Accept": "application/json"})
            with urllib.request.urlopen(req, timeout=120,
                                        context=ssl_context()) as r:
                resp = json.loads(r.read().decode("utf-8"))
            # 响应可能被 data 包裹，也可能扁平 —— 两种都认
            if isinstance(resp, dict):
                url = resp.get("url")
                if not url:
                    inner = resp.get("data")
                    if isinstance(inner, dict):
                        url = inner.get("url")
                    elif isinstance(inner, list) and inner:
                        url = (inner[0] or {}).get("url")
                if url:
                    _upload_cache_set(cache_key, str(url))
                    return str(url)
            raise ApiError(f"上传成功但响应里没有 url：{json.dumps(resp)[:200]}")
        except urllib.error.HTTPError as e:
            last_err = ApiError(
                f"参考图上传失败（HTTP {e.code}）：{err_from_body(e.read())[0]}",
                e.code)
            if 400 <= e.code < 500:   # 客户端错误（key 错、格式错）不重试
                break
        except (OSError, TimeoutError) as e:
            last_err = e
            if attempt < 2:
                time.sleep(1 << attempt)           # 1s / 2s 退避
            continue
        else:
            pass

    if last_err is not None:
        raise last_err
    raise ApiError("参考图上传失败：未知错误（重试 3 次后仍失败）")


def _generate_apimart(prompt: str, model: str, quality: str, aspect: str,
                      n: int, refs: list[tuple[bytes, str]] | None,
                      api_key: str,
                      resolution: str = "1k",
                      output_format: str = "png") -> GenResult:
    """APIMart 异步生成流程：提交 → 轮询 → 下载 → 组装 GenResult。"""
    from .settings import provider_base
    from .httpclient import ssl_context
    from .console import dim, ok, warn

    base = provider_base()

    # 1. 上传参考图（如果有），拿到公网 URL
    #
    # ⚠️ 这里**不能**静默吞掉错误。上一版把所有异常都 pass 掉，结果是：
    #    上传 400 失败 → image_urls 为空 → 任务变成"纯文生图"，
    #    用户以为在改自己的图，其实生成了一张全新的图。这是个静默的数据正确性 bug。
    #    现在：只要用户给了参考图，上传失败就**明确报错**，不偷偷降级。
    image_urls: list[str] = []
    if refs:
        for idx, (b, media) in enumerate(refs[:settings.MAX_REFS], 1):
            print(dim(f"  · 上传参考图 {idx}/{len(refs)}（{len(b) // 1024}KB）…"))
            url = upload_image(b, media, api_key)     # 失败会抛 ApiError
            image_urls.append(url)
        print(f"  {ok('[OK]')} 参考图已上传 {len(image_urls)} 张")

    # 2. 提交生成任务
    payload: dict = {
        "model": model,
        "prompt": prompt,
        "quality": quality,
        "size": aspect,
        "n": max(1, min(4, n)),
    }
    if resolution and resolution != "1k":
        payload["resolution"] = resolution
    if output_format:
        payload["output_format"] = output_format
    if image_urls:
        payload["image_urls"] = image_urls[:16]

    submit_req = urllib.request.Request(
        f"{base}/images/generations",
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        method="POST",
        headers={"Authorization": f"Bearer {api_key}",
                 "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(submit_req, timeout=60,
                                    context=ssl_context()) as r:
            submit_resp = json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        # ⚠️ 真机 bug（2026-09-19）：这里原来是裸 urlopen。
        #    余额不足时 APIMart 返回 403 + body 里有 quota_not_enough，
        #    裸异常冒泡到 UI 后只剩 "HTTP Error 403: Forbidden"，
        #    用户误以为是权限问题。现在转成可读的 ApiError。
        raise _http_error_to_api(e) from None
    if submit_resp.get("code") != 200:
        err = submit_resp.get("error", {})
        raise ApiError(f"提交失败：{err.get('message', submit_resp)}")
    tasks = submit_resp.get("data", [])
    if not tasks:
        raise ApiError(f"提交成功但没有 task_id：{json.dumps(submit_resp)[:200]}")

    # 3. 并发轮询所有任务（APIMart 提交后每个 task_id 独立，可并行等）
    #    用 concurrency.gather 让 n 张图的轮询同时跑，总时间 ≈ 最慢的那张
    #    而不是串行累加（旧版 n=4 时可能要等 4×15s=60s，改后约 15s）
    from . import concurrency as _cg

    def _poll_one(task_info: dict):
        """轮询一个任务 + 下载图片，返回 (cost, tokens, [imgs]) 或抛异常。"""
        task_id = task_info.get("task_id", "")
        if not task_id:
            raise ValueError("missing task_id")
        td = poll_task(task_id, api_key=api_key,
                       max_wait=settings.APIMART_TIMEOUT)
        results: list[tuple[bytes, str]] = []
        cost = float(td.get("cost") or 0.0)
        usage = td.get("usage") or {}
        images_result = (td.get("result") or {}).get("images", [])
        for img_entry in images_result:
            urls = img_entry.get("url", [])
            if not urls:
                continue
            img_url = urls[0] if isinstance(urls, list) else urls
            try:
                img_bytes = _download_image(img_url, api_key, timeout=60)
                media = "image/png"
                ct = img_entry.get("content_type", "")
                if "jpeg" in ct or "jpg" in ct:
                    media = "image/jpeg"
                elif "webp" in ct:
                    media = "image/webp"
                results.append((img_bytes, media))
            except Exception:                          # noqa: BLE001
                pass
        return cost, usage.get("total_tokens", 0), results

    print(dim(f"  · 提交 {len(tasks)} 个任务，开始并行轮询…"))
    gathered = _cg.gather(_poll_one, tasks)
    out: list[tuple[bytes, str]] = []
    total_cost = 0.0
    total_tokens = 0
    failed_tasks = 0
    for r in gathered:
        if isinstance(r, BaseException):
            failed_tasks += 1
            print(f"  {warn('[X]')} 一个任务失败：{r}")
            continue
        cost, tokens, results = r
        total_cost += cost
        total_tokens += tokens
        out.extend(results)

    if not out:
        raise ApiError(f"所有任务都没有产出图片（tasks={len(tasks)}，失败={failed_tasks}）")
    return GenResult(out, cost=total_cost, tokens=total_tokens,
                     raw_usage={"total_tokens": total_tokens})


def list_models(api_key: str | None = None, *, images_only: bool = True) -> list[str]:
    """当前账号可用的模型。

    OpenRouter：`/images/models`（生图专用，无需 key）。
    APIMart：`/models`（通用清单，需要 key）。实测 `/v1/images/models` 是 404。
    APIMart 加 `?expand=1` 能拿到 category，用它筛出生图模型。
    """
    base = provider_base()
    if settings.API_PROVIDER == "apimart":
        d = get_json(f"{base}/models?expand=1", api_key or "")
        items = d.get("data") or []
        if images_only:
            filtered = [m for m in items
                        if (m.get("category") or "").lower() == "image"
                        or _looks_like_image_model(m.get("id", ""))]
            # 如果 expand 没返回 category（或全被过滤掉），退回"按名字猜"的结果
            items = filtered or items
        return [m.get("id", "") for m in items if m.get("id")]
    d = get_json(f"{base}/images/models", api_key)
    return [m["id"] for m in d.get("data", [])]


# 名字里带这些词的，按生图模型处理（APIMart 未提供 category 时的兜底）
# 模型 ID 里包含这些子串时视为生图模型。
# 注意：用更精确的模式避免误判（比如 "gpt-4o" 不含 "image"，不会误匹配）。
_IMAGE_HINTS = (
    "gpt-image",       # openai/gpt-image-*
    "gemini-.*-image", # gemini-2.5-flash-image 等
    "seedream",        # 即梦 Seedream
    "wan-",            # 阿里 Wan
    "flux",            # Flux Pro
    "nano.*banana",    # Nano Banana（大小写不敏感匹配）
    "qwen-image",      # Qwen Image
    "dall.?e",         # dall-e-2 / dall-e-3
    "stable.?diffus",  # stable-diffusion / sd-xl
    "sdxl",            # SDXL
)


def _looks_like_image_model(model_id: str) -> bool:
    mid = (model_id or "").lower()
    # 用正则匹配而非子串，避免 "gpt-4o" 被 "image" 误匹配
    import re
    for pat in _IMAGE_HINTS:
        if re.search(pat, mid):
            return True
    return False


def check_key(api_key: str) -> dict:
    """校验 key，并拿到 usage / limit。

    OpenRouter 有 `/key`。APIMart 实测 `/v1/key` 与 `/v1/me` 都是 404，
    它没有余额查询接口 —— 所以改用一次最轻量的 `/models` 请求来校验 key 是否有效，
    返回值里带 `_provider` 标记，调用方不要再去看 data.usage。
    """
    base = provider_base()
    if settings.API_PROVIDER == "apimart":
        d = get_json(f"{base}/models", api_key)      # 无效 key 会抛 ApiError(401)
        n = len(d.get("data") or [])
        return {"_provider": "apimart", "_model_count": n,
                "data": {"usage": None, "limit": None}}
    return get_json(f"{base}/key", api_key)


def error_hint(e: Exception) -> str:
    """给 TUI 用的**单行**错误提示（TUI 里不能 print 多行）。

    为什么需要它：curses 界面里只能 log 一行，
    但"HTTP Error 403: Forbidden"这种信息对用户毫无意义。
    这里把常见错误翻译成一句可操作的话。
    返回空串表示没有额外建议。
    """
    # ⚠️ 裸 HTTPError 没有 .status（只有 .code），且 str() 只有
    #    "HTTP Error 403: Forbidden" —— **body 里的真实原因读不到**。
    #    所以这里要先把 HTTPError 转成 ApiError 再判断（真机 bug，2026-09-19）。
    if isinstance(e, urllib.error.HTTPError):
        e = _http_error_to_api(e)
    msg = str(e)
    low = msg.lower()
    status = getattr(e, "status", None) or getattr(e, "code", None)

    # ⚠️ 余额判断必须在权限之前（APIMart 余额不足返回 403）
    if (status == 402 or "insufficient" in low or "quota" in low
            or ("balance" in low and "insufficient" in low)
            or "payment" in low or "credit" in low):
        import re as _re
        cur = _re.search(r"(?:current|balance)\s*[:=]?\s*(\d+\.?\d*)", msg)
        req = _re.search(r"required\s*[:=]?\s*(\d+\.?\d*)", msg)
        bits = []
        if cur:
            bits.append(f"当前 ${cur.group(1)}")
        if req:
            bits.append(f"需要 ${req.group(1)}")
        detail = f"（{'，'.join(bits)}）" if bits else ""
        return (f"账户余额不足{detail} —— 去 APIMart 充值；"
                "编辑比文生图贵（要传参考图）")
    if status in (401, 403):
        return "key 无效或没权限（去设置里换 key）"
    if status == 429:
        return "触发限流，等几秒再试"
    if status in (500, 502, 503, 504):
        return "服务端错误，稍后重试"
    if "timed out" in low or "超时" in msg:
        return "超时了，可以降到 low 档或换 1k 分辨率"
    if "certificate" in low or "ssl" in low:
        return "TLS 证书问题，换个网络试试"
    if "region" in low:
        return "地区限制，换网络或换模型"
    return ""


def explain_error(e: Exception) -> None:
    """把 OpenRouter 的报错翻译成"你该怎么办"。"""
    if isinstance(e, urllib.error.HTTPError):
        e = _http_error_to_api(e)          # body 里的真实原因要读出来
    msg = str(e)
    print(f"\n  [X] 失败：{msg}")
    low = msg.lower()
    status = getattr(e, "status", None) or getattr(e, "code", None)

    # ⚠️ 顺序很重要：**先判余额**，再判权限。
    #    因为 APIMart 余额不足会返回 403（不是 402），
    #    若先按 403 → "API key 无效" 就会误导用户（真机 bug，2026-09-19）。
    _no_money = (status in (402, 403)
                 or "insufficient" in low or "quota" in low
                 or "balance" in low or "credit" in low
                 or "payment" in low)
    if _no_money and ("insufficient" in low or "quota" in low
                      or "balance" in low or status == 402
                      or "credit" in low or "payment" in low):
        print("    -> **账户余额不足**（不是权限问题）。")
        # 尽量把余额数字捞出来给用户看
        import re as _re
        cur = _re.search(r"(?:current|balance)\s*[:=]?\s*(\d+\.?\d*)", msg)
        req = _re.search(r"required\s*[:=]?\s*(\d+\.?\d*)", msg)
        if cur or req:
            parts = []
            if cur:
                parts.append(f"当前 ${cur.group(1)}")
            if req:
                parts.append(f"本次需要 ${req.group(1)}")
            print(f"       {'，'.join(parts)}")
        print("       · 充值后重试")
        print("       · 或先降成本：Ctrl+P 换 low、Ctrl+E 用 1k、Ctrl+A 设 1 张")
        print("       · 编辑比文生图贵（要传参考图，输入 token 更多）")
    elif "not available in your region" in low:
        print("    -> 这是**地区限制**（不是你的代码问题）。可以：")
        print("       · 换网络（Wi-Fi <-> 流量）")
        print("       · 换模型（设置 -> 1）")
        print("       · 白名单内备选：google/gemini-2.5-flash-image、openai/gpt-image-1-mini")
        print("       · 想先跑通流程：设置 -> 5 切到离线模式")
    elif "no allowed providers" in low:
        print("    -> 账号设了 provider 白名单，这个模型不在里面。")
        print("       换模型，或去 https://openrouter.ai/settings/privacy 放开")
    elif status in (401, 403) or "invalid_api_key" in low or "no auth" in low:
        print("    -> API key 无效或没权限。删掉这个文件重跑：")
        print(f"       {settings.KEY_FILE}")
    elif status == 429:
        print("    -> 触发限流（已自动重试 3 次），等几秒再试")
    elif "certificate" in low or "ssl" in low:
        print("    -> TLS 证书问题：pip 装 certifi，或换个网络")
    elif "timed out" in low or "超时" in msg:
        print("    -> 超时。high 档大画幅可能要 20s+；也可以先降到 low")
    else:
        print("    -> 可先切到离线模式，确认是本机流程问题还是服务端问题")
