using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Internal;

namespace Game.Framework.Common
{
    /// <summary>
    /// 配置表根访问扩展：保留 Context 作用域与多配置集语义，同时隐藏重复的
    /// <c>GetUtility&lt;IConfigUtility&lt;TTables&gt;&gt;()</c> 解析链。
    /// </summary>
    /// <remarks>
    /// <para><see cref="GetConfig{TTables}(ICanGetUtility)"/> 只用于调用方已经证明配置就绪的同步路径；
    /// 它不会等待、触发重试或偷偷选择全局 Context。尚未就绪时会 fail-fast，并给出对应的门禁入口。</para>
    /// <para><see cref="EnsureConfig{TTables}(ICanGetUtility,CancellationToken)"/> 用于启动流程等硬门禁；
    /// 加载提示、失败提示等持续界面仍应直接获取 <see cref="IConfigUtility{TTables}"/> 并订阅其 <c>State</c>。</para>
    /// <para>高频读取应缓存返回的表根或单张表，例如 <c>var tables = await this.EnsureConfig&lt;Tables&gt;(token)</c>
    /// 后反复使用 <c>tables.TbItem[id]</c>，避免每次查询都重新解析 Context。</para>
    /// </remarks>
    public static class ConfigAccessExtensions
    {
        /// <summary>从当前层所属的精确 Context 取得已经就绪的配置表根。</summary>
        /// <typeparam name="TTables">项目生成或实现的配置表根类型。</typeparam>
        /// <param name="self">可读取 Utility 的 Model、System、Utility 或 View。</param>
        /// <returns>当前 Context 中 <see cref="IConfigUtility{TTables}"/> 持有的稳定表根实例。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="self"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">配置 Utility 未注册、尚未就绪，或实现违反 Ready 发布契约。</exception>
        public static TTables GetConfig<TTables>(this ICanGetUtility self) where TTables : class
        {
            if (self == null) throw new ArgumentNullException(nameof(self));
            return RequireReady(self.GetUtility<IConfigUtility<TTables>>());
        }

        /// <summary>从 Command 的受限 Context 取得已经就绪的配置表根。</summary>
        /// <typeparam name="TTables">项目生成或实现的配置表根类型。</typeparam>
        /// <param name="context">Command 执行入口收到的受限 Context。</param>
        /// <returns>当前 Context 中 <see cref="IConfigUtility{TTables}"/> 持有的稳定表根实例。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">配置 Utility 未注册、尚未就绪，或实现违反 Ready 发布契约。</exception>
        public static TTables GetConfig<TTables>(this ICommandContext context) where TTables : class
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return RequireReady(context.GetUtility<IConfigUtility<TTables>>());
        }

        /// <summary>等待当前层所属 Context 的配置服务就绪，并返回同一份表根。</summary>
        /// <remarks>取消、失败、共享加载所有权与重试语义完全沿用 <see cref="IConfigUtility{TTables}.EnsureReady"/>。</remarks>
        public static UniTask<TTables> EnsureConfig<TTables>(
            this ICanGetUtility self,
            CancellationToken cancellationToken = default) where TTables : class
        {
            if (self == null) throw new ArgumentNullException(nameof(self));
            return self.GetUtility<IConfigUtility<TTables>>().EnsureReady(cancellationToken);
        }

        /// <summary>等待 Command 当前受限 Context 的配置服务就绪，并返回同一份表根。</summary>
        /// <remarks>取消、失败、共享加载所有权与重试语义完全沿用 <see cref="IConfigUtility{TTables}.EnsureReady"/>。</remarks>
        public static UniTask<TTables> EnsureConfig<TTables>(
            this ICommandContext context,
            CancellationToken cancellationToken = default) where TTables : class
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return context.GetUtility<IConfigUtility<TTables>>().EnsureReady(cancellationToken);
        }

        private static TTables RequireReady<TTables>(IConfigUtility<TTables> config) where TTables : class
        {
            var state = config.State.CurrentValue;
            var tables = config.Tables;
            if (state == ConfigInitState.Ready && tables != null) return tables;

            string tableType = typeof(TTables).FullName ?? typeof(TTables).Name;
            if (state == ConfigInitState.Ready)
            {
                throw new InvalidOperationException(
                    $"配置服务“{config.GetType().FullName}”已发布 Ready，但表根“{tableType}”仍为空；" +
                    "该实现违反 IConfigUtility 的发布契约。请先写入 Tables，再发布 Ready。");
            }

            throw new InvalidOperationException(
                $"配置表根“{tableType}”尚不可同步读取（当前状态：{state}）。" +
                $"请先 await EnsureConfig<{typeof(TTables).Name}>(token)；" +
                "若界面需要持续展示加载或失败状态，请获取 IConfigUtility<TTables> 并订阅 State。");
        }
    }
}
