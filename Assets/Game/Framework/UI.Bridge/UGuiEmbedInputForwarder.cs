using System;
using System.Collections.Generic;
using Game.Framework.UI.Toolkit;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.UIElements;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Game.Framework.Test")]

namespace Game.Framework.UI.Bridge
{
    /// <summary>
    /// 把 <see cref="RenderTextureElement"/> 上的 UI Toolkit 指针事件转发进渲进 RenderTexture 的 UGUI 画布——
    /// v1 的「事件不穿透」解法（ADR-0033 §v2）。不走全局 EventSystem 的屏幕路由（它按真实鼠标位置走、够不到离屏 RT），
    /// 改<b>手动驱动</b>：把元素内指针坐标翻成 RT 空间屏幕点，喂给一个（禁用注册的）<see cref="GraphicRaycaster"/> 命中控件，
    /// 再 <see cref="ExecuteEvents"/> 分发 down/up/click/enter/exit/drag/scroll。
    /// </summary>
    /// <remarks>
    /// 全指针状态机：悬停 enter/exit；按下记压目标，抬起同目标判 click；move 超 <see cref="EventSystem.pixelDragThreshold"/>
    /// 触发 beginDrag→drag→抬起 endDrag（拖拽期捕获指针，移出元素仍收事件）；滚轮转 scrollDelta。
    /// 文本输入 / IME、多点触控不做。坐标换算与拖拽阈值抽成纯静态函数便于单测。
    /// </remarks>
    internal sealed class UGuiEmbedInputForwarder : IDisposable
    {
        private readonly RenderTextureElement _element;
        private readonly GraphicRaycaster _raycaster;
        private readonly EventSystem _eventSystem;
        private readonly Func<Vector2Int> _rtSize;

        private readonly PointerEventData _ped;
        private readonly List<RaycastResult> _results = new();

        private GameObject _enterTarget;  // 当前悬停目标
        private GameObject _pressTarget;  // 按下目标（判 click 用）
        private GameObject _dragTarget;   // 拖拽目标
        private bool _dragging;
        private Vector2 _pressPos;        // RT 空间按下点（拖拽阈值基准）
        private Vector2 _lastPos;         // 上一指针点（算 delta）
        private bool _disposed;

        public UGuiEmbedInputForwarder(RenderTextureElement element, GraphicRaycaster raycaster, EventSystem eventSystem, Func<Vector2Int> rtSize)
        {
            _element = element;
            _raycaster = raycaster;
            _eventSystem = eventSystem;
            _rtSize = rtSize;
            _ped = new PointerEventData(_eventSystem);

            _element.pickingMode = PickingMode.Position; // 只读显示默认 Ignore；交互时要吃指针事件
            _element.RegisterCallback<PointerDownEvent>(OnDown);
            _element.RegisterCallback<PointerUpEvent>(OnUp);
            _element.RegisterCallback<PointerMoveEvent>(OnMove);
            _element.RegisterCallback<PointerLeaveEvent>(OnLeave);
            _element.RegisterCallback<WheelEvent>(OnWheel);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _element.UnregisterCallback<PointerDownEvent>(OnDown);
            _element.UnregisterCallback<PointerUpEvent>(OnUp);
            _element.UnregisterCallback<PointerMoveEvent>(OnMove);
            _element.UnregisterCallback<PointerLeaveEvent>(OnLeave);
            _element.UnregisterCallback<WheelEvent>(OnWheel);
            _element.pickingMode = PickingMode.Ignore;
        }

        // ── 坐标 / 阈值：纯函数，单测覆盖 ──

        /// <summary>元素内局部坐标（面板点、y 向下）→ RT 空间屏幕点（像素、y 向上，供 GraphicRaycaster 用）。</summary>
        public static Vector2 ComputeRtScreenPoint(Vector2 local, Vector2 contentSize, Vector2Int rtSize)
        {
            float u = contentSize.x > 0f ? local.x / contentSize.x : 0f;
            float v = contentSize.y > 0f ? local.y / contentSize.y : 0f;
            return new Vector2(u * rtSize.x, (1f - v) * rtSize.y); // y 翻转：Toolkit y 向下 → 屏幕 y 向上
        }

        /// <summary>指针从按下点移动是否已超过拖拽阈值（超过才从「可能点击」升级为「拖拽」）。</summary>
        public static bool ExceedsDragThreshold(Vector2 pressPos, Vector2 currentPos, float threshold)
            => (currentPos - pressPos).sqrMagnitude >= threshold * threshold;

        private Vector2 ToRtScreen(Vector2 local) => ComputeRtScreenPoint(local, _element.contentRect.size, _rtSize());

        private GameObject Raycast(Vector2 pos, out RaycastResult result)
        {
            _ped.position = pos;
            _results.Clear();
            _raycaster.Raycast(_ped, _results); // 禁用注册的 raycaster 仍可手动 Raycast
            result = _results.Count > 0 ? _results[0] : default;
            return result.gameObject;
        }

        // ── 指针回调 ──

        private void OnDown(PointerDownEvent evt)
        {
            var pos = ToRtScreen(evt.localPosition);
            _lastPos = pos;
            var hit = Raycast(pos, out var result);

            _ped.delta = Vector2.zero;
            _ped.pressPosition = pos;
            _ped.pointerPressRaycast = result;
            _ped.pointerCurrentRaycast = result;
            _ped.button = PointerEventData.InputButton.Left;
            _ped.eligibleForClick = true;

            var press = ExecuteEvents.ExecuteHierarchy(hit, _ped, ExecuteEvents.pointerDownHandler);
            if (press == null) press = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit);
            _ped.pointerPress = press;
            _ped.rawPointerPress = hit;
            _pressTarget = press;

            _dragTarget = ExecuteEvents.GetEventHandler<IDragHandler>(hit);
            _ped.pointerDrag = _dragTarget;
            _pressPos = pos;
            _dragging = false;

            _element.CapturePointer(evt.pointerId); // 拖拽 / 抬起移出元素仍收事件
            evt.StopPropagation();
        }

        private void OnMove(PointerMoveEvent evt)
        {
            var pos = ToRtScreen(evt.localPosition);
            _ped.delta = pos - _lastPos;
            _ped.position = pos;
            _lastPos = pos;

            var hit = Raycast(pos, out var result);
            _ped.pointerCurrentRaycast = result;
            UpdateHover(hit);

            if (_dragTarget != null)
            {
                if (!_dragging && ExceedsDragThreshold(_pressPos, pos, _eventSystem.pixelDragThreshold))
                {
                    ExecuteEvents.Execute(_dragTarget, _ped, ExecuteEvents.beginDragHandler);
                    _dragging = true;
                }
                if (_dragging) ExecuteEvents.Execute(_dragTarget, _ped, ExecuteEvents.dragHandler);
            }
        }

        private void OnUp(PointerUpEvent evt)
        {
            var pos = ToRtScreen(evt.localPosition);
            _ped.position = pos;
            var hit = Raycast(pos, out var result);
            _ped.pointerCurrentRaycast = result;

            if (_pressTarget != null) ExecuteEvents.Execute(_pressTarget, _ped, ExecuteEvents.pointerUpHandler);

            if (!_dragging)
            {
                var clickTarget = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit);
                if (clickTarget != null && clickTarget == _pressTarget)
                    ExecuteEvents.Execute(_pressTarget, _ped, ExecuteEvents.pointerClickHandler);
            }
            else if (_dragTarget != null)
            {
                ExecuteEvents.Execute(_dragTarget, _ped, ExecuteEvents.endDragHandler);
                ExecuteEvents.ExecuteHierarchy(hit, _ped, ExecuteEvents.dropHandler);
            }

            _ped.pointerPress = null;
            _ped.rawPointerPress = null;
            _ped.pointerDrag = null;
            _ped.eligibleForClick = false;
            _pressTarget = null;
            _dragTarget = null;
            _dragging = false;
            _element.ReleasePointer(evt.pointerId);
        }

        private void OnLeave(PointerLeaveEvent evt)
        {
            // 拖拽期不清悬停（指针捕获中仍在交互）；仅纯悬停离开时退出。
            if (_dragging) return;
            UpdateHover(null);
        }

        private void OnWheel(WheelEvent evt)
        {
            var pos = ToRtScreen(evt.localMousePosition); // WheelEvent 是鼠标事件，用 localMousePosition
            _ped.position = pos;
            var hit = Raycast(pos, out _);
            _ped.scrollDelta = new Vector2(evt.delta.x, -evt.delta.y); // UGUI scrollDelta 与 Toolkit wheel y 反向
            ExecuteEvents.ExecuteHierarchy(hit, _ped, ExecuteEvents.scrollHandler);
        }

        private void UpdateHover(GameObject hit)
        {
            if (hit == _enterTarget) return;
            if (_enterTarget != null) ExecuteEvents.ExecuteHierarchy(_enterTarget, _ped, ExecuteEvents.pointerExitHandler);
            _enterTarget = hit;
            if (_enterTarget != null) ExecuteEvents.ExecuteHierarchy(_enterTarget, _ped, ExecuteEvents.pointerEnterHandler);
            _ped.pointerEnter = _enterTarget;
        }
    }
}
