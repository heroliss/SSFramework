using System;
using UnityEngine;

namespace Game.Framework.UI.Toolkit
{
    /// <summary>
    /// 相机 → RenderTexture 的生命周期管理：持有一个外部 <see cref="Camera"/>，按需申请 / 重建等大的
    /// <see cref="RenderTexture"/> 并接到相机 <see cref="Camera.targetTexture"/>，供 <see cref="RenderTextureElement"/> 显示。
    /// 后端无关——相机拍什么（UGUI Canvas / 3D 道具预览 / 小地图）由调用方决定；本类只管纹理与相机的接线。
    /// </summary>
    /// <remarks>
    /// <see cref="Resize"/> 幂等：同尺寸不重建（省显存与 GC），尺寸变化才释放旧纹理、建新纹理并重新接相机。
    /// 用完必须 <see cref="Dispose"/>——RenderTexture 既占 GPU 显存又是 Unity 对象，两者都要释放。
    /// </remarks>
    public sealed class CameraTextureRenderer : IDisposable
    {
        private readonly Camera _camera;
        private RenderTexture _texture;

        /// <summary>当前 RenderTexture（尚未 <see cref="Resize"/> 出有效尺寸时为 <c>null</c>）。</summary>
        public RenderTexture Texture => _texture;

        /// <summary>创建一个借用指定相机、并负责其目标纹理生命周期的渲染器。</summary>
        /// <param name="camera">要接入 RenderTexture 的相机；本类不销毁相机。</param>
        /// <exception cref="ArgumentNullException"><paramref name="camera"/> 为 <c>null</c>。</exception>
        public CameraTextureRenderer(Camera camera)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            _camera = camera;
        }

        /// <summary>
        /// 确保存在一张 <paramref name="width"/>×<paramref name="height"/> 的 RenderTexture 并接到相机。
        /// 尺寸未变时空操作返回 <c>false</c>；首次或尺寸变化时重建并返回 <c>true</c>；宽 / 高 ≤ 0 视为无效、不动、返回 <c>false</c>。
        /// </summary>
        public bool Resize(int width, int height)
        {
            int curW = _texture != null ? _texture.width : 0;
            int curH = _texture != null ? _texture.height : 0;
            if (!ShouldRecreate(curW, curH, width, height)) return false;

            ReleaseTexture();
            _texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                name = "UGuiEmbed RT",
                antiAliasing = 1,
            };
            _texture.Create();
            _camera.targetTexture = _texture;
            return true;
        }

        /// <summary>手动渲染一帧（OnDemand 刷新模式用；EveryFrame 模式靠 <c>camera.enabled</c> 自动渲、不必调本方法）。</summary>
        public void Render()
        {
            if (_texture != null) _camera.Render();
        }

        public void Dispose() => ReleaseTexture();

        private void ReleaseTexture()
        {
            if (_texture == null) return;
            // 相机还指着这张纹理就先摘开，避免相机持有已释放的 targetTexture。
            if (_camera != null && _camera.targetTexture == _texture) _camera.targetTexture = null;
            _texture.Release();       // 释放 GPU 显存
            DestroyTexture(_texture); // 销毁 Unity 对象本身
            _texture = null;
        }

        // RenderTexture 是 UnityEngine.Object：运行时 Destroy、编辑期 DestroyImmediate（编辑器工具 / 测试也可能走到）。
        private static void DestroyTexture(UnityEngine.Object obj)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);
        }

        /// <summary>
        /// 是否需要（重新）申请纹理：目标尺寸有效（宽高均 &gt; 0）且与现有尺寸不同才需要。抽成纯函数便于单测。
        /// </summary>
        public static bool ShouldRecreate(int currentWidth, int currentHeight, int newWidth, int newHeight)
            => newWidth > 0 && newHeight > 0 && (currentWidth != newWidth || currentHeight != newHeight);
    }
}
