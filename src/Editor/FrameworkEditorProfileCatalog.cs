using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// Framework Editor 中所有 ScriptableObject Profile 的发现快照。拥有 Profile 的可选 Module 仍保留
    /// 单例/多份、创建和业务校验语义；Catalog 只收口昂贵的 <see cref="AssetDatabase.FindAssets(string)"/>、
    /// 稳定路径加载与工程变化后的失效，不成为第二份配置注册表或通用创建器。
    /// </summary>
    public static class FrameworkEditorProfileCatalog
    {
        private static readonly Dictionary<Type, IReadOnlyList<string>> PathsByType = new();
        private static int _revision;

        static FrameworkEditorProfileCatalog()
        {
            EditorApplication.projectChanged += Invalidate;
        }

        /// <summary>工程资产变化使已发现路径失效时触发；显式刷新完成不会再次触发，避免窗口自刷新循环。</summary>
        public static event Action Invalidated;

        /// <summary>每次失效或显式刷新递增，用于测试和诊断快照是否已更新。</summary>
        public static int Revision => _revision;

        /// <summary>
        /// 返回指定 Profile 类型按资产路径排序的会话快照。相同 revision 内重复调用复用同一只读列表，
        /// 不重复执行工程级查找。
        /// </summary>
        public static IReadOnlyList<string> GetPaths(Type profileType)
        {
            ValidateProfileType(profileType);
            if (PathsByType.TryGetValue(profileType, out IReadOnlyList<string> cached)) return cached;
            IReadOnlyList<string> discovered = Discover(profileType);
            PathsByType[profileType] = discovered;
            return discovered;
        }

        /// <summary>只读取已存在的快照；窗口可先画轻量壳，再显式调度 <see cref="Refresh"/>。</summary>
        public static bool TryGetPaths(Type profileType, out IReadOnlyList<string> paths)
        {
            ValidateProfileType(profileType);
            return PathsByType.TryGetValue(profileType, out paths);
        }

        /// <summary>
        /// 按缓存路径加载全部 Profile；只复用发现结果，资产字段始终由 AssetDatabase 当前对象提供。
        /// 若非空快照中的路径已经无法加载，说明资产刚移动或删除而 <c>projectChanged</c> 尚未送达；
        /// 此时只刷新当前类型并重试一次，避免把确定陈旧的路径误报成“配置缺失”。
        /// </summary>
        public static IReadOnlyList<T> ResolveAll<T>() where T : ScriptableObject
        {
            IReadOnlyList<string> paths = GetPaths(typeof(T));
            IReadOnlyList<T> profiles = LoadAll<T>(paths);
            if (paths.Count == 0 || profiles.Count == paths.Count) return profiles;

            Refresh(typeof(T));
            return LoadAll<T>(GetPaths(typeof(T)));
        }

        /// <summary>
        /// 加载稳定排序后的首个 Profile，并把本次使用的路径快照一并返回给 owner Module 处理单例告警。
        /// 非空快照的首路径已经失效时，只刷新当前类型并重试一次；空快照不会在只读路径中重复扫描。
        /// </summary>
        public static bool TryResolveFirst<T>(out T profile, out IReadOnlyList<string> paths)
            where T : ScriptableObject
        {
            paths = GetPaths(typeof(T));
            if (TryLoadFirst(paths, out profile)) return true;
            if (paths.Count == 0) return false;

            Refresh(typeof(T));
            paths = GetPaths(typeof(T));
            return TryLoadFirst(paths, out profile);
        }

        /// <summary>
        /// 一次刷新给定类型集合，并在全部查询成功后原子替换这些类型的快照。用于配置中心的明确“重新扫描”；
        /// 未列出的其它类型缓存保持不变。
        /// </summary>
        public static void Refresh(IEnumerable<Type> profileTypes)
        {
            if (profileTypes == null) throw new ArgumentNullException(nameof(profileTypes));
            Type[] types = profileTypes
                .Where(type => type != null)
                .Distinct()
                .ToArray();
            foreach (Type type in types) ValidateProfileType(type);

            var refreshed = new Dictionary<Type, IReadOnlyList<string>>();
            foreach (Type type in types) refreshed[type] = Discover(type);
            foreach (var pair in refreshed) PathsByType[pair.Key] = pair.Value;
            _revision++;
        }

        /// <summary>只刷新一个 Profile 类型；不会由该显式调用主动丢弃其它类型快照。</summary>
        public static void Refresh(Type profileType) => Refresh(new[] { profileType });

        /// <summary>清空全部会话快照；通常由 AssetDatabase 的 projectChanged 自动调用。</summary>
        public static void Invalidate()
        {
            PathsByType.Clear();
            _revision++;
            Invalidated?.Invoke();
        }

        private static IReadOnlyList<string> Discover(Type profileType) =>
            Array.AsReadOnly(AssetDatabase.FindAssets("t:" + profileType.Name)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => AssetDatabase.LoadAssetAtPath(path, profileType) != null)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray());

        private static IReadOnlyList<T> LoadAll<T>(IEnumerable<string> paths) where T : ScriptableObject =>
            paths.Select(AssetDatabase.LoadAssetAtPath<T>)
                .Where(profile => profile != null)
                .ToArray();

        private static bool TryLoadFirst<T>(IReadOnlyList<string> paths, out T profile)
            where T : ScriptableObject
        {
            profile = paths.Count > 0 ? AssetDatabase.LoadAssetAtPath<T>(paths[0]) : null;
            return profile != null;
        }

        private static void ValidateProfileType(Type profileType)
        {
            if (profileType == null) throw new ArgumentNullException(nameof(profileType));
            if (!typeof(ScriptableObject).IsAssignableFrom(profileType) || profileType.IsAbstract)
                throw new ArgumentException(
                    "Profile Catalog 只接受具体 ScriptableObject 类型：" + profileType.FullName,
                    nameof(profileType));
        }
    }
}
