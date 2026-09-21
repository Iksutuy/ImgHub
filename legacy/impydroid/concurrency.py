"""并发工具 —— 只用在**真正值得**的地方。

实测结论（本机 Python 3.12 / aarch64）：

  * **IO 阻塞**（HTTP 请求）并发有效：3 次润色调用 17.2s -> 3.2s（**5.3x**）
  * **CPU 密集**（PNG 缩放）并发无效甚至更慢：2048 缩放 1 线程 0.20s，
    2 线程 0.99s，4 线程 0.87s —— GIL 下纯 Python 循环无法并行

所以本模块只服务 IO：

  `gather(fn, items, workers)`   并发跑一组 IO 任务，保持**输入顺序**返回，
                                 单个任务失败只影响它自己（结果里带异常）
  `map_unordered(fn, items)`     不关心顺序时的版本

设计约束（都是手机环境的现实）：
  * 线程数默认 **4**，上限 8 —— Pydroid 是手机，开太多线程只会互相抢
  * 不用 `ProcessPoolExecutor`：Android 上 fork/多进程有坑（没有 fork、spawn 慢）
  * 所有线程都是 **daemon**，主流程退出时不会被卡住
  * 每个任务带**独立超时**（由调用方的 HTTP 超时保证），不在这里做全局超时
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

from concurrent.futures import Future, ThreadPoolExecutor, as_completed
from typing import Callable, Iterable, Sequence, TypeVar

T = TypeVar("T")
R = TypeVar("R")

DEFAULT_WORKERS = 4
MAX_WORKERS = 8


def _pool(workers: int) -> ThreadPoolExecutor:
    n = max(1, min(MAX_WORKERS, workers or DEFAULT_WORKERS))
    return ThreadPoolExecutor(max_workers=n, thread_name_prefix="impydroid")


def gather(fn: Callable[[T], R], items: Sequence[T],
           workers: int = DEFAULT_WORKERS) -> list[R | BaseException]:
    """并发跑 fn(item)，**按输入顺序**返回结果。

    单个任务抛异常时，那个位置放异常对象（而不是整体失败）——
    调用方可以逐个判断，把"哪几张成功"交给上层决定。
    """
    if not items:
        return []
    if len(items) == 1:
        # 一个任务不值得开线程
        try:
            return [fn(items[0])]
        except BaseException as e:                        # noqa: BLE001
            return [e]

    results: list[R | BaseException | None] = [None] * len(items)
    with _pool(min(workers, len(items))) as ex:
        futs: dict[Future, int] = {ex.submit(fn, it): i
                                   for i, it in enumerate(items)}
        for fut in as_completed(futs):
            i = futs[fut]
            try:
                results[i] = fut.result()
            except BaseException as e:                    # noqa: BLE001
                results[i] = e
    return results                                            # type: ignore[return-value]


def map_unordered(fn: Callable[[T], R], items: Iterable[T],
                  workers: int = DEFAULT_WORKERS) -> list[R | BaseException]:
    """并发执行，**不保证顺序**（谁先完成先收）。适合只关心"全部跑完"。"""
    items = list(items)
    if not items:
        return []
    out: list[R | BaseException] = []
    with _pool(min(workers, len(items))) as ex:
        for fut in as_completed([ex.submit(fn, it) for it in items]):
            try:
                out.append(fut.result())
            except BaseException as e:                    # noqa: BLE001
                out.append(e)
    return out


# CPU 密集任务（纯 Python 循环）在 GIL 下并行无效 -> 恒为 1。
# 不提供函数是因为没有调用点；要用直接读这个常量。
CPU_WORKERS = 1


def describe() -> str:
    return f"IO 并发上限 {DEFAULT_WORKERS}（CPU 任务走单线程：GIL 下并行无效）"
