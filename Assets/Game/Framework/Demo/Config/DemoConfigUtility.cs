using System;
using System.Collections.Generic;
using DemoCfg;
using Game.Framework;
using Luban;

namespace Game.Framework.Demo.Config
{
    /// <summary>
    /// Demo 的配置表服务：用生成的表根 <see cref="Tables"/> 闭合框架的自加载 Utility 基类，只补两件项目侧的事——
    /// 「预载哪些文件」(交还生成的清单 <see cref="LubanTableManifest"/>)、「字节怎么变表根」(Luban <see cref="ByteBuf"/>
    /// 包一层喂给 <see cref="Tables"/> 构造)。这是运行时唯一接触 Luban 类型的地方；各层经
    /// <c>GetUtility&lt;IConfigUtility&lt;Tables&gt;&gt;()</c> 或 <c>[Inject] IConfigUtility&lt;Tables&gt;</c> 直读。
    /// </summary>
    public sealed class DemoConfigUtility : MonoConfigUtilityBase<Tables>
    {
        protected override IReadOnlyList<string> TableFiles => LubanTableManifest.Files;

        protected override Tables CreateTables(Func<string, byte[]> getBytes)
            => new(file => new ByteBuf(getBytes(file)));
    }
}
