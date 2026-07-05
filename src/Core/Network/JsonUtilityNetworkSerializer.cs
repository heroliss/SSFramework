using System;
using System.Text;
using UnityEngine;

namespace Game.Framework.Network
{
    /// <summary>
    /// 默认网络序列化器：UnityEngine.JsonUtility + UTF-8，零第三方依赖。
    /// 产物是紧凑（无缩进）JSON——与存储序列化器相反的取舍：网络对体积敏感、没有「文本编辑器打开调试」的需求。
    /// </summary>
    /// <remarks>
    /// JsonUtility 的已知限制（请求 / 响应 / 推送类型按此设计）：
    /// <list type="bullet">
    ///   <item>只序列化 <c>[Serializable]</c> 类型的<b>字段</b>（public 或 <c>[SerializeField]</c>），不含属性——
    ///         推送事件用 record 位置参数（生成属性）会反序列化出空数据，见 ADR-0028 §4。</item>
    ///   <item>不支持 <c>Dictionary</c> / 多态 / 可空值类型 / 顶层数组——用 <c>List</c> + 平铺字段建模；
    ///         确需这些能力换 Newtonsoft / MemoryPack 实现。</item>
    ///   <item>字段增删宽容：响应多出的字段被忽略、缺的字段取默认值——服务器 API 演进大多免改客户端。</item>
    /// </list>
    /// 忘标 <c>[Serializable]</c> 会静默序列化出空对象，Editor / Dev 构建下本类型会 LogError 帮你抓住。
    /// </remarks>
    public sealed class JsonUtilityNetworkSerializer : INetworkSerializer
    {
        public string ContentType => "application/json";

        public byte[] Serialize<T>(T data)
        {
            WarnIfNotSerializable<T>();
            string json = JsonUtility.ToJson(data, prettyPrint: false);
            return Encoding.UTF8.GetBytes(json);
        }

        public T Deserialize<T>(byte[] bytes)
        {
            WarnIfNotSerializable<T>();
            string json = Encoding.UTF8.GetString(bytes);
            // FromJson 对损坏 / 非 JSON 内容抛 ArgumentException，由 utility 折叠为 NetworkException(DeserializeError)——这里不吞。
            return JsonUtility.FromJson<T>(json);
        }

        // JsonUtility 对未标 [Serializable] 的类型不报错、只产出 "{}"——网络场景这等于发空请求 / 收空响应，开发期必须炸出来。
        private static void WarnIfNotSerializable<T>()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var t = typeof(T);
            if (!t.IsDefined(typeof(SerializableAttribute), inherit: false) && !typeof(UnityEngine.Object).IsAssignableFrom(t))
                Debug.LogError(
                    $"[JsonUtilityNetworkSerializer] 类型 {t.Name} 未标 [Serializable]——JsonUtility 会静默产出空对象（数据丢失）。给网络消息类型加上 [Serializable]。");
#endif
        }
    }
}
