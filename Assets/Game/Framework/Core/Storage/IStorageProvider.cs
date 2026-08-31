using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Storage
{
    /// <summary>
    /// 存储介质抽象：字节 ↔ 介质。<see cref="StorageUtility"/> 的两个正交扩展点之一
    /// （另一个是 <see cref="IStorageSerializer"/>：对象 ↔ 字节），换后端（SQLite / 云存档 / PlayerPrefs 桥）
    /// 只换本接口实现、不动 serializer。ADR-0021。
    ///
    /// <para>默认实现为 <see cref="FileStorageProvider"/>（persistentDataPath 下的文件 + 原子写 + 上一版备份）。
    /// 业务<b>消费</b>存储一律走 <see cref="IStorageUtility"/>，不要直接持有 provider——
    /// key 校验、序列化、FIFO 串行、备份回退都在 utility 层，绕过会失去这些保障。</para>
    /// </summary>
    /// <remarks>
    /// 实现契约（适配层保留 <c>Async</c> 后缀，同 <c>IAssetProvider</c> 惯例）：
    /// <list type="bullet">
    ///   <item>key 已由 <see cref="StorageUtility"/> 按 <see cref="StorageKey"/> 规则校验（字母/数字/-/_，<c>/</c> 分段），实现可直接当相对路径用。</item>
    ///   <item><b>写必须防损坏</b>：任何时刻介质上都要有一份完整可读的数据（临时写 + 原子替换 + 上一版备份，或介质自身的事务）。</item>
    ///   <item>读不到（不存在 / IO 失败）返回 <c>null</c>（IO 失败打日志）；<b>写失败抛异常</b>。</item>
    ///   <item>方法从主线程被调用、经全局 FIFO 串行（无并发调用）；耗时 IO 应自行切线程池，异步物理终态允许停在任意线程。
    ///         <see cref="StorageUtility"/> 会在反序列化、推进队列和完成公共 task 前恢复 Unity 主线程。</item>
    ///   <item><see cref="IDisposable.Dispose"/> 是全部已接纳方法完成后的唯一终端调用，不会与其它方法并发；实现无需自行协调排空。</item>
    /// </list>
    /// </remarks>
    public interface IStorageProvider : IDisposable
    {
        /// <summary>写入一个 key 的完整数据（防损坏语义见类型 remarks）。失败抛异常。</summary>
        UniTask WriteAsync(string key, byte[] bytes, CancellationToken ct);

        /// <summary>读主数据。不存在 / 读失败返回 null（读失败打日志）。</summary>
        UniTask<byte[]> ReadAsync(string key, CancellationToken ct);

        /// <summary>读上一版备份（主数据损坏时由 <see cref="StorageUtility"/> 调用做回退）。不存在 / 读失败返回 null。</summary>
        UniTask<byte[]> ReadBackupAsync(string key, CancellationToken ct);

        /// <summary>key 是否存在（主或备份任一可用即 true）。同步快照，不排队。</summary>
        bool Exists(string key);

        /// <summary>删除 key 的全部数据（主 + 备份 + 残留临时文件）。不存在 = no-op；失败抛异常。</summary>
        UniTask DeleteAsync(string key, CancellationToken ct);

        /// <summary>
        /// 列出全部持有已提交数据的 key（主数据或备份任一存在即包含；仅有未提交临时数据不包含），
        /// 按 <paramref name="prefix"/> 前缀过滤，null = 不过滤。结果去重、顺序稳定，无内容返回空列表。
        /// </summary>
        UniTask<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct);
    }
}
