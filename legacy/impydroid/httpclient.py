"""HTTP 客户端（urllib，带重试与可操作的错误信息）。

不用 requests 的原因：Pydroid 里 `pip install requests` 经常失败（编译依赖/网络），
而 urllib 是标准库。
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
import random
import ssl
import time
import urllib.error
import urllib.request


from . import settings
from .console import print


_SSL_CTX: ssl.SSLContext | None = None


class ApiError(RuntimeError):
    """带 HTTP 状态码的错误。"""

    def __init__(self, msg: str, status: int | None = None):
        super().__init__(msg)
        self.status = status


def ssl_context() -> ssl.SSLContext:
    """优先用 certifi 的证书束；没有就用系统默认；全失败才退回不校验。

    Pydroid 常见的坑：系统 CA 路径不对 → CERTIFICATE_VERIFY_FAILED。
    """
    global _SSL_CTX
    if _SSL_CTX is not None:
        return _SSL_CTX
    try:
        import certifi                                       # type: ignore
        _SSL_CTX = ssl.create_default_context(cafile=certifi.where())
        return _SSL_CTX
    except Exception:                                        # noqa: BLE001
        pass
    try:
        _SSL_CTX = ssl.create_default_context()
        return _SSL_CTX
    except Exception:                                        # noqa: BLE001
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        print("  [!] 无法加载 CA 证书，已关闭校验（仅影响本机与 OpenRouter 之间的校验）")
        _SSL_CTX = ctx
        return ctx


def err_from_body(raw: bytes) -> tuple[str, str | None]:
    """从错误体里取 message 与错误码。code 统一成 str —— 有的错误体里它是数字。"""
    try:
        d = json.loads(raw.decode("utf-8", "replace"))
    except Exception:                                        # noqa: BLE001
        return raw.decode("utf-8", "replace")[:300], None
    err = d.get("error") if isinstance(d, dict) else None
    if isinstance(err, dict):
        raw_code = err.get("code") or err.get("type")
        code = str(raw_code) if raw_code not in (None, "") else None
        return str(err.get("message") or err)[:400], code
    if err:
        return str(err)[:400], None
    return json.dumps(d)[:300], None


def post_json(url: str, payload: dict, api_key: str,
              timeout: float | None = None) -> dict:
    """POST JSON。网络类错误自动重试；4xx 直接抛 ApiError。"""
    timeout = timeout if timeout is not None else settings.TIMEOUT
    body = json.dumps(payload).encode("utf-8")
    last: Exception | None = None
    for attempt in range(1, settings.MAX_ATTEMPTS + 1):
        req = urllib.request.Request(
            url, data=body, method="POST",
            headers={
                "Authorization": f"Bearer {api_key}",
                "Content-Type": "application/json",
                "Accept": "application/json",
                "HTTP-Referer": "https://openrouter.ai/",
                "X-Title": "impydroid",
            },
        )
        try:
            with urllib.request.urlopen(req, timeout=timeout,
                                        context=ssl_context()) as r:
                return json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            msg, code = err_from_body(e.read())
            tail = f"/{code}" if code not in (None, "") else ""
            last = ApiError(f"{msg} (HTTP {e.code}{tail})", e.code)
            retryable = e.code == 429 or 500 <= e.code < 600
            if not retryable or attempt == settings.MAX_ATTEMPTS:
                raise last from None
        except urllib.error.URLError as e:
            last = ApiError(f"网络错误：{e.reason}")
        except TimeoutError:
            last = ApiError(f"超时（>{timeout:.0f}s）")
        except Exception as e:                               # noqa: BLE001
            last = ApiError(f"{type(e).__name__}: {e}")
        if attempt < settings.MAX_ATTEMPTS:
            delay = min(8.0, settings.BACKOFF_BASE ** attempt) + random.uniform(0, 0.4)
            print(f"  ...第 {attempt} 次失败，{delay:.1f}s 后重试：{last}")
            time.sleep(delay)
    raise last or ApiError("未知错误")


def get_json(url: str, api_key: str | None = None, timeout: float = 30) -> dict:
    headers = {"Accept": "application/json"}
    if api_key:
        headers["Authorization"] = f"Bearer {api_key}"
    req = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=timeout, context=ssl_context()) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        msg, code = err_from_body(e.read())
        tail = f"/{code}" if code not in (None, "") else ""
        raise ApiError(f"{msg} (HTTP {e.code}{tail})", e.code) from None
    except Exception as e:                                   # noqa: BLE001
        raise ApiError(f"{type(e).__name__}: {e}") from None
