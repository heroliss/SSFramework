using System.Runtime.CompilerServices;

// 测试程序集需要白盒访问框架内部（如 GameContext.Container、MonoGameContextBase.RawContext）以构建测试场景。
[assembly: InternalsVisibleTo("Game.Framework.Test")]

// 框架自身的编辑器工具（如诊断面板）读取内核诊断数据面（FrameworkDiagnostics / Container 注册明细），
// 与 Test 同待遇的白盒通道——诊断数据面不进公共 API，业务程序集拿不到（ADR-0026）。
[assembly: InternalsVisibleTo("Game.Framework.Editor")]
