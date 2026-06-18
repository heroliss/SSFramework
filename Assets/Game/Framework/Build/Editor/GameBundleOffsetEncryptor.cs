using System; // Buffer.BlockCopy；裸写 System 类型规避 Game.Framework.System 命名空间劫持（Assets/Game/AGENTS.md §6）
using System.IO;
using YooAsset; // IBundleEncryptor / BundleEncryptArgs / BundleEncryptResult

namespace Game.Framework.Build
{
    /// <summary>
    /// 偏移式资源包加密器（构建期）——在每个 bundle 文件头前**插入 N 个字节**，使产物不再以 AssetBundle 魔数开头，
    /// 挡住「直接用 AB 提取工具/AssetStudio 打开」的最低门槛。N = 偏移字节数。
    ///
    /// <para>这是运行时 <c>GameBundleOffsetDecryptor</c> 的<b>构建期对偶</b>：构建插 N 字节、运行时跳过 N 字节，两端必须用<b>同一个 N</b>
    /// （构建侧 <see cref="FrameworkAssetBuildProfile.FileOffset"/>，运行时侧 <c>AssetSystemConfigModel.FileOffset</c>）。
    /// 偏移式不改变 bundle 正文、解密近乎零成本（运行时只是带偏移加载或剥头），强度也最弱（仅防扫魔数）；
    /// 要真正的内容加密（XOR / AES 等）见 <c>docs/asset-encryption.md</c>。</para>
    ///
    /// <para><b>为什么插入的字节是确定性的</b>：YooAsset 对**加密后**的文件计算 hash/CRC 写入清单
    /// （<c>TaskUpdateBundleInfo</c>：<c>PackageSourceFilePath = EncryptedFilePath</c>），所以头部字节是产物的一部分、参与寻址。
    /// 若用随机字节填充，内容未变的包每次构建也会得到不同 hash → CDN 误判「文件变了」全量重传、破坏增量发布。
    /// 这里按 bundle 名做确定性填充（FNV-1a 派生序列）：同名同内容的包永远产出同样的头，构建可复现。</para>
    /// </summary>
    internal sealed class GameBundleOffsetEncryptor : IBundleEncryptor
    {
        private readonly int _fileOffset;

        /// <param name="fileOffset">文件头偏移字节数；须与运行时 <c>AssetSystemConfigModel.FileOffset</c> 一致。0 视为不加密。</param>
        public GameBundleOffsetEncryptor(ulong fileOffset) => _fileOffset = (int)fileOffset;

        /// <summary>
        /// 无参构造，仅为兼容 YooAsset 自带 Bundle Builder <b>窗口</b>——该窗口靠反射 + <c>Activator.CreateInstance</c>（无参）实例化加密器，
        /// 会把本类列进它的「Bundle Encryptor」下拉。<b>本框架不使用那个窗口</b>，统一构建走 <see cref="FrameworkAssetBuilder"/>
        /// （按 <see cref="FrameworkAssetBuildProfile.FileOffset"/> 用带参构造）。
        /// 提供它只为：① 万一有人在该窗口选了本类，不至于因「缺无参构造」抛 <c>MissingMethodException</c>；
        /// ② 此路径下偏移为 0＝不加密（空操作）并明确警告，避免「以为加密了其实没加密」还无声无息。
        /// 注意：<see cref="FrameworkAssetBuilder"/> 仅在 FileOffset&gt;0 时才用带参构造创建本类，因此偏移为 0 只可能来自此窗口路径。
        /// </summary>
        public GameBundleOffsetEncryptor()
        {
            _fileOffset = 0;
            UnityEngine.Debug.LogWarning(
                "[GameBundleOffsetEncryptor] 检测到经 YooAsset 自带 Bundle Builder 窗口实例化（无参构造）——本框架不使用该窗口，" +
                "本次不会加密。请改用菜单「SSFramework/资源构建」走 FrameworkAssetBuilder，由构建 profile 的 FileOffset 驱动偏移加密。");
        }

        public BundleEncryptResult Encrypt(BundleEncryptArgs args)
        {
            if (_fileOffset <= 0)
                return new BundleEncryptResult(false, null);

            byte[] source = File.ReadAllBytes(args.FilePath);
            byte[] encrypted = new byte[_fileOffset + source.Length];
            // 种子只取 bundle 名（文件名），不能用 args.FilePath 全路径：它含构建输出绝对路径（本机项目根 / CI 工作目录），
            // 用全路径会让同名同内容的包在不同机器 / 检出目录得到不同种子 → 不同头 → 不同 hash → 破坏增量发布。
            // 只取文件名才路径无关（HashName 风格下文件名即内容哈希，天然内容相关）。
            FillDeterministicHeader(encrypted, _fileOffset, Path.GetFileName(args.FilePath));
            Buffer.BlockCopy(source, 0, encrypted, _fileOffset, source.Length);
            return new BundleEncryptResult(true, encrypted);
        }

        // 用「bundle 名」派生一个确定性字节序列填充头部：FNV-1a 取种子 + LCG 滚动。
        // 不追求随机质量（偏移式本就不靠头部内容做强度），只要「同输入永远同输出」即可保证构建可复现（路径无关，见调用处注释）。
        private static void FillDeterministicHeader(byte[] buffer, int count, string seedSource)
        {
            uint state = 2166136261u; // FNV-1a offset basis
            foreach (char c in seedSource)
                state = (state ^ c) * 16777619u;

            for (int i = 0; i < count; i++)
            {
                state = state * 1664525u + 1013904223u; // Numerical Recipes LCG
                buffer[i] = (byte)(state >> 24);
            }
        }
    }
}
