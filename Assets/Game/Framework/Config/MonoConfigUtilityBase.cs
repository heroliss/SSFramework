using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Internal;
using Game.Framework.Utility;
using R3;
using UnityEngine;

namespace Game.Framework
{
    /// <summary>
    /// 配置表服务基类：**自加载**——进游戏时按清单并行预载数据文件 → 构造表根 → 写入自身状态，对各层提供只读访问。
    ///
    /// <para>把原来「配置 Model + 配置初始化 System」两件套折叠成**一个组件**：配置加载比资源系统简单（无多包 / CDN /
    /// 下载编排），不必拆出 System；做成 Utility 又让 View 也能直读（资源系统三件套是因为加载复杂才拆）。Utility 能取
    /// 其他 Utility（<c>IUtility : ICanGetUtility</c>），故能自己用 <c>IAssetUtility</c> + <c>Bag.LoadBytes</c> 加载。</para>
    ///
    /// <para>项目侧用具体表根类型闭合泛型并补两件事即可成为可挂场景的组件：「预载哪些文件」(<see cref="TableFiles"/>，
    /// 直接交还生成的清单)、「字节怎么变表根」(<see cref="CreateTables"/>)。这是运行时唯一接触配置后端（Luban 等）的地方。</para>
    ///
    /// <para><b>为什么先预载再构造</b>：生成的表根构造函数通常同步逐表要字节，而资源加载异步——先按 <see cref="TableFiles"/>
    /// 并行读进内存，再用同步取字节委托一次性构造。数据文件按普通资源收集（<c>.bytes</c> 即 TextAsset），经
    /// <c>Bag.LoadBytes</c> 直读（拷出即释放句柄），本类不持任何资源句柄；加载会等资源系统就绪，业务无需关心时序。
    /// 失败置 <see cref="ConfigInitState.Failed"/> 并落日志，不抛到框架外。</para>
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

        public TTables Tables => _tables;
        public ReadOnlyReactiveProperty<ConfigInitState> State => _state;

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
        private void Start()
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(
                this.GetCancellationTokenOnDestroy(),
                ((IHasGameContext)this).Context.CancellationToken);
            LoadAsync(_cts.Token).Forget();
        }

        protected override void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            base.OnDestroy();
        }

        private async UniTaskVoid LoadAsync(CancellationToken token)
        {
            _state.Value = ConfigInitState.Loading;
            try
            {
                var files = TableFiles;
                if (files == null || files.Count == 0)
                    throw new InvalidOperationException(
                        "[ConfigUtility] TableFiles is empty — run the config codegen first (the manifest is generated together with the table code).");

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
                            $"[ConfigUtility] table data '{files[i]}' failed to load (null). " +
                            "Check that the data output dir is inside a YooAsset collector of this package, " +
                            "and that the asset package was rebuilt after codegen.");
                    bytesByFile[files[i]] = results[i];
                }

                var tables = CreateTables(file =>
                {
                    if (!bytesByFile.TryGetValue(file, out var bytes))
                        throw new KeyNotFoundException(
                            $"[ConfigUtility] table data '{file}' not preloaded. " +
                            "The generated tables requested a file missing from TableFiles — regenerate config code/data so the manifest matches.");
                    return bytes;
                });

                // 先写表再置 Ready：订阅 State 的一方在收到 Ready 时 Tables 一定可用。
                _tables = tables;
                _state.Value = ConfigInitState.Ready;
            }
            catch (OperationCanceledException)
            {
                // 宿主或 Context 销毁导致的取消是正常退出路径，不算失败。
            }
            catch (Exception e)
            {
                _state.Value = ConfigInitState.Failed;
                Debug.LogException(e);
            }
        }
    }
}
