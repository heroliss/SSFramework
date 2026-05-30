using System.Runtime.CompilerServices;

// 测试程序集需要白盒访问框架内部（如 GameContext.Container、MonoGameContextBase.RawContext）以构建测试场景。
[assembly: InternalsVisibleTo("Game.Framework.Test")]
