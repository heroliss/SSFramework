using System;
using System.Text;
using Game.Framework.Logging;
using UnityEngine;

namespace Game.Framework.Storage
{
    /// <summary>
    /// 默认存储序列化器：UnityEngine.JsonUtility + UTF-8，零第三方依赖。
    /// 产物是带缩进的明文 JSON——<c>.sav</c> 文件可直接用文本编辑器打开调试（体积敏感 / 需混淆时换 serializer，见 ADR-0021 §5）。
    /// </summary>
    /// <remarks>
    /// JsonUtility 的已知限制（存档类型按此设计）：
    /// <list type="bullet">
    ///   <item>只序列化 <c>[Serializable]</c> 类型的<b>字段</b>（public 或 <c>[SerializeField]</c>），不含属性。</item>
    ///   <item>不支持 <c>Dictionary</c> / 多态 / 可空值类型——用 <c>List</c> + 平铺字段建模；确需这些能力换 Newtonsoft / MemoryPack 实现。</item>
    ///   <item>字段增删天然宽容：旧档缺新字段取默认值、多余字段被忽略——这正是「绝大多数存档演进免迁移」的来源。</item>
    /// </list>
    /// 忘标 <c>[Serializable]</c> 会静默序列化出空对象（数据丢失），Editor / Dev 构建下本类型会 LogError 帮你抓住。
    /// </remarks>
    public sealed class JsonUtilityStorageSerializer : IStorageSerializer
    {
        public byte[] Serialize<T>(T data) where T : class
        {
            WarnIfNotSerializable<T>();
            string json = JsonUtility.ToJson(data, prettyPrint: true);
            return Encoding.UTF8.GetBytes(json);
        }

        public T Deserialize<T>(byte[] bytes) where T : class
        {
            WarnIfNotSerializable<T>();
            string json = Encoding.UTF8.GetString(bytes);
            // FromJson 对损坏 / 非 JSON 内容抛 ArgumentException，由 StorageUtility 捕获做备份回退——这里不吞。
            return JsonUtility.FromJson<T>(json);
        }

        // JsonUtility 对未标 [Serializable] 的普通类不报错、只产出 "{}"——存档场景这等于静默丢数据，开发期必须炸出来。
        private static void WarnIfNotSerializable<T>()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var t = typeof(T);
            if (!t.IsDefined(typeof(SerializableAttribute), inherit: false) && !typeof(UnityEngine.Object).IsAssignableFrom(t))
                Log.Error(
                    $"类型 {t.Name} 未标 [Serializable]——JsonUtility 会静默序列化出空对象（数据丢失）。给存档类型加上 [Serializable]。",
                    category: "JsonUtilityStorageSerializer");
#endif
        }
    }
}
