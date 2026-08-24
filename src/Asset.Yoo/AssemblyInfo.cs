using System.Runtime.CompilerServices;

// Yoo Adapter 的 EditMode 契约测试需要直接构造进程级包操作协调器；不把该内部实现扩成业务公共 API。
[assembly: InternalsVisibleTo("Game.Framework.Asset.Yoo.Tests")]
