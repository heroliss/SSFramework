using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Internal;
using Game.Framework.Logging;
using UnityEngine;

namespace Game.Framework.Storage
{
    /// <summary>
    /// <see cref="IStorageUtility"/> 的默认实现：key 校验 + 序列化 + 全局 FIFO 串行 + 损坏时备份回退的编排层，
    /// 介质与格式分别委托给 <see cref="IStorageProvider"/> / <see cref="IStorageSerializer"/>（构造注入，默认文件 + JSON）。
    /// </summary>
    /// <remarks>
    /// <b>注册（按生命周期选，同 PoolUtility 三选一）：</b>纯 C# 跟随 Context 用
    /// <c>builder.RegisterOwnedUtility(new StorageUtility())</c>（随 Context Dispose 释放 provider，推荐）；
    /// 已有外部 owner 时用 <c>RegisterUtility</c>；要 Inspector 配根目录 / 跟随场景节点用 <see cref="MonoStorageUtility"/>。<br/>
    /// <b>不依赖 Context</b>（无 IGameContext 引用），可被父子 Context 共享（子级解析回退父级）。公共 API 主线程调用。<br/>
    /// <b>并发模型</b>：所有操作进全局 FIFO 队列逐个执行（同 key 竞态 / 读写交错天然消失；存储低频，串行无感知）；
    /// 队列哨兵在 finally 里必然完成，单个操作的异常只传给它自己的调用方、不毒化队列。
    /// 序列化在主线程（JsonUtility 最稳、典型存档体积耗时可忽略），文件 IO 由 provider 切线程池；Provider 可在任意线程
    /// 物理完成，但本类会在反序列化、推进 FIFO 和交付公共终态前恢复 Unity 主线程。<br/>
    /// <b>Dispose 后不可再用</b>（抛 <see cref="ObjectDisposedException"/>——写丢失必须 fail-fast，不学池的宽容警告）；
    /// Dispose 会立即发布逻辑终态、拒绝新请求，但不会同步等待尚未完成的 FIFO：此前已入队的操作仍会排空，
    /// provider 作为队列的最后一步释放。因此物理释放可能延后，但绝不会与已入队操作并发，也不会让排队操作访问已释放 provider。
    /// 队列已空时 provider.Dispose 可能在当前调用栈内完成，所以 Adapter 的同步释放实现仍应保持短小。
    /// </remarks>
    public sealed class StorageUtility : IStorageUtility, IDisposable
    {
        private readonly IStorageProvider _provider;
        private readonly IStorageSerializer _serializer;

        // 全局 FIFO 的队尾。主线程独占访问（公共 API 契约），无锁。
        private UniTask _tail = UniTask.CompletedTask;
        private bool _disposed;

        /// <summary>
        /// 创建存储编排实例。传入的 provider 在构造成功后由本实例接管，并在已接纳的 FIFO 操作排空后释放；
        /// serializer 只借用，不要求实现释放协议。
        /// </summary>
        /// <param name="provider">存储介质；null = 默认 <see cref="FileStorageProvider"/>（persistentDataPath/storage）。</param>
        /// <param name="serializer">序列化格式；null = 默认 <see cref="JsonUtilityStorageSerializer"/>（UTF-8 JSON）。</param>
        public StorageUtility(IStorageProvider provider = null, IStorageSerializer serializer = null)
        {
            _provider = provider ?? new FileStorageProvider(Path.Combine(Application.persistentDataPath, "storage"));
            _serializer = serializer ?? new JsonUtilityStorageSerializer();
        }

        /// <inheritdoc />
        public async UniTask Save<T>(string key, T data, CancellationToken ct = default) where T : class
        {
            ThrowIfDisposed();
            StorageKey.Validate(key);
            if (data == null) throw new ArgumentNullException(nameof(data), $"Save('{key}') 的数据不能为 null——删除数据用 Delete。");

            byte[] bytes = _serializer.Serialize(data); // 主线程序列化（ADR-0021 §6），失败在入队前就抛给调用方
            // serializer 是可替换的同步扩展点；若其回调重入 Context.Dispose，不能在 terminal 之后再把 Write 塞进队列。
            ThrowIfDisposed();
            if (bytes == null)
                throw SerializerContractViolation(
                    $"Serialize<{typeof(T).Name}> 返回了 null；无内容也必须返回 Array.Empty<byte>()");
            await Enqueue(() => _provider.WriteAsync(key, bytes, ct));
        }

        /// <inheritdoc />
        public async UniTask<T> Load<T>(string key, CancellationToken ct = default) where T : class
        {
            ThrowIfDisposed();
            StorageKey.Validate(key);
            return await Enqueue(async () =>
            {
                byte[] main = await MainThreadGuard.AwaitOnMainThread(
                    _provider.ReadAsync(key, ct));
                var data = TryDeserialize<T>(main, key, "主文件");
                if (data != null) return data;

                byte[] bak = await MainThreadGuard.AwaitOnMainThread(
                    _provider.ReadBackupAsync(key, ct));
                data = TryDeserialize<T>(bak, key, "备份");
                if (data != null)
                {
                    Log.Warning($"'{key}' 主文件不可用，已回退上一版备份（下次 Save 会重建主文件）。", "StorageUtility");
                    return data;
                }

                // 主备都没有可用数据。曾经有过内容（读到了字节却解析不出）= 损坏，必须留痕；全都不存在 = 新玩家常态，静默。
                if (main != null || bak != null)
                    Log.Error($"'{key}' 主文件与备份均无法反序列化——按无存档处理（返回 null）。", category: "StorageUtility");
                return null;
            });
        }

        /// <inheritdoc />
        public bool Exists(string key)
        {
            ThrowIfDisposed();
            StorageKey.Validate(key);
            return _provider.Exists(key);
        }

        /// <inheritdoc />
        public async UniTask Delete(string key, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            StorageKey.Validate(key);
            await Enqueue(() => _provider.DeleteAsync(key, ct));
        }

        /// <inheritdoc />
        public async UniTask<IReadOnlyList<string>> ListKeys(string prefix = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            // prefix 只是过滤条件、不是持久契约，不做 key 校验（非法字符只会匹配不到任何 key）。
            return await Enqueue(() => _provider.ListKeysAsync(prefix, ct));
        }

        /// <summary>
        /// 立即拒绝后续调用，并在已接纳的 FIFO 操作全部完成后释放 provider。
        /// 本方法不为等待尚未完成的 FIFO 而同步阻塞；队列已空时同步 provider.Dispose 可内联执行。
        /// 物理释放失败会记录 Error，因为它可能延后发生，无法可靠地同步交还调用方。
        /// </summary>
        public void Dispose()
        {
            MainThreadGuard.AssertMainThread(nameof(StorageUtility));
            if (_disposed) return;
            _disposed = true;
            EnqueueProviderDisposal().Forget(e =>
                Log.Error("存储 FIFO terminal 意外停止。", e, nameof(StorageUtility)));
        }

        private void ThrowIfDisposed()
        {
            MainThreadGuard.AssertMainThread(nameof(StorageUtility));
            if (_disposed)
                throw new ObjectDisposedException(nameof(StorageUtility), "存储已随 Context 释放——检查是否持有了过期引用。");
        }

        // ── 全局 FIFO ─────────────────────────────────────────────────────────
        // 每个操作先等前驱的哨兵完成再执行；哨兵在 finally 里必然 SetResult，
        // 所以操作抛异常 / 被取消都只影响它自己的 await 方，队列继续前进（不毒化）。
        // 主线程独占保证「读 _tail + 换 _tail」原子，无需加锁。

        private async UniTask Enqueue(Func<UniTask> op)
        {
            UniTask prev = _tail;
            var gate = new UniTaskCompletionSource();
            _tail = gate.Task;
            await MainThreadGuard.AwaitOnMainThread(prev);
            try { await MainThreadGuard.AwaitOnMainThread(op()); }
            finally { gate.TrySetResult(); }
        }

        private async UniTask<TResult> Enqueue<TResult>(Func<UniTask<TResult>> op)
        {
            UniTask prev = _tail;
            var gate = new UniTaskCompletionSource();
            _tail = gate.Task;
            await MainThreadGuard.AwaitOnMainThread(prev);
            try { return await MainThreadGuard.AwaitOnMainThread(op()); }
            finally { gate.TrySetResult(); }
        }

        // Dispose 是 FIFO 的 terminal：逻辑终态已由 _disposed 同步发布，物理资源则等全部已接纳操作完成后再释放。
        // provider.Dispose 的异常必须在这里观察；延后的 fire-and-forget 异常既不能丢，也不能反向打断 Context Dispose。
        private UniTask EnqueueProviderDisposal()
        {
            return Enqueue(() =>
            {
                try
                {
                    _provider.Dispose();
                }
                catch (Exception e)
                {
                    Log.Error("存储 Provider 在 FIFO 队列排空后释放失败。", e, nameof(StorageUtility));
                }

                return UniTask.CompletedTask;
            });
        }

        // 反序列化一份字节：bytes 为 null（不存在 / 读失败）直接返回 null；解析失败或 Serializer 违规返回 null 根时
        // 打 warning 并按不可用处理，最终语义（回退 / 报错）由 Load 决定。
        private T TryDeserialize<T>(byte[] bytes, string key, string label) where T : class
        {
            if (bytes == null) return null;
            try
            {
                T value = _serializer.Deserialize<T>(bytes);
                if (value == null)
                    throw SerializerContractViolation(
                        $"Deserialize<{typeof(T).Name}> 对已提交的存储字节返回了 null");
                return value;
            }
            catch (Exception e)
            {
                Log.Write(
                    LogLevel.Warning,
                    $"'{key}' {label}反序列化失败，已按不可用处理。",
                    category: nameof(StorageUtility),
                    exception: e);
                return null;
            }
        }

        private InvalidOperationException SerializerContractViolation(string detail) =>
            new InvalidOperationException(
                $"存储序列化器 {_serializer.GetType().FullName} 违反 IStorageSerializer 契约：{detail}。");
    }
}
