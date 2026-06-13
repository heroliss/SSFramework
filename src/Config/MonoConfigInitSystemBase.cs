using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Internal;
using Game.Framework.System;
using UnityEngine;

namespace Game.Framework
{
    /// <summary>
    /// 配置表初始化 System 基类：进入游戏时编排「按清单并行预载数据文件 → 构造表根 → 写入配置 Model」，
    /// 镜像资源系统三段式里 <see cref="AssetInitSystem"/> 的「编排归 System」定位。
    ///
    /// <para><b>为什么要先预载再构造</b>：代码生成器产出的表根构造函数通常是同步的
    /// （按文件名逐表向 loader 要字节），而框架资源加载是异步的——所以先按
    /// <see cref="TableFiles"/> 清单把全部数据文件并行读进内存，再用同步取字节的委托调
    /// <see cref="CreateTables"/> 一次性构造。</para>
    ///
    /// <para><b>数据文件按普通资源收集</b>（<c>.bytes</c> 在 Unity 里是 TextAsset，进普通 AssetBundle），
    /// 预载经 <c>Bag.LoadBytes</c> 直读——内容拷出即释放句柄（通道按包构建类型由资源系统自动路由），
    /// 本类不持有任何资源句柄。</para>
    ///
    /// <para><b>后端无关</b>：本类不依赖任何配置库。清单来自哪里（生成的清单类）、字节如何反序列化
    /// （Luban ByteBuf / JSON / 自定义格式），都由项目侧子类在 <see cref="CreateTables"/> 里决定。</para>
    ///
    /// <para>数据文件随资源系统打包与热更（与普通资源同通道），加载会等待资源系统初始化完成，
    /// 业务无需关心时序；失败时把 Model 状态置 Failed 并输出异常日志，不抛到框架外。</para>
    /// </summary>
    /// <typeparam name="TTables">配置表根类型，与同 Context 下 <see cref="MonoConfigModelBase{TTables}"/> 的闭合类型一致。</typeparam>
    public abstract class MonoConfigInitSystemBase<TTables> : MonoSystemBase where TTables : class
    {
        [SerializeField, Tooltip("配置数据所在的资源包名；留空 = 默认包。\n数据文件须在该包的收集范围内（普通资源收集即可，按文件名寻址）。")]
        private string _packageName = "";

        [SerializeField, Tooltip("加载前先显式初始化该资源包（针对未开「自动初始化」的包——框架对 Idle 包直接 Load 会 fail-fast 抛错）。\n" +
                                 "默认关闭：合规启动（隐私同意前零联网）等场景由业务自己决定初始化时机；包本身开了自动初始化时无需勾选。")]
        private bool _initializePackageIfIdle;

        private CancellationTokenSource _cts;

        /// <summary>
        /// 要预载的数据文件名清单（不含扩展名的资源 location，须与表根构造时向 loader 请求的键一致）。
        /// 通常由生成管线随代码一起产出，子类直接返回生成的清单常量，避免手工维护漏表。
        /// </summary>
        protected abstract IReadOnlyList<string> TableFiles { get; }

        /// <summary>
        /// 用已预载的字节构造表根实例。<paramref name="getBytes"/> 是同步取字节的委托
        /// （键 = 文件名），键不在清单内会直接抛异常——typo 或清单过期在构造期就暴露。
        /// 该委托只应在构造期间调用，表根不要保存它——它捕获着全部预载字节，保存会让这块内存随表根活到场景卸载。
        /// </summary>
        protected abstract TTables CreateTables(Func<string, byte[]> getBytes);

        // 在 Start 而非 Awake 启动：AssetInitSystem 与本类同属 System 层（同 ExecutionOrder），
        // 同序 Awake 顺序不确定——若本类先跑，空包名会在 AssetUtility 被 Configure 之前解析默认包名，
        // 拿到错误的包。Start 保证全场景 Awake（含 Configure）已完成。
        private void Start()
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(
                this.GetCancellationTokenOnDestroy(),
                ((IHasGameContext)this).Context.CancellationToken);
            InitAsync(_cts.Token).Forget();
        }

        protected override void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            base.OnDestroy();
        }

        private async UniTaskVoid InitAsync(CancellationToken token)
        {
            IConfigModel<TTables> model;
            try
            {
                model = this.GetModel<IConfigModel<TTables>>();
            }
            catch (Exception e)
            {
                Debug.LogError(
                    $"[ConfigInitSystem] IConfigModel<{typeof(TTables).Name}> not found in Context. " +
                    "Place the MonoConfigModelBase subclass under the same MonoGameContextBase.");
                Debug.LogException(e);
                return;
            }

            model.State.Value = ConfigInitState.Loading;
            try
            {
                var files = TableFiles;
                if (files == null || files.Count == 0)
                    throw new InvalidOperationException(
                        "[ConfigInitSystem] TableFiles is empty — run the config codegen first (the manifest is generated together with the table code).");

                // 包未开自动初始化时由本系统按需触发（「不自动初始化的包须业务在用前显式 Initialize」——配置系统就是那个用包方）。
                // Initialize 不抛、结果回写包状态；若初始化失败，下面的加载会抛出清晰异常进入 Failed 分支。
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
                    // 资源级加载失败 LoadBytes 返回 null（不抛）——这里必须拦下，否则 null 字节会在
                    // 表构造里炸出难定位的 NRE。最常见原因：数据目录不在收集范围，或生成后没重新构建资源包。
                    if (results[i] == null)
                        throw new InvalidOperationException(
                            $"[ConfigInitSystem] table data '{files[i]}' failed to load (null). " +
                            "Check that the data output dir is inside a YooAsset collector of this package, " +
                            "and that the asset package was rebuilt after codegen.");
                    bytesByFile[files[i]] = results[i];
                }

                var tables = CreateTables(file =>
                {
                    if (!bytesByFile.TryGetValue(file, out var bytes))
                        throw new KeyNotFoundException(
                            $"[ConfigInitSystem] table data '{file}' not preloaded. " +
                            "The generated tables requested a file missing from TableFiles — regenerate config code/data so the manifest matches.");
                    return bytes;
                });

                // 先写表再置 Ready：订阅 State 的一方在收到 Ready 时 Tables 一定可用。
                model.Tables.Value = tables;
                model.State.Value = ConfigInitState.Ready;
            }
            catch (OperationCanceledException)
            {
                // 宿主或 Context 销毁导致的取消是正常退出路径，不算失败。
            }
            catch (Exception e)
            {
                model.State.Value = ConfigInitState.Failed;
                Debug.LogException(e);
            }
        }
    }
}
