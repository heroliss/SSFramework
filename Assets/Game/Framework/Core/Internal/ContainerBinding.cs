using System;
using Game.Framework.Logging;

namespace Game.Framework.Internal
{
    /// <summary>
    /// Container 的单个构建期绑定：显式区分“现成值”与“工厂”，并把 Singleton 缓存、循环检测、
    /// contract 校验和 owned 接管收在同一处。业务不可见，由 <c>ContainerBuilder</c> 创建。
    /// </summary>
    /// <remarks>
    /// 不再用 <c>stored is Func&lt;Container, object&gt;</c> 猜绑定种类：委托本身也可能是合法服务值，
    /// 以运行时类型充当 tag 会把值误执行成工厂，并让诊断状态与实际缓存状态分叉。
    /// </remarks>
    internal sealed class ContainerBinding
    {
        private readonly Func<Container, object> _factory;
        private readonly Type[] _contracts;
        private readonly bool _ownsResult;
        private object _instance;
        private bool _hasInstance;
        private bool _isCreating;

        private ContainerBinding(object instance)
        {
            _instance = instance;
            _hasInstance = true;
        }

        private ContainerBinding(Func<Container, object> factory, Type[] contracts, bool ownsResult)
        {
            _factory = factory;
            _contracts = contracts;
            _ownsResult = ownsResult;
        }

        internal bool IsFactory => _factory != null;
        internal bool IsResolved => _hasInstance;
        internal object Instance => _hasInstance ? _instance : null;

        internal static ContainerBinding ForValue(object instance) => new(instance);

        internal static ContainerBinding ForFactory(
            Func<Container, object> factory,
            Type[] contracts,
            bool ownsResult)
            => new(factory, contracts, ownsResult);

        internal object Resolve(Container container)
        {
            if (_hasInstance) return _instance;
            if (_isCreating)
                throw new InvalidOperationException(
                    $"[ContainerBuilder] 检测到契约 '{ContractNames()}' 的工厂循环解析。");

            _isCreating = true;
            object instance = null;
            bool ownershipTransferred = false;
            try
            {
                instance = _factory(container);

                // Factory 属于用户回调：它可能同步重入宿主并 Dispose Context。入口检查只能证明“调用前可用”，
                // 返回后必须再次证明事务仍可提交；否则会把新实例缓存进已经关闭的 Container。
                container.ThrowIfDisposed();
                if (instance == null)
                    throw new InvalidOperationException(
                        $"[ContainerBuilder] 契约 '{ContractNames()}' 的工厂返回了 null。");
                ValidateFactoryInstance(instance);

                if (_ownsResult)
                {
                    if (instance is not IDisposable disposable)
                        throw new InvalidOperationException(
                            $"[ContainerBuilder] 契约 '{ContractNames()}' 的托管工厂返回了 " +
                            $"未实现 IDisposable 的类型 '{instance.GetType().Name}'。");
                    container.Own(disposable);
                    ownershipTransferred = true;
                }

                _instance = instance;
                _hasInstance = true;
                return instance;
            }
            catch
            {
                // RegisterOwnedFactory 从工厂成功返回 IDisposable 起就声明 Container 所有权。若 contract 校验或
                // ownership 提交失败，本次新产物必须立即回滚；已由其它有效绑定持有的同实例继续留给 Container，
                // 不能因这条错误 alias 提前释放。普通 RegisterFactory 则从未声明所有权，这里不擅自 Dispose。
                if (_ownsResult && !ownershipTransferred && instance is IDisposable disposable &&
                    !container.HasEverOwned(disposable))
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch (Exception cleanupException)
                    {
                        Log.Error(
                            "托管工厂产物未能提交，回滚释放又抛出异常；仍保留最初的工厂结果错误。",
                            cleanupException,
                            nameof(ContainerBinding));
                    }
                }
                throw;
            }
            finally
            {
                // 构造失败不缓存，保留 Lazy 工厂既有的“下次解析可重试”语义。
                _isCreating = false;
            }
        }

        private void ValidateFactoryInstance(object instance)
        {
            for (int i = 0; i < _contracts.Length; i++)
                if (!_contracts[i].IsInstanceOfType(instance))
                    throw new InvalidOperationException(
                        $"[ContainerBuilder] 工厂结果 '{instance.GetType().Name}' 不能赋给契约 '{_contracts[i].Name}'。");
        }

        private string ContractNames()
        {
            if (_contracts.Length == 1) return _contracts[0].Name;
            var names = new string[_contracts.Length];
            for (int i = 0; i < _contracts.Length; i++) names[i] = _contracts[i].Name;
            return string.Join(", ", names);
        }
    }
}
