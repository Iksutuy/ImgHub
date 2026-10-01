namespace ImgHub.Integration.Tests;

/// <summary>
/// 进程级**环境变量**相关测试的串行集合。
///
/// ## 为什么需要它（实测 CI 失败）
/// `Environment.SetEnvironmentVariable` 改的是**整个进程**的状态，而 xunit 默认
/// 并行跑不同测试类。下面这些类都会写 `IMGHUB_HOME` / `IMGAGENT_HOME`：
///   · <see cref="WorkbenchFlowTests"/>            —— 构造函数里设成自己的临时目录
///   · <see cref="GenerationRequestContractTests"/> —— 同上
///   · <see cref="RenamePathTests"/>               —— 还会把 IMGHUB_HOME 置为 null
///                                                     来单独验证回退逻辑
/// 并行时 A 类刚设好、B 类就把它清掉或改掉 → `ResolveHome()` 返回另一个类
/// 的目录。实测（CI run 3677xxxxx）：
///   `RenamePathTests.ResolveHome_PrefersExplicitEnv`
///   Expected `...imghub-env-1c0f...` / Actual `...imghub-req-83c3...`
///   —— 正是另一个测试类的临时目录名。
///
/// 同一提交重跑可能变绿 ⇒ 典型**测试互相干扰**，不是产品缺陷。
///
/// ## 约定
/// ⚠️ 任何调用 <c>Environment.SetEnvironmentVariable</c> 的测试类，
///    都必须加 <c>[Collection(EnvironmentVariableTests.Name)]</c>。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariableTests
{
    public const string Name = "Environment variables (process-wide state)";
}
