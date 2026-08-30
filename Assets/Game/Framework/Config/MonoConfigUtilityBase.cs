using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.Utility;
using R3;
using UnityEngine;

namespace Game.Framework
{
    /// <summary>
    /// 配置表服务基类：**自加载**——进游戏时按清单并行预载数据文件 → 构造表根 → 写入自身状态，对各层提供只读访问。
    ///
    /// <para>把原来「配置 Model + 配置初始化 System」两件套折叠成**一个组件**：配置加载比资源系统简单（无多包 / CDN /
    /// 下载编排），不必拆出 System；做成 Utility 又让 View 也能直读。资源系统虽更复杂，也把配置、状态机与自动初始化封装在
    /// 单个深的 <see cref="AssetUtility"/> 中，而不是按形式拆层。Utility 能取
    /// 其他 Utility（<c>IUtility : ICanGetUtility</c>），故能自己用 <c>IAssetUtility</c> + <c>Bag.LoadBytes</c> 加载。</para>
    ///
    /// <para>项目侧用具体表根类型闭合泛型并补两件事即可成为可挂场景的组件：「预载哪些文件」(<see cref="TableFiles"/>，
    /// 直接交还生成的清单)、「字节怎么变表根」(<see cref="CreateTables"/>)。这是运行时唯一接触配置后端（Luban 等）的地方。</para>
    ///
    /// <para><b>为什么先预载再构造</b>：生成的表根构造函数通常同步逐表要字节，而资源加载异步——先按 <see cref="TableFiles"/>
    /// 并行读进内存，再用同步取字节委托一次性构造。数据文件按普通资源收集（<c>.bytes</c> 即 TextAsset），经
    /// <c>Bag.LoadBytes</c> 直读（拷出即释放句柄），本类不持任何资源句柄；加载会等资源系统就绪，业务无需关心时序。
    /// 失败置 <see cref="ConfigInitState.Failed"/> 并落日志；命令式调用方可经 <see cref="EnsureReady"/> 取得表根或重新收到原始异常。</para>
    ///
    /// <para><b>生命周期：</b>Context 取消会终止共享加载及未完成的 <see cref="EnsureReady"/> 等待；
    /// 配置组件销毁还会 Dispose <see cref="State"/> 使订阅正常完结。取消回调即使抛异常也不会截断状态流、
    /// Bag 与 Context 反注册的后续清理。Idle 时禁用组件或 GameObject 会让 <see cref="EnsureReady"/> 立即给出可操作错误，
    /// 而不是等待一个 Unity 不会调用的 <c>Start</c>；owner token 未取消时下游自发抛出的取消异常属于失败。</para>
    /// </summary>
    /// <typeparam name="TTables">配置表根类型。</typeparam>
    [DefaultExecutionOrder(-400)]
    public abstract class MonoConfigUtilityBase<TTables> : MonoUtilityBase, IConfigUtility<TTables> where TTables : class
    {
        [SerializeField, Tooltip("配置数据所在的资源包名；留空 = 默认包。\n数据文件须在该包的收集范围内（普通资源收集即可，按文件名寻址）。")]
        private string _packageName = "";

        [SerializeField, Tooltip("加载前先显式初始化该资源包（针对未开「自动初始化」的包——框架对 Idle 包直接 Load 会 fail-fast 抛错）。\n" +
                                 "默认关闭：合规启动（隐私同意前零联网）等场景由业务自己决定初始化时机；包本身开了自动初始化时无需勾选。")]
        private bool _initializePackageIfIdle;

        // 内部可写、对外只读：各层只能读 Tables/State，写入只发生在本类的加载流程里。
        // Tables 是一次性加载的只读数据、之后不变，普通字段即可；State 有 Idle→Loading→Ready/Failed 转换，用 RP 供订阅。
        private TTables _tables;
        private readonly RP<ConfigInitState> _state = new(ConfigInitState.Idle);
        // completion 只表达“共享尝试已到终态”，不直接承载异常：即使无人等待，也不会制造未观察的 UniTask 异常。
        // 失败通过 ExceptionDispatchInfo 单独保存，让后来才调用 EnsureReady 的一方仍收到原始异常与原始堆栈。
        private readonly UniTaskCompletionSource _completion = new();
        private ExceptionDispatchInfo _failure;

        public TTables Tables => _tables;
        public ReadOnlyReactiveProperty<ConfigInitState> State => _state;

        /// <inheritdoc />
        public async UniTask<TTables> EnsureReady(CancellationToken cancellationToken = default)
        {
            var state = _state.CurrentValue;
            if (state == ConfigInitState.Ready) return _tables;
            if (state == ConfigInitState.Failed) return RethrowFailure();

            // Start 尚未获得执行机会但组件有效时，等待同一共享 completion 是合法的启动门禁；
            // disabled / inactive 的 MonoBehaviour 则根本不会收到 Start，继续等待只会制造无终态挂起。
            // 这里只报告可修复的场景接线问题，不写 Failed / completion，重新启用后仍能由 Start 发起首次加载。
            if (state == ConfigInitState.Idle && !isActiveAndEnabled)
            {
                string reason = !gameObject.activeInHierarchy
                    ? "所在 GameObject 处于未激活（inactive）状态"
                    : "组件处于禁用（disabled）状态";
                throw new InvalidOperationException(
                    $"配置服务“{GetType().Name}”仍为 Idle，且{reason}；Unity 不会调用它的 Start，" +
                    "EnsureReady 无法等到加载开始。请先激活 GameObject 并启用组件，再调用 EnsureReady / EnsureConfig。");
            }

            // 调用者只挂到共享完成信号上；AttachExternalCancellation 不会把短命 token 传给真正的资源加载。
            // 因而界面切走等局部取消只让该 waiter 离开，组件 / Context 仍拥有同一次物理加载。
            if (cancellationToken.CanBeCanceled)
                await _completion.Task.AttachExternalCancellation(cancellationToken);
            else
                await _completion.Task;

            if (_state.CurrentValue == ConfigInitState.Ready) return _tables;
            if (_state.CurrentValue == ConfigInitState.Failed) return RethrowFailure();

            // 正常路径只可能 Ready / Failed；到这里表示 owner 销毁取消了完成信号。
            throw new OperationCanceledException("配置加载因其所有者（owner）已销毁而终止。");
        }

        private CancellationTokenSource _cts;

        /// <summary>
        /// 要预载的数据文件名清单（不含扩展名的资源 location，须与表根构造时向 loader 请求的键一致）。
        /// 通常返回生成管线随代码一起产出的清单常量，避免手工维护漏表。
        /// </summary>
        protected abstract IReadOnlyList<string> TableFiles { get; }

        /// <summary>
        /// 用已预载的字节构造表根实例。<paramref name="getBytes"/> 是同步取字节委托（键 = 文件名，键不在清单内直接抛）。
        /// 只应在构造期间调用；表根不要保存它——它捕获着全部预载字节，保存会让这块内存随表根活到场景卸载。
        /// </summary>
        protected abstract TTables CreateTables(Func<string, byte[]> getBytes);

        // 在 Start 而非 Awake 加载：保证全场景 Awake（含 AssetUtility 的 Configure）已完成——
        // 否则空包名会在 AssetUtility 被 Configure 之前解析默认包名，拿到错误的包。
        // protected virtual（而非 private）：Unity 魔法方法按最派生类调用，若基类 Start 是 private，
        // 子类自己声明 Start() 会静默顶掉这里的加载且无编译警告；virtual 让子类必须写 override（并调 base.Start()）。
        protected virtual void Start()
        {
            // 只链接 Context；组件自身销毁由 OnDestroy 在 try/catch 边界内显式 Cancel。
            // Unity 的 GetCancellationTokenOnDestroy 会在 OnDestroy 之前先取消，直接链它会让 Provider 的坏回调
            // 越过本类的异常隔离、先落到 Unity 原生销毁通道。
            _cts = CancellationTokenSource.CreateLinkedTokenSource(
                ((IHasGameContext)this).Context.CancellationToken);
            LoadAsync(_cts.Token).Forget();
        }

        protected override void OnDestroy()
        {
            var owner = _cts;
            _cts = null;
            try
            {
                if (owner != null)
                {
                    try
                    {
                        owner.Cancel();
                    }
                    catch (Exception exception)
                    {
                        // Provider / 等待者可以向 owner token 注册回调；单个坏回调不能把 Mono 清理停在半途。
                        Log.Error(
                            "配置共享加载的取消回调执行失败；状态流、Bag 与 Context 反注册仍会继续。",
                            exception,
                            "ConfigUtility",
                            this);
                    }
                    finally
                    {
                        owner.Dispose();
                    }
                }
            }
            finally
            {
                _completion.TrySetCanceled();
                try
                {
                    // 显式写出 true 锁定公开长期源的完结契约，不把 R3 无参重载的默认值变成隐藏知识。
                    _state.Dispose(callOnCompleted: true);
                }
                finally
                {
                    base.OnDestroy();
                }
            }
        }

        private async UniTaskVoid LoadAsync(CancellationToken token)
        {
            _state.Value = ConfigInitState.Loading;
            try
            {
                var files = SnapshotAndValidateTableFiles();

                // 包未开自动初始化时由本服务按需触发（「不自动初始化的包须业务在用前显式 Initialize」——配置服务就是那个用包方）。
                if (_initializePackageIfIdle)
                    await this.GetUtility<IAssetUtility>().Initialize(
                        string.IsNullOrEmpty(_packageName) ? null : _packageName, token);

                // 并行直读全部数据文件字节（LoadBytes 内容拷出即释放句柄，无需托管）；加载内部会等资源系统初始化完成。
                var tasks = new UniTask<byte[]>[files.Count];
                for (int i = 0; i < files.Count; i++)
                {
                    tasks[i] = string.IsNullOrEmpty(_packageName)
                        ? Bag.LoadBytes(files[i], token)
                        : Bag.LoadBytes(_packageName, files[i], token);
                }
                var results = await UniTask.WhenAll(tasks);

                var bytesByFile = new Dictionary<string, byte[]>(files.Count);
                for (int i = 0; i < files.Count; i++)
                {
                    // 资源级加载失败 LoadBytes 返回 null（不抛）——这里必须拦下，否则 null 字节会在表构造里炸出难定位的 NRE。
                    if (results[i] == null)
                        throw new InvalidOperationException(
                            $"[ConfigUtility] 表数据“{files[i]}”加载失败（返回 null）。" +
                            "请确认数据输出目录位于该资源包的 YooAsset 收集器范围内，" +
                            "并在代码生成后重新构建资源包。");
                    bytesByFile[files[i]] = results[i];
                }

                var tables = CreateTables(file =>
                {
                    if (!bytesByFile.TryGetValue(file, out var bytes))
                        throw new KeyNotFoundException(
                            $"[ConfigUtility] 表数据“{file}”未预加载。生成的表代码请求了 TableFiles 未列出的文件——" +
                            "请重新生成配置代码与数据，使清单保持一致。");
                    return bytes;
                });

                if (tables == null)
                    throw new InvalidOperationException(
                        "[ConfigUtility] CreateTables 返回 null；项目 Adapter 必须返回完整构造的表根对象。");

                // 先写表再置 Ready：订阅 State 的一方在收到 Ready 时 Tables 一定可用。
                _tables = tables;
                _state.Value = ConfigInitState.Ready;
                _completion.TrySetResult();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 宿主或 Context 销毁导致的取消是正常退出路径，不算失败。
                _completion.TrySetCanceled(token);
            }
            catch (OperationCanceledException e)
            {
                // OCE 只有在 owner token 确实取消时才是生命周期控制流。否则通常是 Provider / Adapter
                // 错误地自发取消；若也发布成 canceled，调用方会误以为 Context 已销毁，State 还会永久停在 Loading。
                PublishFailure(new InvalidOperationException(
                    "[ConfigUtility] 配置加载下游在 owner token 未请求取消时抛出 OperationCanceledException。" +
                    "这属于 Provider / Adapter 失败；请检查其取消来源与 token 透传。",
                    e));
            }
            catch (Exception e) when (token.IsCancellationRequested)
            {
                // Bag / Provider 可以再链接 owner token。深层取消回调抛错时，某些 Adapter 会让加载 task
                // 以普通异常而非 OCE 退场；owner 意图已成立，不能因此把即将销毁的配置服务误发布为 Failed。
                Log.Error(
                    "配置共享加载在 owner 取消收尾时有下游回调抛出异常；服务仍按生命周期取消并继续清理。",
                    e,
                    "ConfigUtility",
                    this);
                _completion.TrySetCanceled(token);
            }
            catch (Exception e)
            {
                PublishFailure(e);
            }
        }

        private void PublishFailure(Exception failure)
        {
            _failure = ExceptionDispatchInfo.Capture(failure);
            _state.Value = ConfigInitState.Failed;
            Log.Error(
                $"配置服务“{GetType().Name}”加载表根失败。",
                failure,
                "ConfigUtility",
                this);
            _completion.TrySetResult();
        }

        private IReadOnlyList<string> SnapshotAndValidateTableFiles()
        {
            var source = TableFiles;
            if (source == null || source.Count == 0)
                throw new InvalidOperationException(
                    "[ConfigUtility] TableFiles 为空——请先运行配置代码生成；表清单会与表代码一同生成。");

            var files = new string[source.Count];
            var unique = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < source.Count; i++)
            {
                string file = source[i];
                if (string.IsNullOrWhiteSpace(file))
                    throw new InvalidOperationException(
                        $"[ConfigUtility] TableFiles 在索引 {i} 处包含空资源地址（location）；请重新生成表清单。");
                if (!unique.Add(file))
                    throw new InvalidOperationException(
                        $"[ConfigUtility] TableFiles 包含重复资源地址“{file}”；请重新生成表清单。");
                files[i] = file;
            }

            return files;
        }

        private TTables RethrowFailure()
        {
            if (_failure != null) _failure.Throw();
            throw new InvalidOperationException(
                "配置状态为 Failed，但没有捕获到原始失败异常。");
        }
    }
}
