## 这个 PR 做了什么

<!-- 一两句话。若修的是 issue，写「closes #N」。 -->

## 改动类型

- [ ] 修 bug
- [ ] 新功能
- [ ] 重构（行为不变）
- [ ] 文档
- [ ] 构建 / CI

## 自检（不是走过场）

- [ ] `dotnet test tests/ImgHub.Core.Tests` 全绿
- [ ] `dotnet test tests/ImgHub.Integration.Tests` 全绿
- [ ] 改了 XAML → **人工跑过桌面版点过一次**（测试覆盖不到 UI 可达性）
- [ ] 改了 `AppConfig` / `AppJsonContext` / `ImageApi` / `Catalog` / `HttpJsonClient` / XAML 绑定
      → 已跑 **Native AOT publish 并以「能启动」为验收**（见 `CONTRIBUTING.md` 第四节）
- [ ] 改了 UI 布局 → 已确认**宽屏三栏与窄屏堆叠两套都同步**
      （消息 / 历史 / 高级参数改 `Views/Sections/*` 即可，预览区与主参数区需两处都改）
- [ ] 新增界面文案 → `Localizer.cs` **三个字典各加一条**，XAML 用 `{CompiledBinding L[key]}`
- [ ] 无新增静默 `catch { }`（约束 I1）

> 只改注释 / `docs/` / 测试时，可不跑 AOT，但请在下方说明。

## 未跑的验证及原因

<!-- 例如「本机无 Android 设备，只验证了 APK 构建通过」。没有就写「无」。 -->

## 截图（UI 改动请附）

<!-- 宽屏 + 窄屏各一张最好。 -->
