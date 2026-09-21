#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""提示词润色功能测试（全新接口：4 选项模式）。

旧接口 chat/run/system_for/DEFAULT_MODEL 已废弃，新接口：
  polish.picks(prompt, kind, aspect) -> list[str]   返回 4 个选项
  polish.run(prompt, kind, aspect)   -> str         单条回退
  polish.enabled()                   -> bool
  polish.explain_error(e)            -> None
  polish.describe()                  -> str
  polish.MAX_TOKENS                  -> int
  polish.DIVIDER                     -> str

全部离线：用一个假 urlopen 拦截请求，验证：
  * 批量模式下请求体正确（system 包含示例 + 强约束）
  * 响应解析正确（按 ---DIVIDER--- 分割）
  * 输出清洗正确（去掉围栏、开场白、引号）
  * UI 交互：Y/n/r 三种分支的行为
  * 失败时退回原提示词，不阻断主流程
  * 配置可被环境变量与运行期覆盖

用法：python3 tests/test_polish.py
"""
from __future__ import annotations

import json
import sys
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from impydroid import polish, settings, ui          # noqa: E402

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(errors="replace")
    except Exception:                                # noqa: BLE001
        pass

RESULTS = []


def check(name, cond, extra=""):
    RESULTS.append((name, bool(cond), extra))


# ---------------------------------------------------------------- 假响应
class FakeResp:
    def __init__(self, payload):
        self._b = json.dumps(payload).encode("utf-8")
    def read(self):
        return self._b
    def __enter__(self):
        return self
    def __exit__(self, *a):
        return False


def reply(text):
    return {"id": "x", "object": "chat.completion", "model": "m",
            "choices": [{"index": 0, "finish_reason": "stop",
                         "message": {"role": "assistant", "content": text}}],
            "usage": {"total_tokens": 10}}


class Spy:
    """记录最后一次请求的 URL / headers / body。"""
    def __init__(self, text):
        self.text = text
        self.url = None
        self.headers = None
        self.body = None

    def __call__(self, req, timeout=None, context=None):
        self.url = req.full_url
        self.headers = dict(req.headers)
        self.body = json.loads(req.data.decode("utf-8"))
        return FakeResp(reply(self.text))


# ============================================================ 批量模式请求
def test_batch_request_shape():
    """回归：曾经断言硬编码私有端点，现改为用户可配置（默认建议值）。"""
    # 用显式配置，避免依赖任何内置默认值
    settings.set_polish(base_url="https://example.test/v1",
                        model="test/model", api_key="sk-test-key")
    try:
        spy = Spy("opt1\n---DIVIDER---\nopt2\n---DIVIDER---\nopt3\n---DIVIDER---\nopt4")
        with mock.patch("urllib.request.urlopen", spy):
            opts = polish.picks("hello", kind="gen", aspect="16:9")

        check("请求: 命中 /chat/completions", spy.url.endswith("/chat/completions"), str(spy.url))
        check("请求: 端点用配置值", spy.url.startswith("https://example.test/v1/"), str(spy.url))
        check("请求: temperature=0.9（高 diversity）", spy.body["temperature"] == 0.9,
              spy.body.get("temperature"))
        check("请求: max_tokens 合理", spy.body["max_tokens"] >= 1000, spy.body.get("max_tokens"))
        msgs = spy.body["messages"]
        check("请求: system 提示包含 DIVIDER 约束", "---DIVIDER---" in msgs[0]["content"])
        check("请求: system 提示包含示例", "EXAMPLE" in msgs[0]["content"] or "示例" in msgs[0]["content"])
        check("请求: user 包含 aspect 和提示词", "hello" in msgs[1]["content"] and "16:9" in msgs[1]["content"])
        check("请求: stream=False", spy.body.get("stream") is False)
        check("请求: 带 Authorization", "Authorization" in spy.headers)
        check("返回: 4 个选项", len(opts) == 4, str(len(opts)))
    finally:
        settings.set_polish(base_url="", model="", api_key="")


# ============================================================ 单条模式
def test_single_mode():
    settings.set_polish(base_url="https://example.test/v1",
                        model="test/model", api_key="sk-test-key")
    try:
        spy = Spy("single result")
        with mock.patch("urllib.request.urlopen", spy):
            out = polish.run("test", kind="edit")
        check("单条: 返回正确文本", out == "single result", out)
        # 单条用的 system 不包含 DIVIDER
        check("单条: system 不含 DIVIDER", "---DIVIDER---" not in spy.body["messages"][0]["content"])
    finally:
        settings.set_polish(base_url="", model="", api_key="")


# ============================================================ 输出清洗
def test_cleaning():
    cases = [
        ("```\nA beautiful cat\n```", "A beautiful cat"),
        ("Here is the prompt: A red car", "A red car"),
        ('"A red car"', "A red car"),
        ("  extra   spaces   here  ", "extra spaces here"),
    ]
    bad = []
    for raw, want in cases:
        got = polish._clean(raw)
        if got != want:
            bad.append((raw, want, got))
    check("清洗: 围栏/开场白/引号/空白（4 例）", not bad, str(bad[:2]))


# ============================================================ 两套 system prompt
def test_system_prompts():
    check("system: edit 强调保留主体", "保持不变" in polish._SYS_EDIT_BATCH)
    check("system: edit 有 DIVIDER 说明", "---DIVIDER---" in polish._SYS_EDIT_BATCH)
    check("system: gen 与 edit 不同", polish._SYS_BATCH != polish._SYS_EDIT_BATCH)
    ug = polish.build_user_text("a cat", "gen", "16:9")
    ue = polish.build_user_text("换成雪山", "edit")
    check("user: gen 提到 aspect", "16:9" in ug, ug[:80])
    check("user: edit 提到编辑要求", "编辑要求" in ue or "保留" in ue, ue[:80])


# ============================================================ 不内置任何凭据
def test_no_builtin_credentials():
    """回归：分发版曾内置私有端点 + 真实 API key；现在必须为空。

    当初怎么坏的：polish.py 里写死 `_DEFAULT_KEY = "sk-c3af..."` 与一个
    私有端点，导致①凭据泄漏进仓库 ②用户改不了端点 ③别人跑起来用的是
    你的账号。修法：默认端点/模型只是建议值，key 一律留空、由用户配置；
    未配置 key 时 enabled() 必须返回 False，绝不偷偷发请求。
    """
    check("凭据: polish 模块不含硬编码 sk- key",
          not any(isinstance(getattr(polish, n, None), str)
                  and str(getattr(polish, n)).startswith("sk-")
                  for n in dir(polish) if not n.startswith("__")),
          "发现内置 key")
    # 清空配置 → enabled 必须为 False
    old = (settings.POLISH_BASE_URL, settings.POLISH_MODEL, settings.POLISH_API_KEY)
    try:
        settings.set_polish(base_url="", model="", api_key="")
        check("凭据: 未配置 key 时 enabled()=False", polish.enabled() is False,
              str(polish.enabled()))
        check("凭据: 未配置时 configured()=False", polish.configured() is False, "")
        hit = {"n": 0}
        def no_call(url, timeout=None, context=None):
            hit["n"] += 1
            return FakeResp(reply("x"))
        with mock.patch("urllib.request.urlopen", no_call):
            try:
                polish.picks("x")
            except Exception:                        # noqa: BLE001
                pass
        check("凭据: 未配置时不发请求", hit["n"] == 0, str(hit["n"]))
    finally:
        settings.set_polish(base_url=old[0], model=old[1], api_key=old[2])


# ============================================================ 配置持久化
def test_persist_config(tmp_home=None):
    """回归：润色配置只能靠环境变量（重启即丢）。现在应持久化到数据目录。"""
    import tempfile
    from pathlib import Path as _P
    # 用项目内目录而不是系统 temp（某些沙箱里 %TEMP% 不可写）
    base = _P(__file__).resolve().parent.parent
    d = _P(tempfile.mkdtemp(prefix=".polish-cfg-", dir=str(base)))
    old_home = settings.HOME
    old = (settings.POLISH_BASE_URL, settings.POLISH_MODEL, settings.POLISH_API_KEY)
    try:
        settings.set_home(str(d))
        settings.set_polish(base_url="", model="", api_key="")
        settings.set_polish(base_url="https://persist.test/v1",
                            model="persist-model", api_key="sk-persist-123456",
                            persist=True)
        # 模拟重启：清空内存，从磁盘重新读
        settings.set_polish(base_url="", model="", api_key="")
        cfg = settings.load_polish_config()
        check("持久化: base_url 落盘", cfg.get("base_url") == "https://persist.test/v1",
              str(cfg))
        check("持久化: model 落盘", cfg.get("model") == "persist-model", str(cfg))
        check("持久化: key 写独立文件",
              settings.load_polish_key() == "sk-persist-123456",
              str(settings.load_polish_key()))
    finally:
        settings.set_home(str(old_home))
        settings.set_polish(base_url=old[0], model=old[1], api_key=old[2])
        import shutil
        shutil.rmtree(d, ignore_errors=True)


# ============================================================ 配置覆盖
def test_config_override():
    old = (settings.POLISH_BASE_URL, settings.POLISH_MODEL, settings.POLISH_API_KEY,
           settings.POLISH_ENABLED)
    try:
        settings.set_polish(base_url="https://example.test/v2",
                            model="my/model:1", api_key="sk-abc")
        spy = Spy("opt1\n---DIVIDER---\nopt2")
        with mock.patch("urllib.request.urlopen", spy):
            polish.picks("x", kind="gen")
        check("配置: base_url 生效", spy.url.startswith("https://example.test/"), spy.url)
        check("配置: 请求用新模型", spy.body["model"] == "my/model:1")
        check("配置: key 生效", "sk-abc" in str(spy.headers.get("Authorization", "")))

        # 已经是 .../chat/completions 结尾时不应重复拼
        settings.set_polish(base_url="https://example.test/v1/chat/completions")
        spy2 = Spy("x")
        with mock.patch("urllib.request.urlopen", spy2):
            polish.picks("x")
        check("配置: 端点幂等（不重复拼）",
              spy2.url == "https://example.test/v1/chat/completions", spy2.url)
        settings.set_polish(enabled=False)
        check("配置: enabled=False 时不可用", polish.enabled() is False)
        settings.set_polish(enabled=True)
        check("配置: enabled=True 时可用", polish.enabled() is True)
    finally:
        settings.set_polish(base_url=old[0], model=old[1], api_key=old[2],
                            enabled=True)


# ============================================================ Y / n / r 分支（UI 层）
def feed(answers):
    it = iter(answers)
    return lambda *a, **k: next(it)


def _make_input_mock(answers):
    """创建随用的 input 伪装，用完就返回旧值。"""
    import builtins
    it = iter(answers)
    original = builtins.input
    def mock_input(*a, **k):
        return next(it)
    return mock_input, original


def test_flow_branches():
    import builtins
    # 必须先配置（有 key）才 enabled()=True，否则 polish_flow 直接返回原文
    settings.set_polish(base_url="https://example.test/v1",
                        model="test/model", api_key="sk-test-key")
    # ---- Y + 选 2
    spy = Spy("opt1\n---DIVIDER---\nopt2\n---DIVIDER---\nopt3\n---DIVIDER---\nopt4")
    with mock.patch("urllib.request.urlopen", spy):
        mock_in, orig = _make_input_mock(["y", "2"])
        try:
            builtins.input = mock_in
            out = ui.polish_flow("orig", kind="gen")
            check("流程 Y+2: 返回第 2 条选项", out == "opt2", out)
        finally:
            builtins.input = orig

    # ---- n 取消：不该发请求
    hit = {"n": 0}
    def no_call(url, timeout=None, context=None):
        hit["n"] += 1
        return FakeResp(reply("x"))
    with mock.patch("urllib.request.urlopen", no_call):
        mock_in, orig = _make_input_mock(["n"])
        try:
            builtins.input = mock_in
            out = ui.polish_flow("orig", kind="gen")
            check("流程 n: 返回原提示词", out == "orig", out)
            check("流程 n: 不调用 LLM", hit["n"] == 0, str(hit["n"]))
        finally:
            builtins.input = orig

    # ---- r 重新生成 + 选 1
    calls = {"n": 0}
    def two(url, timeout=None, context=None):
        calls["n"] += 1
        return FakeResp(reply("opt1\n---DIVIDER---\nopt2"))
    with mock.patch("urllib.request.urlopen", two):
        mock_in, orig = _make_input_mock(["y", "r", "y", "1"])
        try:
            builtins.input = mock_in
            out = ui.polish_flow("orig", kind="gen")
            check("流程 r->1: 调用两次并采用第 1 条",
                  calls["n"] == 2 and out == "opt1", f"calls={calls['n']} out={out}")
        finally:
            builtins.input = orig

    # ---- 空回车：y + 空（不选，默认取消）
    spy3 = Spy("x")
    with mock.patch("urllib.request.urlopen", spy3):
        mock_in, orig = _make_input_mock(["y", ""])
        try:
            builtins.input = mock_in
            out = ui.polish_flow("orig", kind="gen")
            check("流程 空回车: 退回原提示词", out == "orig", out)
        finally:
            builtins.input = orig
    settings.set_polish(base_url="", model="", api_key="")


# ============================================================ 失败不阻断
def test_failure_fallback():
    import builtins
    old_input = builtins.input
    # 必须先配置（有 key）才 enabled()=True，否则不会走到请求分支
    settings.set_polish(base_url="https://example.test/v1",
                        model="test/model", api_key="sk-test-key")

    def boom(url, timeout=None, context=None):
        raise OSError("network down")

    try:
        with mock.patch("urllib.request.urlopen", boom):
            builtins.input = feed(["y", "1"])
            try:
                out = ui.polish_flow("orig", kind="gen")
                check("失败: 退回原提示词且不抛异常", out == "orig", out)
            except Exception as e:                           # noqa: BLE001
                check("失败: 退回原提示词且不抛异常", False, f"{type(e).__name__}: {e}")

        # 关掉润色：完全不该发请求
        settings.set_polish(enabled=False)
        hit = {"n": 0}
        def should_not_call(url, timeout=None, context=None):
            hit["n"] += 1
            return FakeResp(reply("x"))
        with mock.patch("urllib.request.urlopen", should_not_call):
            builtins.input = feed(["y", "1"])            # 就算喂 y 也不该被消费
            out = ui.polish_flow("orig", kind="gen")
        check("关闭时: 不发请求且直接返回原文",
              out == "orig" and hit["n"] == 0, f"out={out} calls={hit['n']}")
    finally:
        builtins.input = old_input
        settings.set_polish(base_url="", model="", api_key="", enabled=True)


def main():
    print("polish test suite (4-option batch mode, offline)")
    print("-" * 62)
    for fn in (test_batch_request_shape, test_single_mode, test_cleaning,
               test_system_prompts, test_config_override,
               test_no_builtin_credentials, test_persist_config,
               test_flow_branches,
               test_failure_fallback):
        try:
            fn()
        except Exception as e:                        # noqa: BLE001
            import traceback
            check(f"{fn.__name__} 崩溃", False,
                  f"{type(e).__name__}: {e}\n{traceback.format_exc()[-300:]}")
    passed = sum(1 for _, ok, _ in RESULTS if ok)
    for name, ok, extra in RESULTS:
        line = f"  {'OK  ' if ok else 'FAIL'} {name}"
        if extra and not ok:
            line += f"\n         {extra}"
        print(line)
    print("-" * 62)
    print(f"{passed}/{len(RESULTS)} passed")
    return 0 if passed == len(RESULTS) else 1


if __name__ == "__main__":
    sys.exit(main())
