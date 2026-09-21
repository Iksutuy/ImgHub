# imgagent / impydroid — 框架与方案

> 版本 **5.17.0** · 纯标准库 Python · 目标平台 Pydroid 3 / Termux（Android）
> 本文是**技术全景**：模块、数据流、API 契约、实现方式、关键设计决策。

---

## 一、项目定位

一个**在 Android 手机上跑**的 AI 生图客户端（TUI + CLI 双界面）。

**硬性前提（决定了所有技术选择）**：

| 前提 | 后果 |
|---|---|
| 运行环境是 Android 手机 | 屏幕窄（40–50 列）、无鼠标、软键盘 |
| Pydroid 3 / Termux 的 Python | `pip install` 经常失败（编译依赖/网络） |
| 可能没有 repository plugin | 不能依赖 Pillow / pyjnius |
| 用户怕花钱 | 必须能离线跑通、必须显示成本 |

**核心结论：只用标准库。** 全部 20 个模块，`import` 的都是 Python 自带的东西。

---

## 二、模块地图（20 个文件）

```
main.py                    顶层入口（薄壳，只 import app.run）
impydroid/
├── __init__.py            包元信息 + 版本号
├── __main__.py            python3 -m impydroid
├── console.py         ★   控制台加固（**必须最先导入**）
├── settings.py        ★   常量 + 路径（唯一真源）
├── pngcodec.py            纯标准库 PNG 编解码 + 缩放
├── placeholder.py         离线占位图（确定性）
├── termimg.py             终端字符画渲染（三级：truecolor/256/ascii）
├── httpclient.py          urllib 封装（重试 + 可读错误）
├── api.py             ★   两个 provider 的 API 封装
├── polish.py              提示词润色（chat/completions）
├── store.py               落盘 + 设置 + 历史
├── photos.py              相册发现/选择
├── preview.py             预览（四级降级）
├── android.py             Android 原生（pyjnius，全可选）
├── concurrency.py         并发工具（**只服务 IO**）
├── doctor.py              环境诊断（**故意只用 ASCII**）
├── curses_ui.py       ★   TUI（3543 行，项目最大文件）
├── ui.py                  CLI 界面（菜单循环）
└── app.py                 入口编排（自检 → 界面 → 退出）
```

★ = 核心模块。改这些要格外小心。

---

## 三、分层架构

```
┌──────────────────────────────────────────────────────┐
│  表现层                                              │
│    curses_ui.py  (TUI，手机默认)                      │
│    ui.py         (CLI，菜单循环，备用/调试)            │
│    termimg.py    (终端渲染，TUI 内嵌预览用)            │
├──────────────────────────────────────────────────────┤
│  编排层                                              │
│    app.py        (环境自检 → 选界面 → 退出)           │
│    store.py      (Session：当前图 + 历史 + 设置)       │
├──────────────────────────────────────────────────────┤
│  能力层                                              │
│    api.py        (生成/编辑/上传/轮询)                │
│    polish.py     (提示词润色)                         │
│    photos.py     (相册)  android.py (原生桥)          │
│    preview.py    (预览降级链)                         │
├──────────────────────────────────────────────────────┤
│  基础层                                              │
│    httpclient.py (重试 + 错误翻译)                    │
│    pngcodec.py   (PNG 编解码+缩放)                    │
│    termimg.py    (像素→字符)                          │
│    concurrency.py(IO 并发)                            │
│    settings.py   (常量/路径)  console.py (加固)        │
│    placeholder.py(离线图)     doctor.py (诊断)         │
└──────────────────────────────────────────────────────┘
```

**依赖方向严格单向**：上 → 下。下层**绝不** import 上层。

---

## 四、数据流：一次生成

```
用户输入提示词
   │
   ├─〔可选〕Ctrl+Y 润色 → polish.py → chat/completions → 4 条候选
   │                                     ↓ _pick_pages 逐条翻页选
   ├─ Enter 提交
   │
   ▼
curses_ui._act_generate
   │  组装参数：model / quality / aspect / resolution / output_format / n
   │  收集参考图：load_edit_images() → [(bytes, media), ...]
   ▼
api.generate(offline=False)
   │
   ├── provider == apimart ────────────┐
   │                                   ▼
   │                         _generate_apimart()
   │                           ① 上传参考图 → POST /uploads/images
   │                           ② 提交任务   → POST /images/generations
   │                           ③ 并发轮询   → GET /tasks/{id}（每 3s）
   │                           ④ 下载图片   → GET <result url>
   │
   └── provider == openrouter ─────────┐
                                       ▼
                             同步 POST /images（base64 内联参考图）
   │
   ▼ GenResult(images=[(bytes, media)], cost, tokens)
   │
   ▼
store.save_image()  → 写盘 + 写 history.jsonl + 更新 state.json
   │
   ▼
curses_ui 刷新界面（历史/累计/当前 三块）
```

**关键不变量**：`GenResult.images` 是 `(bytes, media)` 列表，两个 provider 统一。

---

## 五、两个 Provider 的契约对照

| 维度 | OpenRouter | APIMart |
|---|---|---|
| **同步性** | 同步（一次请求拿到图） | **异步**（提交→轮询） |
| 生成端点 | `POST /images` | `POST /images/generations` |
| 轮询端点 | — | `GET /tasks/{task_id}` |
| 上传端点 | — | `POST /uploads/images` |
| **比例字段** | `aspect_ratio` | `size` |
| **参考图字段** | `input_references`（base64 data URL） | `image_urls`（**公网 URL**） |
| 默认模型 | `openai/gpt-image-2.5-flare` | `gpt-image-2.5-flare` |
| 质量档 | auto/low/medium/high | auto/low/medium/**high/xhigh/max** |
| `n` 上限 | 10 | 4 |
| 响应字段 | 直接含图 | `data.result.images[].url[]` |

**代码位置**：`api.py::_build_payload()` 是**唯一**按 provider 分叉的地方。

### APIMart 的异步三步（`_generate_apimart`）

```python
# ① 上传参考图（有 refs 时）
for idx, (b, media) in enumerate(refs[:MAX_REFS], 1):
    url = upload_image(b, media, api_key)      # 失败抛 ApiError，不静默降级
image_urls = [...]

# ② 提交
payload = {"model","prompt","quality","size","n"}
if resolution != "1k": payload["resolution"] = resolution
if output_format:      payload["output_format"] = output_format
if image_urls:         payload["image_urls"] = image_urls[:16]
→ POST /images/generations → task_id

# ③ 并发轮询（concurrency.gather，n 张同时等）
_poll_one(task_info):
    td = poll_task(task_id, max_wait=APIMART_TIMEOUT)
    → 取 result.images[].url[] → 下载 → [(bytes, media)]
```

**为什么并发轮询**：n=4 时串行要 4×15s=60s，并发约 15s。

---

## 六、核心概念：待修改图 vs 参考图

这是 v5.13.0 确立的**最重要语义**，也是用户唯一反馈过的"功能不对"。

### 定义

| 概念 | 是什么 | 怎么选 | 数量 | 在 `image_urls` 的位置 |
|---|---|---|---|---|
| **待修改图** | 要被改的主体/底图 | `PgUp`/`PgDn` | 1 张 | **永远第 1 位** |
| **参考图** | 提供人物设定/场景/风格 | `Ctrl+Y` 弹窗 | ≤ 8 张 | 第 2 位起 |

### 实现方式（`curses_ui.load_edit_images`）

```python
def load_edit_images(sess, st):
    out = []
    base = _base_file(sess, st)        # ① 待修改图先装入
    if base: out.append(shrink_for_reference(...))
    for name in _ref_files(st):        # ② 参考图跟在后面
        out.append(shrink_for_reference(...))
    return out, desc
```

### 为什么顺序有语义（实测依据）

用纯红图 + 蓝白图做实验：

```
提交：image_urls = [纯红, 蓝白]
      prompt = "第一张是主体，把背景换成第二张的蓝白条纹"
输出：上部 RGB(29,28,219)=蓝  下部(253,253,253)=白  中部红蓝混合
⇒ 模型理解"第 1 张=主体，第 2 张=参考"
```

**旧版 bug**：直接把参考图当作 `image_urls` 传，模型当成"要融合的素材"，
输出更接近参考图本身 → 用户反馈"好像只是高清化了参考图，没改我的原图"。

### 概念隔离的设计决策

**`Ctrl+Y` 用弹窗而非"加光标处那张"**：

早期尝试过"Ctrl+Y 加当前光标处的图"，但那会导致**"浏览历史挑参考图"顺带改掉待修改图**
——两个概念绑在同一光标上，必然混淆。改成弹窗后彻底独立：

- `PgUp/PgDn`（光标）→ 只改「待修改图」
- `Ctrl+Y`（弹窗）→ 只改「参考图」

---

## 七、TUI 架构（curses_ui.py）

### 7.1 布局

宽屏判定：`w >= _WIDE_COLS (76)`

```
┌─ imgagent v5.17.0 ─ Termux ─ ● 就绪 ─ 2026-09-19 15:28:24 ─┐  顶栏（3 行，含动画+时钟）
├─ 当前 ─────────────────┬─ 历史 / 累计 ─────────────────────┤
│ 文件/尺寸/类型/提示词   │  1) ...001        $0.0048         │  左上=当前
│ 质量/画幅/分辨率/格式   │ ▶2) ...002        $0.0114         │  右上=历史+累计
│ 批量/模式              │  累计 $0.0427   1 张              │
├────────────────────────┼───────────────────────────────────┤
│ 待修改图 + 参考图 2/8   │  消息                             │  左下=参考图管理
│ ▶ 待修改 [#3] x.png    │  15:26:35 已加入参考图：...        │  右下=消息流
│   参考图 2/8（Ctrl+Y） │  ...                              │
│ ▶ 1) [#1] ref.png      │                                   │
│  Home/End 选 · Ctrl+K  │                                   │
├────────────────────────┴───────────────────────────────────┤
│ 输入 · 生成   ↑↓ 翻历史 · Enter 提交 · Esc 清空            │  输入框（高度自适应）
│ > 写提示词，Enter 开始生成                                  │
├─────────────────────────────────────────────────────────────┤
│ [^G]生成  [^A]批量  [^D]编辑  [^Z]模型  [^E]分辨率  ...    │  底栏（1–4 行，分档着色）
└─────────────────────────────────────────────────────────────┘
```

**窄屏（<76 列）**：纵向堆叠（当前 / 参考图 / 历史 / 消息），按优先级丢弃面板。

### 7.2 状态管理

**所有 TUI 状态在 `st: dict` 里**（`_new_state()` 创建），不用模块级全局。

| 键 | 含义 |
|---|---|
| `mode` | `gen` \| `edit` —— 输入框当前语义 |
| `hist` | 历史选中索引（**同时决定「待修改图」**） |
| `scroll` | 历史列表滚动位置 |
| `busy` | 非空 = 后台任务进行中（驱动顶栏动画） |
| `spin` / `t0` | 动画帧 / 起始时间 |
| `refs` | 参考图**文件名**列表（不用 Item 对象：历史会刷新/裁剪） |
| `ref_sel` | 参考图列表里的选中项（Home/End 移动，Ctrl+K 删） |
| `inp_hist_idx` | 提示词历史游标（-1 = 当前输入） |
| `inp_draft` | 翻历史前暂存的草稿 |
| `last_submit` | 最近一次提交内容（**失败时用于恢复提示词**） |

### 7.3 键位表（21 个，全部 Ctrl 组合）

**为什么不占用普通字母**：手机上要打中文，普通字母必须能正常输入。

| 键 | 动作 | 档位 |
|---|---|---|
| `^G` | 生成 gen | 生图（亮） |
| `^A` | 批量张数 batch_n | 生图 |
| `^D` | 编辑 edit | 生图 |
| `^Z` | 模型 model（flare↔sunburst） | 生图 |
| `^E` | 分辨率 resolution（1k/2k/4k） | 生图 |
| `^U` | 输出格式 fmt（png/jpeg/webp） | 生图 |
| `^W` | 画幅 aspect | 生图 |
| `^P` | 质量 quality | 生图 |
| `^R` | 撤回 undo | 生图 |
| `^L` | 预览 preview | 生图 |
| `^F` | 上传 upload | 程序（暗） |
| `^B` | 历史 history | 程序 |
| `^Y` | 参考图 toggle_ref | 程序 |
| `^K` | 删参考 del_ref | 程序 |
| `^O` | 刷新 refresh | 程序 |
| `^X` | 脚本图 script_imgs | 程序 |
| `^I` | 目录 data_dir | 程序 |
| `^N` | 环境 env | 程序 |
| `^S` | 设置 settings | 程序 |
| `^T` | 帮助 help | 程序 |
| `^Q` | 退出 quit | 红色警示 |

**输入框内**（`_INPUT_KEYS`，与功能键分离）：
`^A`行首 `^E`行尾 `^U`清空行 `^W`删词 `^H`退格
**导航键分离**：`↑↓`翻提示词历史、`PgUp/PgDn`选待修改图、`Home/End`选参考图

### 7.4 浮窗家族

| 函数 | 用途 | 特性 |
|---|---|---|
| `_pick_list` | 单选列表 | `↑↓` 选择，`_trunc` 压成一行 |
| `_pick_pages` | **逐条翻页** | `←→` 翻页，长文本**折行**（润色候选用） |
| `_output_overlay` | 可滚动文本 | `↑↓` 滚动 |
| `_preview_overlay` | 全屏看图 | `↑↓` 滚动 + `f` 切缩放 |
| `_modal` | 简单模态 | 任意键关闭 |
| `_ask_yes_no` | Y/N 询问 | `y/n/Esc/Enter` + default |

**统一约定：所有浮窗用 `Esc` 关闭**（v5.13.0 起不再用 `q`）。

---

## 八、离线模式（贯穿全系统的设计）

`offline=True` 时 `api.generate` 直接返回 `placeholder_png`，**不联网不花钱**。

**用途**：
1. 用户想先跑通流程再决定花钱
2. 网络不通/被地区限制时的保底
3. **本项目的测试全部基于它**（这是能离线跑 805 项测试的原因）

`placeholder_png(prompt, step, size)` 是**确定性**的——同样的输入永远同一张图，
便于复现与对拍。

---

## 九、错误处理体系

### 9.1 分层

```
网络层  httpclient.post_json  → 重试（429/5xx）+ 错误体解析
      ↓ ApiError(message, status)
API 层  api._http_error_to_api → HTTPError → ApiError（保留 body）
      ↓ ApiError
界面层  api.error_hint(e)      → TUI 单行提示
      api.explain_error(e)    → CLI 多行提示 + 建议
```

### 9.2 关键教训：状态码会骗人

APIMart 对**余额不足**用两种状态码：

| 码 | body |
|---|---|
| 402 | `insufficient balance (current: 0.02 USD, required: 0.05 USD)` |
| **403** | `insufficient balance: insufficient quota: balance=38650, required=42383`（type=`quota_not_enough`） |

**403 在这里不是权限问题。** 所以判断顺序必须是：

```python
# ⚠️ 余额判断**必须前置**于权限判断，否则 403 被误诊为 "key 无效"
if (status == 402 or "insufficient" in low or "quota" in low
        or "balance" in low or "payment" in low or "credit" in low):
    → 余额不足
elif status in (401, 403):
    → key 无效
```

### 9.3 两个技术陷阱（都踩过）

**① `HTTPError` 的 `str()` 里没有 body**
```python
str(HTTPError)  →  "HTTP Error 403: Forbidden"    # body 完全读不到
e.status        →  AttributeError                  # 只有 .code
```
必须显式 `e.read()` + 缓存（见下条）。

**② `HTTPError.read()` 是流，只能读一次**
```python
raw = e.read()            # 第一次：有内容
raw2 = e.read()           # 第二次：空！
```
修法：缓存到 `e._cached_body`，重复调用安全。

---

## 十、本地存储布局

`HOME` 由 `settings.pick_home()` 决定（默认脚本同目录的 `imgagent_data/`，
可用 `--home` 或 `IMGAGENT_HOME` 覆盖）。

```
<imgagent_data>/
├── .imgagent_key               OpenRouter key（chmod 600）
├── .imgagent_apimart_key       APIMart key（chmod 600）
├── config.json                 设置：model/quality/aspect/offline/resolution/
│                               output_format/batch_n/total_cost/provider
├── state.json                  历史（只存文件指针，不复制图片）
├── history.jsonl               流水账（每次生成一行，含花费）
├── prompt_history.jsonl        提示词历史（每次提交一行，读取时倒序去重）
└── *.png / *.jpg ...           所有图片
```

**为什么 `prompt_history.jsonl` 不复用 `history.jsonl`**：
① 后者是"每次生成一条"的流水账，同一提示词会重复
② 需要记住"输入了但生成失败"的提示词（后者没有）

**⚠️ `settings.set_home()` 必须同时改所有路径**——漏一个就会在 `--home` 后写错地方。

---

## 十一、打包与发布

```bash
python3 tools/make_zip.py                  # 同时打两个平台包
python3 tools/make_zip.py --target termux  # 只打一个
python3 tools/make_zip.py --check          # 只列会打包哪些文件
```

**产物**：`imgagent-<版本>-{pydroid3,termux}.zip`

**两个包代码完全相同**（同一份源码三平台自适应），区别只在：
- zip 内附带的 `INSTALL.txt` 不同
- 顶层目录名不同（解压不互相覆盖）

**打包规则**：
- 所有条目以 `<顶层目录>/` 开头（解压后是干净文件夹）
- 排除：`__pycache__` / `.git` / 运行时数据（key/配置/历史/图片）/ `tests` / `tools`
- **打包后自动校验**：解包到临时目录 → 跑入口自检

---

## 十二、扩展点（怎么加新功能）

| 想加什么 | 改哪里 | 注意 |
|---|---|---|
| 新的循环切换项 | `curses_ui._act_cycle_*` + `_KEYMAP` | 键位先查 `_CTRL_ACTIONS` 是否冲突 |
| 新 provider | `api._build_payload` 分叉 + `settings` 常量 | 三处必须一致：端点/字段名/质量档 |
| 新浮窗 | 参考 `_pick_pages`（最完整） | 必须用 `Esc` 关闭、不 `print` |
| 新面板 | `curses_ui._draw_*` | 注意**行号语义**（见 CONSTRAINTS） |
| 新 API 参数 | `api.generate` 签名 → `_build_payload` / `_generate_apimart` | 两处都要加 |
| 新设置项 | `store.Session.__init__` + `_sanitize` + `_load_files` | 三处都要加 |

---

## 十三、当前能力清单（v5.17.0）

**生成**
- ✅ 文生图（双 provider）
- ✅ 多参考图编辑（待修改图 + 最多 8 张参考图）
- ✅ 批量生成 1–4 张
- ✅ 分辨率 1k/2k/4k
- ✅ 输出格式 png/jpeg/webp
- ✅ 质量 auto/low/medium/high/xhigh/max（APIMart）
- ✅ 模型 flare（快）/ sunburst（编辑精度）
- ✅ 9 种画幅 + auto

**交互**
- ✅ 21 个 Ctrl 快捷键（分档着色）
- ✅ 提示词历史（`↑↓` 翻）
- ✅ 提示词润色（4 条候选，逐条翻页）
- ✅ 预览（4 级降级 + TUI 全屏浮层）
- ✅ 历史回退（`Ctrl+R`）
- ✅ 离线模式
- ✅ 成本显示（每次 + 累计）

**平台**
- ✅ Pydroid 3 / Termux
- ✅ 相册选择（pyjnius，可降级）
- ✅ 系统看图器（可降级到终端字符画）
- ✅ 纯 ASCII 终端（黑屏加固）

**未做（有意）**
- ❌ 透明背景（`background` 参数）—— 用户不是电商场景
- ❌ 精确像素尺寸（`size="1600x1200"`）—— 比例名够用
- ❌ 流式部分图 —— API 不支持
