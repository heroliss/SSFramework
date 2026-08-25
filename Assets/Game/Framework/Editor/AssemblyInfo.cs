using System.Runtime.CompilerServices;

// Editor 契约测试需要直接覆盖预检的“先验证、后写入”边界，不把验证 helper 扩成业务公共 API。
[assembly: InternalsVisibleTo("Game.Framework.Editor.Tests")]
[assembly: InternalsVisibleTo("Game.Framework.Odin.Editor")]
