using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Utility;

namespace Game.Framework.Storage
{
    /// <summary>
    /// 本地存储（存档 / 设置）工具。框架统一的持久化入口，业务层通过 <c>GetUtility&lt;IStorageUtility&gt;</c> 访问。
    ///
    /// <para><b>形态：类型化整存整取</b>——每类持久数据定义一个 <c>[Serializable]</c> 类
    /// （设置 = <c>SettingsData</c>、存档 = <c>PlayerSaveData</c>），整对象 <see cref="Save{T}"/> / <see cref="Load{T}"/>。
    /// 刻意不提供 <c>GetInt/SetString</c> 散装 KV（字符串 key 散落各处正是框架要消灭的东西；
    /// 真要碎片标记，Unity 的 <c>PlayerPrefs</c> 本身够薄，框架不重复包装）。ADR-0021。</para>
    ///
    /// <para><b>key 是持久契约</b>（落成文件名）：显式传、用常量管理、只增不改——改 key 等同丢弃旧数据
    /// （与资源 location 同一心智）。字符集限 <c>[A-Za-z0-9-_/]</c>，<c>/</c> 分段做槽位分组（如 <c>"save/slot1"</c>），
    /// 规则见 <see cref="StorageKey"/>。</para>
    ///
    /// <para><b>防损坏由框架兜住</b>：写路径 = 临时文件 + 原子替换 + 上一版自动备份；读路径 = 主数据损坏自动回退备份。
    /// 业务不需要（也不应该）再手写这类样板。</para>
    ///
    /// <para><b>版本迁移的姿势</b>：默认 JSON 序列化对字段增删天然宽容（新增字段旧档取默认值），绝大多数演进免迁移；
    /// 结构性改动在数据类里放 <c>int Version</c> 字段，Load 后按版本链式迁移再 Save 回写——框架不提供迁移管线（一个 switch 更直白）。</para>
    /// </summary>
    /// <remarks>
    /// <b>失败语义</b>（对齐资源系统「预期内缺失给 null、系统级失败抛异常」）：
    /// <list type="bullet">
    ///   <item><see cref="Load{T}"/>：key 不存在 → null（新玩家常态）；主数据损坏、备份可用 → 回退备份 + warning；
    ///         主备全坏 → null + error（业务当新档处理，游戏能继续）。</item>
    ///   <item><see cref="Save{T}"/>：磁盘满 / 权限 / IO 失败 → <b>抛异常</b>（数据没落盘必须让业务知道）。</item>
    ///   <item>key 非法 / data 为 null → 抛参数异常；Dispose 后调用 → 抛 <see cref="ObjectDisposedException"/>。</item>
    /// </list>
    /// <b>线程与并发</b>：公共 API 主线程调用（框架统一契约）；内部全局 FIFO 串行（同 key 竞态、读写交错天然消失），
    /// 文件 IO 切线程池不卡帧。存储操作低频，串行无感知；<b>别 fire-and-forget Save</b>——await 它，
    /// 否则紧随其后的 <see cref="Exists"/>（同步快照，不排队）可能看不到这次写入。
    /// <para><b>扩展点</b>：换介质（SQLite / 云存档）实现 <see cref="IStorageProvider"/>；换格式 / 加密（MemoryPack、包一层加解密）
    /// 实现 <see cref="IStorageSerializer"/>——都经 <see cref="StorageUtility"/> 构造函数注入，业务代码零改动。</para>
    /// </remarks>
    public interface IStorageUtility : IUtility
    {
        /// <summary>
        /// 保存一份数据到 key（整对象覆盖写，原子 + 自动备份上一版）。
        /// data 须为 <c>[Serializable]</c> 类型（默认 JSON 序列化只认 <c>[Serializable]</c> 类的<b>字段</b>，不含属性）。
        /// IO 失败抛异常；key 非法 / data 为 null 抛参数异常。
        /// </summary>
        UniTask Save<T>(string key, T data, CancellationToken ct = default) where T : class;

        /// <summary>
        /// 读取 key 的数据。无可用数据（不存在，或主备均损坏——后者已打 error）返回 <c>null</c>，业务按「无存档」处理；
        /// 主数据损坏但备份可用时自动回退（打 warning）。
        /// </summary>
        UniTask<T> Load<T>(string key, CancellationToken ct = default) where T : class;

        /// <summary>key 是否有已落盘的数据（主或备份任一存在）。同步快照、不进队列——await 完 Save 再查询才保证一致。</summary>
        bool Exists(string key);

        /// <summary>删除 key 的全部数据（含备份）。不存在 = no-op；IO 失败抛异常。</summary>
        UniTask Delete(string key, CancellationToken ct = default);

        /// <summary>
        /// 列出全部已存在的 key，可按前缀过滤（如 <c>"save/"</c> 列全部槽位；null = 全部）。
        /// 返回的 key 与 <see cref="Save{T}"/> 时传入的一致（<c>/</c> 分段、无扩展名）。
        /// </summary>
        UniTask<IReadOnlyList<string>> ListKeys(string prefix = null, CancellationToken ct = default);
    }

    /// <summary>
    /// 存储 key 的合法性规则（key 是持久契约，规则收在这一处）：
    /// 仅允许字母 / 数字 / <c>-</c> / <c>_</c>，以 <c>/</c> 分段；不允许空 key、首尾 <c>/</c>、空段（<c>//</c>）。
    /// 字符集刻意排除 <c>.</c>（杜绝 <c>..</c> 路径逃逸与扩展名混淆）与空白 / 平台敏感字符（跨平台文件名安全）。
    /// </summary>
    public static class StorageKey
    {
        /// <summary>校验 key，非法时抛 <see cref="ArgumentException"/>（消息里带具体原因）。</summary>
        public static void Validate(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Storage key 不能为空。", nameof(key));
            if (key[0] == '/' || key[key.Length - 1] == '/')
                throw new ArgumentException($"Storage key '{key}' 不能以 '/' 开头或结尾。", nameof(key));

            char prev = '\0';
            foreach (char c in key)
            {
                bool legal = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                             || c == '-' || c == '_' || c == '/';
                if (!legal)
                    throw new ArgumentException(
                        $"Storage key '{key}' 含非法字符 '{c}'——仅允许字母/数字/-/_，用 '/' 分段。", nameof(key));
                if (c == '/' && prev == '/')
                    throw new ArgumentException($"Storage key '{key}' 含空段（连续 '/'）。", nameof(key));
                prev = c;
            }
        }
    }
}
