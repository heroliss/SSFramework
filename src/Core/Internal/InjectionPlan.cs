using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Logging;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Game.Framework.Internal
{
    /// <summary>
    /// 反射注入计划：每个类型构建一次注入动作数组，后续 Apply 直接执行委托。
    /// 替代每次都走 GetFields/GetProperties/GetMethods + IsDefined 的暴力反射。
    /// </summary>
    /// <remarks>
    /// <b>注入顺序契约：</b>
    /// <list type="bullet">
    ///   <item>按"基类先于派生类"扫描（从最派生类型沿 BaseType 链上升，每层先字段、再属性、再方法）。</item>
    ///   <item>同一类内的字段/属性/方法之间相对顺序，依赖 <see cref="Type.GetFields"/> 等反射 API 的返回顺序，
    ///         <b>不在 .NET / Mono 规范保证之列</b>。业务代码不应依赖此顺序。</item>
    ///   <item>若多个 <c>[Inject]</c> 字段之间有时序耦合（例如 A 必须在 B 之前赋值），改用构造器/工厂注入，
    ///         或在 Inject 完成后的初始化方法里显式编排。</item>
    /// </list>
    /// <b>注入权限校验：</b>把"扩展方法访问权限"镜像到注入期——见 <see cref="InjectDenyReason"/>。
    /// </remarks>
    internal sealed class InjectionPlan
    {
        private const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // 静态缓存不加锁：与 Container 同一「主线程独占」契约（Inject 只发生在 Awake / Command 执行等主线程路径），
        // Editor / Development Build 下由 MainThreadGuard 兜底检测跨线程误用。
        private static readonly Dictionary<Type, InjectionPlan> _cache = new();
        private static readonly Action<object, GameContext>[] _empty = Array.Empty<Action<object, GameContext>>();

        private readonly Action<object, GameContext>[] _actions;

        private InjectionPlan(Action<object, GameContext>[] actions) => _actions = actions;

        /// <summary>
        /// 判断"把 <paramref name="fieldType"/> 注入到 <paramref name="hostType"/> 的字段/属性/参数"是否越权，
        /// 越权返回原因（用于 LogError），合法返回 <c>null</c>。
        /// </summary>
        /// <remarks>
        /// 权限模型与 <c>this.GetXxx</c> 扩展方法<b>同源</b>：容器里只会注册 Model/System/Utility 三类层 + 普通服务；
        /// 宿主能注入某层 ⟺ 它实现了对应的 <see cref="ICanGetModel"/> / <see cref="ICanGetSystem"/> / <see cref="ICanGetUtility"/>。
        /// 这把"哪层能拿哪层"的编译期约束（缺失的 ICanXxx 让 <c>this.GetModel</c> 编译不过）延伸到 <c>[Inject]</c> 反射注入这条路——
        /// 否则 View 写 <c>[Inject] SomeModel</c> 就能绕过只读约束。
        /// <list type="bullet">
        ///   <item><b>Command 例外：</b>它不实现 ICanXxx，但经 <see cref="Game.Framework.Command.ICommandContext"/> 拥有完整层访问权，故允许注入 Model/System/Utility。</item>
        ///   <item><b>GameContext/IGameContext：</b>万能门，任何宿主都禁注，应走扩展方法访问层。</item>
        ///   <item><b>View / Command / Event 等非层类型：</b>不受 ICanGetX 管辖、不在本校验内——能否注入只看容器是否注册（通常它们不注册，注入会自然 resolve 失败）。</item>
        /// </list>
        /// </remarks>
        private static string InjectDenyReason(Type fieldType, Type hostType)
        {
            // 万能门：拿到完整 Context 能绕过所有权限接口，任何宿主都禁注。
            if (fieldType == typeof(GameContext) || fieldType == typeof(IGameContext))
                return "GameContext/IGameContext is the all-access gate; reach layers via extension methods instead";

            // Command 经 ctx 有完整层访问权，等同于持有 ICanGetModel/System/Utility 全部。
            bool hostIsCommand = typeof(Game.Framework.Command.ICommandBase).IsAssignableFrom(hostType);

            // 三类层注入：宿主须具备对应的"获取"权限（与 this.GetXxx 同源）。
            if (typeof(Game.Framework.Model.IModel).IsAssignableFrom(fieldType))
                return hostIsCommand || typeof(ICanGetModel).IsAssignableFrom(hostType)
                    ? null
                    : $"'{hostType.Name}' has no GetModel permission, so it cannot inject Model '{fieldType.Name}'";
            if (typeof(Game.Framework.Systems.ISystem).IsAssignableFrom(fieldType))
                return hostIsCommand || typeof(ICanGetSystem).IsAssignableFrom(hostType)
                    ? null
                    : $"'{hostType.Name}' has no GetSystem permission, so it cannot inject System '{fieldType.Name}'";
            if (typeof(Game.Framework.Utility.IUtility).IsAssignableFrom(fieldType))
                return hostIsCommand || typeof(ICanGetUtility).IsAssignableFrom(hostType)
                    ? null
                    : $"'{hostType.Name}' has no GetUtility permission, so it cannot inject Utility '{fieldType.Name}'";

            // 其它类型（普通服务，或 View / Command / Event 等不受 ICanGetX 管辖的角色）：不在权限模型内，允许——
            // 能否真正注入只取决于容器是否注册了它（通常 View/Command/Event 不注册，会自然 TryResolve 失败 → LogWarning 兜底）。
            return null;
        }

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void ClearCacheOnDomainReload() => _cache.Clear();
#endif

        public static InjectionPlan For(Type type)
        {
            if (_cache.TryGetValue(type, out var plan)) return plan;
            MainThreadGuard.AssertMainThread(nameof(InjectionPlan));
            plan = Build(type);
            _cache[type] = plan;
            return plan;
        }

        public void Apply(object target, GameContext context)
        {
            var actions = _actions;
            for (var i = 0; i < actions.Length; i++) actions[i](target, context);
        }

        private static InjectionPlan Build(Type type)
        {
            List<Action<object, GameContext>> list = null;
            // t 沿 BaseType 链上升用于反射取成员；hostType 始终是最派生的具体类型，权限校验按它判。
            var t = type;
            while (t != null && t != typeof(object))
            {
                CollectFields(t, type, ref list);
                CollectProperties(t, type, ref list);
                CollectMethods(t, type, ref list);
                t = t.BaseType;
            }
            return new InjectionPlan(list?.ToArray() ?? _empty);
        }

        private static void CollectFields(Type t, Type hostType, ref List<Action<object, GameContext>> list)
        {
            foreach (var field in t.GetFields(Flags))
            {
                if (!field.IsDefined(typeof(InjectAttribute))) continue;
                var f = field;
                var fType = field.FieldType;
                var ownerName = t.Name;

                var deny = InjectDenyReason(fType, hostType);
                if (deny != null)
                {
                    Log.Error(
                        $"cannot inject '{fType.Name}' into field '{ownerName}.{f.Name}': {deny}.",
                        category: "Inject");
                    continue;
                }

                list ??= new List<Action<object, GameContext>>(4);
                list.Add((target, context) =>
                {
                    if (context.TryResolve(fType, out var value)) f.SetValue(target, value);
                    else Log.Warning(
                        $"Cannot resolve '{fType.Name}' for field '{ownerName}.{f.Name}'",
                        category: "Inject");
                });
            }
        }

        private static void CollectProperties(Type t, Type hostType, ref List<Action<object, GameContext>> list)
        {
            foreach (var prop in t.GetProperties(Flags))
            {
                if (!prop.IsDefined(typeof(InjectAttribute))) continue;
                if (!prop.CanWrite) continue;
                var p = prop;
                var pType = prop.PropertyType;
                var ownerName = t.Name;

                var deny = InjectDenyReason(pType, hostType);
                if (deny != null)
                {
                    Log.Error(
                        $"cannot inject '{pType.Name}' into property '{ownerName}.{p.Name}': {deny}.",
                        category: "Inject");
                    continue;
                }

                list ??= new List<Action<object, GameContext>>(4);
                list.Add((target, context) =>
                {
                    if (context.TryResolve(pType, out var value)) p.SetValue(target, value);
                    else Log.Warning(
                        $"Cannot resolve '{pType.Name}' for property '{ownerName}.{p.Name}'",
                        category: "Inject");
                });
            }
        }

        private static void CollectMethods(Type t, Type hostType, ref List<Action<object, GameContext>> list)
        {
            foreach (var method in t.GetMethods(Flags))
            {
                if (!method.IsDefined(typeof(InjectAttribute))) continue;
                var m = method;
                var pars = method.GetParameters();
                var paramTypes = new Type[pars.Length];
                for (var i = 0; i < pars.Length; i++) paramTypes[i] = pars[i].ParameterType;
                var ownerName = t.Name;

                var denied = false;
                for (var i = 0; i < paramTypes.Length; i++)
                {
                    var deny = InjectDenyReason(paramTypes[i], hostType);
                    if (deny != null)
                    {
                        Log.Error(
                            $"cannot inject '{paramTypes[i].Name}' into parameter of method '{ownerName}.{m.Name}': {deny}.",
                            category: "Inject");
                        denied = true;
                        break;
                    }
                }
                if (denied) continue;

                list ??= new List<Action<object, GameContext>>(4);
                list.Add((target, context) =>
                {
                    var args = new object[paramTypes.Length];
                    for (var i = 0; i < paramTypes.Length; i++)
                    {
                        if (!context.TryResolve(paramTypes[i], out args[i]))
                        {
                            Log.Warning(
                                $"Cannot resolve '{paramTypes[i].Name}' for method '{ownerName}.{m.Name}'",
                                category: "Inject");
                            return;
                        }
                    }
                    m.Invoke(target, args);
                });
            }
        }
    }
}
