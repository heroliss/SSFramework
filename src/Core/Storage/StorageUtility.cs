using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Game.Framework.Storage
{
    /// <summary>
    /// <see cref="IStorageUtility"/> 的默认实现：key 校验 + 序列化 + 全局 FIFO 串行 + 损坏时备份回退的编排层，
    /// 介质与格式分别委托给 <see cref="IStorageProvider"/> / <see cref="IStorageSerializer"/>（构造注入，默认文件 + JSON）。
    /// </summary>
    /// <remarks>
    /// <b>注册（按生命周期选，同 PoolUtility 三选一）：</b>纯 C# 跟随 Context 用
    /// <c>builder.RegisterOwned(new StorageUtility(), typeof(IStorageUtility))</c>（随 Context Dispose 释放 provider，推荐）；
    /// 全局唯一、不关心释放用 <c>RegisterValue</c>；要 Inspector 配根目录 / 跟随场景节点用 <see cref="MonoStorageUtility"/>。<br/>
    /// <b>不依赖 Context</b>（无 IGameContext 引用），可被父子 Context 共享（子级解析回退父级）。公共 API 主线程调用。<br/>
    /// <b>并发模型</b>：所有操作进全局 FIFO 队列逐个执行（同 key 竞态 / 读写交错天然消失；存储低频，串行无感知）；
    /// 队列哨兵在 finally 里必然完成，单个操作的异常只传给它自己的调用方、不毒化队列。
    /// 序列化在主线程（JsonUtility 最稳、典型存档体积耗时可忽略），文件 IO 由 provider 切线程池。<br/>
    /// <b>Dispose 后不可再用</b>（抛 <see cref="ObjectDisposedException"/>——写丢失必须 fail-fast，不学池的宽容警告）；
    /// Dispose 不等待队列中未完成的操作（默认文件 provider 的原子写保证介质上始终有完整数据可回退）。
    /// </remarks>
    public sealed class StorageUtility : IStorageUtility, IDisposable
    {
        private readonly IStorageProvider _provider;
        private readonly IStorageSerializer _serializer;

        // 全局 FIFO 的队尾。主线程独占访问（公共 API 契约），无锁。
        private UniTask _tail = UniTask.CompletedTask;
        private bool _disposed;

        /// <param name="provider">存储介质；null = 默认 <see cref="FileStorageProvider"/>（persistentDataPath/storage）。</param>
        /// <param name="serializer">序列化格式；null = 默认 <see cref="JsonUtilityStorageSerializer"/>（UTF-8 JSON）。</param>
        public StorageUtility(IStorageProvider provider = null, IStorageSerializer serializer = null)
        {
            _provider = provider ?? new FileStorageProvider(Path.Combine(Application.persistentDataPath, "storage"));
            _serializer = serializer ?? new JsonUtilityStorageSerializer();
        }

        public async UniTask Save<T>(string key, T data, CancellationToken ct = default) where T : class
        {
            ThrowIfDisposed();
            StorageKey.Validate(key);
            if (data == null) throw new ArgumentNullException(nameof(data), $"Save('{key}') 的数据不能为 null——删除数据用 Delete。");

            byte[] bytes = _serializer.Serialize(data); // 主线程序列化（ADR-0021 §6），失败在入队前就抛给调用方
            await Enqueue(() => _provider.WriteAsync(key, bytes, ct));
        }

        public async UniTask<T> Load<T>(string key, CancellationToken ct = default) where T : class
        {
            ThrowIfDisposed();
            StorageKey.Validate(key);
            return await Enqueue(async () =>
            {
                byte[] main = await _provider.ReadAsync(key, ct);
                var data = TryDeserialize<T>(main, key, "主文件");
                if (data != null) return data;

                byte[] bak = await _provider.ReadBackupAsync(key, ct);
                data = TryDeserialize<T>(bak, key, "备份");
                if (data != null)
                {
                    Debug.LogWarning($"[StorageUtility] '{key}' 主文件不可用，已回退上一版备份（下次 Save 会重建主文件）。");
                    return data;
                }

                // 主备都没有可用数据。曾经有过内容（读到了字节却解析不出）= 损坏，必须留痕；全都不存在 = 新玩家常态，静默。
                if (main != null || bak != null)
                    Debug.LogError($"[StorageUtility] '{key}' 主文件与备份均无法反序列化——按无存档处理（返回 null）。");
                return null;
            });
        }

        public bool Exists(string key)
        {
            ThrowIfDisposed();
            StorageKey.Validate(key);
            return _provider.Exists(key);
        }

        public async UniTask Delete(string key, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            StorageKey.Validate(key);
            await Enqueue(() => _provider.DeleteAsync(key, ct));
        }

        public async UniTask<IReadOnlyList<string>> ListKeys(string prefix = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            // prefix 只是过滤条件、不是持久契约，不做 key 校验（非法字符只会匹配不到任何 key）。
            return await Enqueue(() => _provider.ListKeysAsync(prefix, ct));
        }

        /// <summary>释放 provider 并拒绝后续调用。不等待队列中未完成的操作（见类型 remarks）。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _provider.Dispose();
        }

        private void ThrowIfDisposed()
        {
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
            await prev;
            try { await op(); }
            finally { gate.TrySetResult(); }
        }

        private async UniTask<TResult> Enqueue<TResult>(Func<UniTask<TResult>> op)
        {
            UniTask prev = _tail;
            var gate = new UniTaskCompletionSource();
            _tail = gate.Task;
            await prev;
            try { return await op(); }
            finally { gate.TrySetResult(); }
        }

        // 反序列化一份字节：bytes 为 null（不存在 / 读失败）或解析失败 / 解析出 null 都返回 null。
        // 解析失败打 warning 留痕（损坏细节），最终语义（回退 / 报错）由 Load 决定。
        private T TryDeserialize<T>(byte[] bytes, string key, string label) where T : class
        {
            if (bytes == null) return null;
            try
            {
                return _serializer.Deserialize<T>(bytes);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[StorageUtility] '{key}' {label}反序列化失败（{e.GetType().Name}: {e.Message}）。");
                return null;
            }
        }
    }
}
