using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Framework.Common;
using Game.Framework.Context;
using UnityEngine;

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
    /// </remarks>
    internal sealed class InjectionPlan
    {
        private const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<Type, InjectionPlan> _cache = new();
        private static readonly object _cacheLock = new();
        private static readonly Action<object, GameContext>[] _empty = Array.Empty<Action<object, GameContext>>();

        private readonly Action<object, GameContext>[] _actions;

        /// <summary>禁止直接注入的类型。GameContext/IGameContext 是万能门，应通过扩展方法访问层。</summary>
        private static bool IsForbiddenType(Type type) => type == typeof(GameContext) || type == typeof(IGameContext);

        private InjectionPlan(Action<object, GameContext>[] actions) => _actions = actions;

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void ClearCacheOnDomainReload()
        {
            lock (_cacheLock) _cache.Clear();
        }
#endif

        public static InjectionPlan For(Type type)
        {
            if (_cache.TryGetValue(type, out var plan)) return plan;
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(type, out plan)) return plan;
                plan = Build(type);
                _cache[type] = plan;
                return plan;
            }
        }

        public void Apply(object target, GameContext context)
        {
            var actions = _actions;
            for (var i = 0; i < actions.Length; i++) actions[i](target, context);
        }

        private static InjectionPlan Build(Type type)
        {
            List<Action<object, GameContext>> list = null;
            var t = type;
            while (t != null && t != typeof(object))
            {
                CollectFields(t, ref list);
                CollectProperties(t, ref list);
                CollectMethods(t, ref list);
                t = t.BaseType;
            }
            return new InjectionPlan(list?.ToArray() ?? _empty);
        }

        private static void CollectFields(Type t, ref List<Action<object, GameContext>> list)
        {
            foreach (var field in t.GetFields(Flags))
            {
                if (!field.IsDefined(typeof(InjectAttribute))) continue;
                var f = field;
                var fType = field.FieldType;
                var ownerName = t.Name;

                if (IsForbiddenType(fType))
                {
                    Debug.LogError(
                        $"[Inject] '{fType.Name}' cannot be injected into field '{ownerName}.{f.Name}'. " +
                        "Use extension methods (this.GetXxx/this.ExecuteCommand) instead.");
                    continue;
                }

                list ??= new List<Action<object, GameContext>>(4);
                list.Add((target, context) =>
                {
                    if (context.TryResolve(fType, out var value)) f.SetValue(target, value);
                    else Debug.LogWarning(
                        $"[Inject] Cannot resolve '{fType.Name}' for field '{ownerName}.{f.Name}'");
                });
            }
        }

        private static void CollectProperties(Type t, ref List<Action<object, GameContext>> list)
        {
            foreach (var prop in t.GetProperties(Flags))
            {
                if (!prop.IsDefined(typeof(InjectAttribute))) continue;
                if (!prop.CanWrite) continue;
                var p = prop;
                var pType = prop.PropertyType;
                var ownerName = t.Name;

                if (IsForbiddenType(pType))
                {
                    Debug.LogError(
                        $"[Inject] '{pType.Name}' cannot be injected into property '{ownerName}.{p.Name}'. " +
                        "Use extension methods (this.GetXxx/this.ExecuteCommand) instead.");
                    continue;
                }

                list ??= new List<Action<object, GameContext>>(4);
                list.Add((target, context) =>
                {
                    if (context.TryResolve(pType, out var value)) p.SetValue(target, value);
                    else Debug.LogWarning(
                        $"[Inject] Cannot resolve '{pType.Name}' for property '{ownerName}.{p.Name}'");
                });
            }
        }

        private static void CollectMethods(Type t, ref List<Action<object, GameContext>> list)
        {
            foreach (var method in t.GetMethods(Flags))
            {
                if (!method.IsDefined(typeof(InjectAttribute))) continue;
                var m = method;
                var pars = method.GetParameters();
                var paramTypes = new Type[pars.Length];
                for (var i = 0; i < pars.Length; i++) paramTypes[i] = pars[i].ParameterType;
                var ownerName = t.Name;

                var hasForbidden = false;
                for (var i = 0; i < paramTypes.Length; i++)
                {
                    if (IsForbiddenType(paramTypes[i]))
                    {
                        Debug.LogError(
                            $"[Inject] '{paramTypes[i].Name}' cannot be injected into parameter of method '{ownerName}.{m.Name}'. " +
                            "Use extension methods (this.GetXxx/this.ExecuteCommand) instead.");
                        hasForbidden = true;
                        break;
                    }
                }
                if (hasForbidden) continue;

                list ??= new List<Action<object, GameContext>>(4);
                list.Add((target, context) =>
                {
                    var args = new object[paramTypes.Length];
                    for (var i = 0; i < paramTypes.Length; i++)
                    {
                        if (!context.TryResolve(paramTypes[i], out args[i]))
                        {
                            Debug.LogWarning(
                                $"[Inject] Cannot resolve '{paramTypes[i].Name}' for method '{ownerName}.{m.Name}'");
                            return;
                        }
                    }
                    m.Invoke(target, args);
                });
            }
        }
    }
}
