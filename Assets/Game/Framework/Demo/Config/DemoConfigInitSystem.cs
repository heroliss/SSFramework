using System;
using System.Collections.Generic;
using DemoCfg;
using Game.Framework;
using Luban;

namespace Game.Framework.Demo.Config
{
    /// <summary>
    /// Demo 的配置表初始化 System：项目侧只需补两件事——
    /// 「预载哪些文件」（直接交还生成的清单 <see cref="LubanTableManifest"/>，不手工维护）、
    /// 「字节怎么变表根」（Luban 的 <see cref="ByteBuf"/> 包一层喂给生成的 <see cref="Tables"/> 构造函数）。
    /// 这是整个运行时链路里唯一接触 Luban 类型的地方，框架基类对配置后端零感知。
    /// </summary>
    public sealed class DemoConfigInitSystem : MonoConfigInitSystemBase<Tables>
    {
        protected override IReadOnlyList<string> TableFiles => LubanTableManifest.Files;

        protected override Tables CreateTables(Func<string, byte[]> getBytes)
            => new(file => new ByteBuf(getBytes(file)));
    }
}
