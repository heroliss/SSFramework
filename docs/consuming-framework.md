# 接入与升级 SSFramework

这份文档面向任何需要在 Unity 工程中使用 `com.liss.ssframework` 的团队。它只描述包本身的公开接入契约，不依赖某个游戏、教程工程或工作区布局。

## 选择安装方式

### Unity Package Manager：Git URL

在 Package Manager 中选择 **Add package from git URL**：

```text
https://github.com/heroliss/SSFramework.git
```

稳定构建建议固定到 tag 或 commit：

```text
https://github.com/heroliss/SSFramework.git#v0.1.0
```

Git URL 的 `#revision` 是可复现边界；不要在需要稳定验证的项目中隐式跟随未经审查的分支头。

### 本地开发包

调试 Framework 源码时，可以通过 Package Manager 的本地路径引用工作副本。消费方只需要重新导入包即可看到修改；准备提交前仍应切换回明确的 tag 或 commit，避免把本地未提交状态当成版本依赖。

## 版本升级流程

1. 阅读目标版本的 changelog、公共 API 变更和迁移说明。
2. 在 Framework 仓库完成修改、包内测试和必要的构建验证。
3. 发布一个 tag，或选定一个已经推送的 commit SHA。
4. 在 Unity 工程中更新 Package Manager 的 Git revision，或更新包锁定文件中的版本。
5. 运行消费方编译、受影响的测试和真实运行路径。
6. 把使用的 tag/SHA 与验证结果记录在消费方自己的兼容性文档中。

Framework 新提交不会自动改写使用它的 Unity 工程；这是为了让每个工程的验证结果可复现。批量升级可以由团队自己的脚本或 CI 编排，但每个消费方仍应显式记录版本和验证结果。

## 最小使用路径

1. 在场景中放置一个 `MonoGlobalContext` 子类作为根 Context。
2. 在 Context 子层级注册 Model、System、Utility 和 View，或在纯 C# 启动代码中注册它们。
3. 让 View 通过 Command 表达用户意图；让 System 修改 Model 并发送 Event。
4. 让 View 订阅只读 `ReadOnlyReactiveProperty<T>` 或 Event，并把订阅交给 `DisposableBag` 管理。

详细 API 和生命周期语义见[框架使用指南](framework-guide.md)。

## 兼容性边界

- 当前 Unity 基线：`6000.3.22f1`。
- Package ID：`com.liss.ssframework`。
- 包内第三方依赖由 `package.json` 声明，Unity Package Manager 负责解析。
- 场景、Prefab、业务资产和项目级 `ProjectSettings` 不属于 Framework 包。
- 需要替换第三方实现时，应接入公开 Interface/Adapter，不修改 `Library/PackageCache/`。

## 出现问题时先检查

- Package Manager 是否解析到了预期的 revision。
- Unity 版本和包依赖是否满足 `package.json`。
- Context 是否先于 Model/System/View 完成初始化。
- 订阅、异步操作、资源句柄和对象租借是否绑定到了正确的 Bag/Context 生命周期。
- 是否把应该留在业务工程的玩法状态或场景资产放进了 Framework。
