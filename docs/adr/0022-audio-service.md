# ADR-0022：音频服务 —— IAudioUtility：音乐单通道 + 池化音效 + 分组音量

**Status:** Accepted（2026-07-04）

## Context

roadmap 中期新模块第二项：音频服务——需求普适（所有游戏都要 BGM + 音效 + 设置页音量条），roadmap 圈定的范围是**分组音量 / 淡入淡出 / AudioSource 池化（吃现成对象池）**。

既有约束与先例：

- **定位先想清楚**：Unity 的 `AudioSource` 组件本身已经能干「挂在对象上持续发声」的活（引擎组件可跨层）。框架服务要补的是它不管的部分——**全局播放编排**：BGM 单通道切换、一次性音效不想每处手挂组件、设置页三条音量滑条要能实时作用到所有在播声音。
- **ports & adapters**：零第三方依赖的能力整体留内核（`PoolUtility` / `StorageUtility` 先例）；重依赖才拆模块 asmdef。
- **对象池先例**（ADR-0007）：`PoolUtility` 刻意不依赖 `IAssetUtility`——工具之间不互拉依赖，组合归调用方。
- **失败语义先例**：池对 Dispose 后误用是「Editor/Dev LogError + 宽容 no-op」；存储是 fail-fast 抛异常（写丢失不可容忍）。音频丢一声不致命，跟池走。
- **生命周期理念**：一切可停止的东西最好能进 `DisposableBag` 随宿主自动清理。

## Decision

### 1. API 形态：音乐单通道 + 音效句柄 + 分组音量

```csharp
public interface IAudioUtility : IUtility
{
    // 音乐：全局单通道，切换自动交叉淡入淡出
    void PlayMusic(AudioClip clip, float fadeSeconds = 0.5f, bool loop = true, float volume = 1f);
    void StopMusic(float fadeSeconds = 0.5f);
    AudioClip CurrentMusic { get; }

    // 音效：池化 AudioSource，一次性播完自动回收；loop=true 时用返回的 handle 停
    AudioHandle PlaySfx(AudioClip clip, float volume = 1f, float pitch = 1f, bool loop = false, string group = AudioGroups.Sfx);
    AudioHandle PlaySfxAt(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f, bool loop = false, string group = AudioGroups.Sfx, float minDistance = 1f, float maxDistance = 500f);
    void StopAllSfx();

    // 分组音量（含总音量），Set 即时作用于所有在播声音
    float MasterVolume { get; set; }
    float GetGroupVolume(string group);
    void SetGroupVolume(string group, float volume);
}
```

- **音乐是单通道语义**，不走 handle：`PlayMusic` 切换、`StopMusic` 停止，同 clip 在播时重复调用 = no-op（幂等，场景重入直接调不用先查状态）。默认 `loop=true`；片头 / 结算曲传 `loop=false` 时，自然结束会清空 `CurrentMusic`、归还 voice 并释放 clip 引用。绝大多数游戏 BGM 就是「同时只有一首」，双 BGM 叠加是罕见需求（用 loop 音效 + 自定义组即可组合出来）。
- **音效返回 `AudioHandle`**（`readonly struct`，零分配）：一次性音效可直接丢弃返回值（fire-and-forget）；循环音效（环境声 / 引擎声）持 handle 调 `Stop(fadeSeconds)`。**handle 实现 `IDisposable`**（Dispose = 立即停），可进 `DisposableBag`——循环音效随宿主 View/Context 销毁自动停，与框架生命周期心智统一。
- **handle 陈旧安全**：音效播完 / 被停后 handle 自动失效，之后的 `Stop` / `IsPlaying` 是安全 no-op / false（voice 复用靠自增 id 区分，旧 handle 不会误停新声音）；`default(AudioHandle)` 同样安全。
- **组是开放字符串，框架只预置 `AudioGroups.Music` / `AudioGroups.Sfx` 两个常量**：业务加 "Voice" / "Ambience" 就是自己定义常量（与存储 key 同一「常量管理字符串契约」姿势）。组不需要预注册，未设置过音量的组默认 1。

### 2. 音量模型：主 × 组 × 单次 三级乘法，纯代码实现，刻意不上 AudioMixer

- 每个声音的实际音量 = `MasterVolume × GroupVolume(组) × volume(单次) × 淡变系数`，全部 [0,1] clamp，线性幅度直接写 `AudioSource.volume`。
- `MasterVolume` / `SetGroupVolume` **即时生效到在播声音**——设置页拖滑条实时反馈，不用等下一次播放。
- **刻意不上 AudioMixer**：mixer 是资产级配置（exposed parameter 是字符串契约 + dB 换算样板），默认路径「零配置开箱即用」比 mixer 路由更符合框架姿势；需要效果链 / 闪避 / snapshot 的项目直接换 `IAudioUtility` 实现——接口本身就是接缝（见 §3）。
- **音量持久化归业务**：设置数据是业务的 `SettingsData`（`IStorageUtility` 整存整取），启动时读出来逐组 `SetGroupVolume` 回灌。框架不悄悄写盘（存哪些组、什么时机存是业务决策）。

### 3. 实现载体：零依赖进内核 `Core/Audio/`，不做 provider 层

- `AudioUtility`（纯 C#，`IDisposable`）：惰性创建一个 DontDestroyOnLoad 的 `[Game.Framework Audio]` 根节点，池化的 AudioSource 全挂它下面（保持激活——要出声，与对象池「停用停放」相反）；Dispose 销毁根节点、全部停声。
- `MonoAudioUtility`：`MonoUtilityBase` + 组合转发（同 `MonoPoolUtility` / `MonoStorageUtility` 模式），Inspector 配初始主音量 / 各组音量 + 运行时诊断（当前音乐、活动声音数）。
- 注册三选一同池/存储：`RegisterOwned`（随 Context 释放，推荐）/ `RegisterValue`（全局）/ Mono 版（Inspector + 场景生命周期）。
- **刻意不做 `IAudioProvider` 层**：`IAudioUtility` 本身就是 port，Unity `AudioSource` 实现就是 adapter。FMOD / Wwise 接入是「接口的第二实现」，不是「实现下面的第二 provider」——只有一个实现就预设 provider 层是纯抽象税（对齐 roadmap「第二实现才能验证抽象」的判断）。存储拆 provider/serializer 是因为「介质 × 格式」两轴独立可组合，音频没有这样的正交轴。
- **接缝的完整性靠 `IAudioHandleOwner`**：`AudioHandle` 的 owner 是公开接口 `IAudioHandleOwner`（`IsVoiceActive` / `StopVoice` 两成员）而非内核具体类，构造函数公开——第三方实现（FMOD / Wwise 适配类）实现该接口即可签发业务代码照常使用的句柄。没有这一步，"接口即接缝"只对无返回值成员成立，`PlaySfx` 的返回值会把接缝焊死在内核实现上。实现约定：陈旧 id 必须安全 no-op（业务丢着不管的旧句柄是常态）。

### 4. 池化与自动回收：复用 `ObjectPool<T>` 原语，不依赖 IPoolUtility 服务

- 内部 `Voice` 类 = AudioSource + 播放态（组 / 基础音量 / 淡变系数 / 自增 id），用 `Core/Pool` 现成的 `ObjectPool<Voice>` 池化——roadmap 说的「吃现成对象池」吃的是**池原语（类）**，不是池**服务（IPoolUtility）**：拉服务会引入注册顺序耦合与工具间依赖（`PoolUtility` 不拉 `IAssetUtility` 同一先例）。
- **一次性音效自动回收**：一个中央驱动循环（UniTask 每帧扫描活动 voice，无活动自动停跑）把 `isPlaying == false` 的非循环 voice 归还池。回收判定叠加「`AudioListener.pause` 期间不回收」——全局暂停不该把暂停中的声音当播完收走。
- **同时发声数不设上限**：Unity 自带 voice 虚拟化（超出可听上限自动静音低优先级声音），框架不重复造限流。

### 5. 淡入淡出：per-voice UniTask 驱动，unscaled 时间

- 音乐切换 = 旧 voice 独立淡出后归还 + 新 voice 独立淡入——没有「双源 ping-pong」状态机，快速连切时每个旧 voice 各自完成淡出，互不打断。
- 每次淡变持有独立 owner；完成、被新淡变接管或 voice 归还时只清理自己的 owner，且 continuation 每次恢复都重新验明身份，避免迟到任务修改或归还已经复用的 voice。淡入完成即释放 owner；若非循环短曲先自然结束，中央驱动会优先归还并取消尚未完成的淡入。
- 用 `Time.unscaledDeltaTime` 推进：游戏暂停（timeScale = 0）时音乐淡变照常工作（暂停菜单切 BGM 是常见场景）。
- `fadeSeconds = 0` = 立即切换/停止（测试与「不要过渡」场景都需要确定性路径）。

### 6. 失败与 Dispose 语义：宽容（学池，不学存储）

| 情形 | 行为 |
|---|---|
| clip 为 null | 抛 `ArgumentNullException`（代码写错了） |
| Dispose 后调用 | Editor/Dev `Log.Error` + 安全 no-op（返回失效 handle）——丢一声音效不致命，不值得炸游戏 |
| 停一个已结束/已停的 handle | 安全 no-op（陈旧 handle 是常态，不是错误） |
| 场景无 AudioListener / batchmode 无音频设备 | 不出声但 API 全部可用（Unity 自身行为，框架不加判定） |

### 7. 刻意不做

- **挂点 / 跟随式 3D 音效**：持续 3D 音源本来就该是对象身上的 `AudioSource` 组件（引擎组件可跨层，Inspector 可调、随对象销毁），框架不抢这活。`PlaySfxAt` 只覆盖「一次性位置音效」（爆炸 / 命中——对象可能已销毁但声音要播完）。
- **按 location 加载的重载**：clip 经资源系统 `Bag.Load<AudioClip>(location)` 取到再传入（`PoolUtility` 不拉 `IAssetUtility` 同一先例；加载与播放的生命周期本就该分开管）。
- **播放列表 / 随机变体 / pitch 抖动**：业务侧一行参数组合的事（`PlaySfx(clips[Random.Range(...)], pitch: 1f + Random.Range(-0.05f, 0.05f))`）。
- **全局暂停包装**：`AudioListener.pause` 就是 Unity 的全局开关，包一层没有增益；框架只保证暂停期间不误回收 voice。
- **音量 dB 曲线**：线性幅度对滑条场景够用；要专业响度曲线的项目换实现。

## Consequences

**得到：**

- BGM 切换（交叉淡变）、一次性/循环音效、设置页三条音量滑条（主 / 音乐 / 音效）开箱即用，零场景配置（`RegisterOwned(new AudioUtility(), ...)` 一行）。
- 循环音效进 Bag 随宿主自动停——音频生命周期并入框架统一心智，业务不用记「哪里还有环境声没停」。
- 音量设置与存储模块天然组合（demo 演示 `SettingsData` 持久化回灌），两个模块互相成为对方的活样板。

**代价 / 权衡：**

- 中央驱动每帧扫一遍活动 voice 列表——数量级 <32，成本可忽略；换来一次性音效零手动管理。
- 线性音量不如 dB 曲线符合听感（滑条中段感知偏快）——大多数游戏可接受，追求专业响度的项目换实现。
- 不上 AudioMixer 意味着没有效果链 / 闪避——这是「全局播放服务」与「混音工程」的边界，后者本就该按项目定制。

**风险：**

- batchmode（CI 命令行跑测试）下 Unity 无音频设备，「播放推进」类断言不可靠——相关测试在 `Application.isBatchMode` 下 `Assert.Ignore`，编辑器内跑全量（音量数学 / 池化 / handle 语义等结构性测试不受影响）。

## 2026-08-26 修订（异步驱动诊断进入日志 Seam）

- 淡变任务和中央 voice 回收驱动的非取消异常通过 `Game.Framework.Logging.Log` 记录，保留原始 exception；可用的 `AudioSource` 或音频根节点作为 Unity context，便于 Console 定位，也让文件/遥测 sink 获得同一证据。
- Dispose 后误用仍只在 Editor/Development 报错并宽容 no-op；迁移日志入口不改变发布版行为。category 固定为 `AudioUtility`，默认 Console 前缀保持不变。
- 没有为此增加新的 Audio provider 或公共 Interface。`IAudioUtility` 已是足够深的 Seam；诊断只属于现有 Implementation 的生命周期职责。

## 2026-08-27 修订（Voice owner 终态闭环）

- `PlayMusic(loop:false)` 自然结束后由中央驱动归还 voice，使 `CurrentMusic`、活动计数和 clip 引用恢复空闲终态；循环音乐仍只由 `PlayMusic` / `StopMusic` 显式切换。
- 淡变 CTS 改为显式 owner：成功完成也会释放，仅当前 owner 能清理自己的槽；旧任务迟到恢复不会误伤接管它的新淡变。淡出异常仍归还已经交出所有权的 voice，避免旧声音永久滞留。
