using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Logging;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// Demo 章节目录与生命周期的唯一 owner：一次发现并校验全部 <see cref="IDemoModule"/> Adapter，
    /// 再让同一实例依次完成 Install → Initialize → Build / Teardown。
    /// </summary>
    /// <remarks>
    /// <see cref="MonoDemoContext"/> 持有本对象并负责 Install / Initialize / Dispose；
    /// <see cref="DemoShellController"/> 只负责选择章节，通过 <see cref="Activate"/> / <see cref="Deactivate"/>
    /// 切换当前展示。活动 <see cref="DemoModuleHost"/> 也由目录持有，确保离开章节时先取消 Host 异步动作，
    /// 再调用模块 Teardown。它是 Demo 内部 Module，不进入框架公共 Interface。
    /// </remarks>
    internal sealed class DemoModuleCatalog : IDisposable
    {
        private static readonly string[] CategoryOrder = { "入门", "核心", "能力", "进阶", "规划中" };
        private static readonly Regex ModuleIdPattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
        private const int MaxSummaryLength = 160;

        private enum LifecyclePhase
        {
            Discovered,
            BindingsInstalled,
            Initialized,
            Faulted,
            Disposed,
        }

        private readonly List<IDemoModule> _modules;
        private readonly IReadOnlyList<IDemoModule> _readOnlyModules;
        private LifecyclePhase _phase;
        private IDemoModule _activeModule;
        private DemoModuleHost _activeHost;

        /// <summary>不可变的已排序章节目录；返回的 Adapter 身份在整个根 Context 生命周期内保持不变。</summary>
        internal IReadOnlyList<IDemoModule> Modules => _readOnlyModules;

        /// <summary>用显式实例构造目录。生产路径由 <see cref="Discover"/> 调用；测试可直接注入记录型 Adapter。</summary>
        internal DemoModuleCatalog(IEnumerable<IDemoModule> modules)
        {
            if (modules == null) throw new ArgumentNullException(nameof(modules));
            var materializedModules = modules.ToList();
            if (materializedModules.Any(module => module == null))
                throw new ArgumentException("Demo 章节目录不能包含 null。", nameof(modules));

            _modules = materializedModules
                .OrderBy(module => CategoryIndex(module.Category))
                .ThenBy(module => module.Order)
                .ThenBy(module => module.Title)
                .ToList();
            ValidateCatalog(_modules);
            _readOnlyModules = _modules.AsReadOnly();
        }

        /// <summary>反射发现当前 Demo 程序集中的全部章节类型；每种类型在一轮 Play 中只构造一次。</summary>
        internal static DemoModuleCatalog Discover()
        {
            var contract = typeof(IDemoModule);
            var modules = typeof(DemoModuleCatalog).Assembly.GetTypes()
                .Where(type => !type.IsAbstract && contract.IsAssignableFrom(type) &&
                               type.GetConstructor(Type.EmptyTypes) != null)
                .Select(type => (IDemoModule)Activator.CreateInstance(type));
            return new DemoModuleCatalog(modules);
        }

        /// <summary>让目录里的同一批 Adapter 向根容器贡献绑定。只能在 Context Build 前调用一次。</summary>
        internal void InstallBindings(ContainerBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            RequirePhase(LifecyclePhase.Discovered, nameof(InstallBindings));
            try
            {
                foreach (var module in _modules)
                    module.InstallBindings(builder);
                _phase = LifecyclePhase.BindingsInstalled;
            }
            catch
            {
                _phase = LifecyclePhase.Faulted;
                throw;
            }
        }

        /// <summary>在容器构建完成后向同一批 Adapter 注入根 Context。只能接在 <see cref="InstallBindings"/> 后调用一次。</summary>
        internal void Initialize(IGameContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            RequirePhase(LifecyclePhase.BindingsInstalled, nameof(Initialize));
            try
            {
                foreach (var module in _modules)
                    module.Initialize(context);
                _phase = LifecyclePhase.Initialized;
            }
            catch
            {
                _phase = LifecyclePhase.Faulted;
                throw;
            }
        }

        /// <summary>
        /// 激活一个属于本目录的章节。目录创建并持有 Host；已有活动章节、未知 Adapter 或初始化前调用都会 fail-fast。
        /// Build 失败时先取消 Host、再 Teardown，并保留原始 Build 异常。
        /// </summary>
        internal void Activate(IDemoModule module, VisualElement content)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            if (content == null) throw new ArgumentNullException(nameof(content));
            RequirePhase(LifecyclePhase.Initialized, nameof(Activate));
            if (!Owns(module))
                throw new ArgumentException("只能激活当前 DemoModuleCatalog 拥有的章节实例。", nameof(module));
            if (_activeModule != null)
                throw new InvalidOperationException(
                    $"章节 '{_activeModule.Id}' 仍处于活动状态；请先 Deactivate，再激活 '{module.Id}'。");

            _activeModule = module;
            _activeHost = new DemoModuleHost(content);
            try
            {
                module.Build(_activeHost);
            }
            catch
            {
                try
                {
                    Deactivate();
                }
                catch (Exception cleanupException)
                {
                    Log.Error(
                        $"Demo chapter '{module.Id}' cleanup also failed after Build threw; the Build exception is preserved.",
                        cleanupException,
                        "DemoLifecycle");
                }
                throw;
            }
        }

        /// <summary>结束当前章节。幂等；有活动章节时始终先 Dispose Host，再调用同一 Adapter 的 Teardown。</summary>
        internal void Deactivate()
        {
            var module = _activeModule;
            var host = _activeHost;
            _activeModule = null;
            _activeHost = null;
            if (module == null) return;

            Exception hostException = null;
            Exception moduleException = null;
            try
            {
                host?.Dispose();
            }
            catch (Exception e)
            {
                hostException = e;
            }

            try
            {
                module.Teardown();
            }
            catch (Exception e)
            {
                moduleException = e;
            }

            if (hostException != null && moduleException != null)
                throw new AggregateException("Demo Host 与模块 Teardown 都发生异常。", hostException, moduleException);
            if (hostException != null) ExceptionDispatchInfo.Capture(hostException).Throw();
            if (moduleException != null) ExceptionDispatchInfo.Capture(moduleException).Throw();
        }

        /// <summary>释放目录所有权；若仍有活动章节，先按正常离开顺序收尾。重复调用无副作用。</summary>
        public void Dispose()
        {
            if (_phase == LifecyclePhase.Disposed) return;
            try
            {
                Deactivate();
            }
            finally
            {
                _phase = LifecyclePhase.Disposed;
            }
        }

        private bool Owns(IDemoModule module)
        {
            foreach (var candidate in _modules)
                if (ReferenceEquals(candidate, module)) return true;
            return false;
        }

        private void RequirePhase(LifecyclePhase expected, string operation)
        {
            if (_phase == expected) return;
            throw new InvalidOperationException(
                $"DemoModuleCatalog.{operation} 要求阶段 {expected}，当前为 {_phase}。" +
                " 正确顺序是 Discover → InstallBindings → Initialize → Activate / Deactivate → Dispose。");
        }

        private static int CategoryIndex(string category)
        {
            int index = Array.IndexOf(CategoryOrder, category);
            return index < 0 ? int.MaxValue : index;
        }

        private static void ValidateCatalog(List<IDemoModule> modules)
        {
            var problems = new List<string>();
            if (modules.Count == 0) problems.Add("未发现任何 IDemoModule 实现");

            foreach (var module in modules)
            {
                string type = module.GetType().Name;
                if (string.IsNullOrWhiteSpace(module.Id) || !ModuleIdPattern.IsMatch(module.Id))
                    problems.Add($"{type}.Id 必须是非空 kebab-case，当前为 '{module.Id}'");
                if (string.IsNullOrWhiteSpace(module.Title))
                    problems.Add($"{type}.Title 不能为空");
                if (string.IsNullOrWhiteSpace(module.Summary))
                    problems.Add($"{type}.Summary 不能为空");
                else
                {
                    if (module.Summary.Length > MaxSummaryLength)
                        problems.Add($"{type}.Summary 最多 {MaxSummaryLength} 字，当前 {module.Summary.Length} 字");

                    int sentenceCount = module.Summary.Count(character => character is '。' or '！' or '？');
                    if (sentenceCount > 2)
                        problems.Add($"{type}.Summary 最多 2 句，当前有 {sentenceCount} 个句末标点");
                }
                if (Array.IndexOf(CategoryOrder, module.Category) < 0)
                    problems.Add($"{type}.Category '{module.Category}' 不在固定分类表中");
            }

            foreach (var group in modules.GroupBy(module => module.Id).Where(group => group.Count() > 1))
                problems.Add($"Id '{group.Key}' 重复：{string.Join(", ", group.Select(module => module.GetType().Name))}");
            foreach (var group in modules.GroupBy(module => module.Title).Where(group => group.Count() > 1))
                problems.Add($"Title '「{group.Key}」' 重复：{string.Join(", ", group.Select(module => module.GetType().Name))}");
            foreach (var group in modules.GroupBy(module => (module.Category, module.Order)).Where(group => group.Count() > 1))
                problems.Add($"「{group.Key.Category}」组内 Order {group.Key.Order} 重复：" +
                             string.Join(", ", group.Select(module => module.GetType().Name)));

            if (problems.Count > 0)
                throw new InvalidOperationException(
                    "[DemoCatalog] 章节目录契约无效：\n  · " + string.Join("\n  · ", problems));
        }
    }
}
