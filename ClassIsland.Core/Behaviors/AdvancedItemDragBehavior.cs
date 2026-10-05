// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
//
// Modified by HelloWRC, 2025

using System.Collections;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using ClassIsland.Core.Controls;
using ClassIsland.Core.Helpers.UI;

namespace ClassIsland.Core.Behaviors;

/// <summary>
/// Allows dragging items within an <see cref="ItemsControl"/>.
/// </summary>
public class AdvancedItemDragBehavior : StyledElementBehavior<Control>
{
    private bool _enableDrag;
    private bool _dragStarted;
    private Point _start;
    private int _draggedIndex;
    private int _targetIndex;
    private ItemsControl? _itemsControl;
    private Control? _draggedContainer;
    private bool _captured;

    /// <summary>
    /// Identifies the <see cref="Orientation"/> avalonia property.
    /// </summary>
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<AdvancedItemDragBehavior, Orientation>(nameof(Orientation));

    /// <summary>
    /// Identifies the <see cref="HorizontalDragThreshold"/> avalonia property.
    /// </summary>
    public static readonly StyledProperty<double> HorizontalDragThresholdProperty =
        AvaloniaProperty.Register<AdvancedItemDragBehavior, double>(nameof(HorizontalDragThreshold), 3);

    /// <summary>
    /// Identifies the <see cref="VerticalDragThreshold"/> avalonia property.
    /// </summary>
    public static readonly StyledProperty<double> VerticalDragThresholdProperty =
        AvaloniaProperty.Register<AdvancedItemDragBehavior, double>(nameof(VerticalDragThreshold), 3);

    /// <summary>
    /// Gets or sets the orientation of the drag operation.
    /// </summary>
    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>
    /// Gets or sets the horizontal drag threshold in pixels.
    /// </summary>
    public double HorizontalDragThreshold
    {
        get => GetValue(HorizontalDragThresholdProperty);
        set => SetValue(HorizontalDragThresholdProperty, value);
    }

    /// <summary>
    /// Gets or sets the vertical drag threshold in pixels.
    /// </summary>
    public double VerticalDragThreshold
    {
        get => GetValue(VerticalDragThresholdProperty);
        set => SetValue(VerticalDragThresholdProperty, value);
    }

    public static readonly StyledProperty<bool> AllowDragFromDragThumbOnlyProperty = AvaloniaProperty.Register<AdvancedItemDragBehavior, bool>(
        nameof(AllowDragFromDragThumbOnly));

    public bool AllowDragFromDragThumbOnly
    {
        get => GetValue(AllowDragFromDragThumbOnlyProperty);
        set => SetValue(AllowDragFromDragThumbOnlyProperty, value);
    }

    private bool _stoodDown;

    /// <inheritdoc />
    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject is not null)
        {
            // 必须在元素级订阅：Style 重应用 Behaviors 时只走 OnDetaching，
            // 不走 OnDetachedFromVisualTree。订阅若绑在 visual-tree 生命周期上，
            // 旧实例的隧道订阅会残留（AssociatedObject 已置空）而新实例接不上，
            // 按压全打进僵尸实例导致“拖一次就死”。先移除再添加，保证幂等。
            RemoveElementHandlers(AssociatedObject);
            AddElementHandlers(AssociatedObject);
            // 去重：同元素已有活实例时后来者 stand down，保证每元素恰好一个活实例，
            // 否则两个实例会在 Release 时各执行一次 MoveDraggedItem 把顺序弹回去。
            if (CountActiveSiblings() > 0)
            {
                _stoodDown = true;
                RemoveElementHandlers(AssociatedObject);
            }
        }
    }

    /// <inheritdoc />
    protected override void OnDetaching()
    {
        var associated = AssociatedObject;
        if (associated is not null)
        {
            RemoveElementHandlers(associated);
            if (!_stoodDown)
            {
                // 自己是活实例：尝试激活第一个 stand down 的同类，把位置传下去。
                foreach (var sibling in Avalonia.Xaml.Interactivity.Interaction.GetBehaviors(associated)?.OfType<AdvancedItemDragBehavior>() ?? Enumerable.Empty<AdvancedItemDragBehavior>())
                {
                    if (!ReferenceEquals(sibling, this) && sibling._stoodDown)
                    {
                        sibling._stoodDown = false;
                        sibling.AddElementHandlers(associated);
                        break;
                    }
                }
            }
        }
        base.OnDetaching();
    }

    private int CountActiveSiblings()
    {
        var associated = AssociatedObject;
        if (associated is null) return 0;
        var count = 0;
        foreach (var sibling in Avalonia.Xaml.Interactivity.Interaction.GetBehaviors(associated)?.OfType<AdvancedItemDragBehavior>() ?? Enumerable.Empty<AdvancedItemDragBehavior>())
        {
            if (!ReferenceEquals(sibling, this) && !sibling._stoodDown) count++;
        }
        return count;
    }

    private void AddElementHandlers(Control associated)
    {
        associated.AddHandler(InputElement.PointerReleasedEvent, PointerReleased, RoutingStrategies.Tunnel);
        associated.AddHandler(InputElement.PointerPressedEvent, PointerPressed, RoutingStrategies.Tunnel);
        associated.AddHandler(InputElement.PointerMovedEvent, PointerMoved, RoutingStrategies.Tunnel);
        associated.AddHandler(InputElement.PointerCaptureLostEvent, PointerCaptureLost, RoutingStrategies.Tunnel);
    }

    private void RemoveElementHandlers(Control associated)
    {
        associated.RemoveHandler(InputElement.PointerReleasedEvent, PointerReleased);
        associated.RemoveHandler(InputElement.PointerPressedEvent, PointerPressed);
        associated.RemoveHandler(InputElement.PointerMovedEvent, PointerMoved);
        associated.RemoveHandler(InputElement.PointerCaptureLostEvent, PointerCaptureLost);
    }

    private bool IsStaleHandler(object? sender)
    {
        return !ReferenceEquals(AssociatedObject, sender);
    }

    private void PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsStaleHandler(sender)) return;
        var isFromDragThumb = (e.Source as Control)?.FindAncestorOfType<TouchDragThumb>() is not null;
        var isFromCurrentThumb = e.Source is Control c1 && UITreeHelper.HasParent(c1, AssociatedObject);
        if ((e.Source as Control)?.FindAncestorOfType<ISelectable>() is {} selectable && !Equals(selectable, AssociatedObject))
        {
            isFromCurrentThumb = false;
        }
        var isTouchMode = e.Pointer.Type == PointerType.Touch;
        // AssociatedObject?.ShowToast($"(debug) {isFromDragThumb} {isFromCurrentThumb} {isTouchMode}");
        if (((isTouchMode || AllowDragFromDragThumbOnly) && !isFromDragThumb) || (isFromDragThumb && !isFromCurrentThumb))
        {
            return;
        }
        var properties = e.GetCurrentPoint(AssociatedObject).Properties;
        if (properties.IsLeftButtonPressed 
            && AssociatedObject?.Parent is ItemsControl itemsControl)
        {
            _enableDrag = true;
            _dragStarted = false;
            _start = e.GetPosition(itemsControl);
            _draggedIndex = -1;
            _targetIndex = -1;
            _itemsControl = itemsControl;
            _draggedContainer = AssociatedObject;

            if (_draggedContainer is not null)
            {
                SetDraggingPseudoClasses(_draggedContainer, true);
            }

            AddTransforms(_itemsControl);

            _captured = true;
        }
    }

    private void PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (IsStaleHandler(sender)) return;
        if (_captured)
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                Released();
            }

            _captured = false;
        }
    }

    private void PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (IsStaleHandler(sender)) return;
        Released();
        _captured = false;
    }

    private void Released()
    {
        if (!_enableDrag)
        {
            return;
        }

        var snapshotControl = _itemsControl;
        // 排序后下一帧检查各行 behavior：ItemsControl 排序时会清空行容器的
        // Interaction.Behaviors 且不再补回，行为数为 0 的行在此自愈补上。
        // OnAttached 去重保证不会叠加；自愈尽力而为，不应影响主流程。
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (snapshotControl is null)
                {
                    return;
                }
                foreach (var c in snapshotControl.GetRealizedContainers())
                {
                    var behaviors = Avalonia.Xaml.Interactivity.Interaction.GetBehaviors(c);
                    var ours = behaviors?.OfType<AdvancedItemDragBehavior>().Count() ?? -1;
                    if (ours == 0 && behaviors is not null)
                    {
                        // 按本实例（刚完成拖拽、配置与该行一致）补一个；OnAttached 去重保证不叠加。
                        var healed = new AdvancedItemDragBehavior
                        {
                            Orientation = Orientation,
                            HorizontalDragThreshold = HorizontalDragThreshold,
                            VerticalDragThreshold = VerticalDragThreshold,
                            AllowDragFromDragThumbOnly = AllowDragFromDragThumbOnly
                        };
                        behaviors.Add(healed);
                    }
                }
            }
            catch
            {
                // 自愈失败则保持现状，不影响主流程。
            }
        });

        RemoveTransforms(_itemsControl);

        if (_itemsControl is not null)
        {
            foreach (var control in _itemsControl.GetRealizedContainers())
            {
                SetDraggingPseudoClasses(control, true);
            }
        }

        if (_dragStarted)
        {
            if (_draggedIndex >= 0 && _targetIndex >= 0 && _draggedIndex != _targetIndex)
            {
                MoveDraggedItem(_itemsControl, _draggedIndex, _targetIndex);
            }
        }

        if (_itemsControl is not null)
        {
            foreach (var control in _itemsControl.GetRealizedContainers())
            {
                SetDraggingPseudoClasses(control, false);
            }
        }

        if (_draggedContainer is not null)
        {
            SetDraggingPseudoClasses(_draggedContainer, false);
        }

        _draggedIndex = -1;
        _targetIndex = -1;
        _enableDrag = false;
        _dragStarted = false;
        _itemsControl = null;

        _draggedContainer = null;
    }

    private void AddTransforms(ItemsControl? itemsControl)
    {
        if (itemsControl?.Items is null)
        {
            return;
        }

        var i = 0;

        foreach (var _ in itemsControl.Items)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container is not null)
            {
                SetTranslateTransform(container, 0, 0);
            }
  
            i++;
        }  
    }

    private void RemoveTransforms(ItemsControl? itemsControl)
    {
        if (itemsControl?.Items is null)
        {
            return;
        }

        var i = 0;

        foreach (var _ in itemsControl.Items)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container is not null)
            {
                SetTranslateTransform(container, 0, 0);
            }
  
            i++;
        }  
    }

    private void MoveDraggedItem(ItemsControl? itemsControl, int draggedIndex, int targetIndex)
    {
        if (itemsControl?.ItemsSource is IList itemsSource)
        {
            var draggedItem = itemsSource[draggedIndex];
            itemsSource.RemoveAt(draggedIndex);
            itemsSource.Insert(targetIndex, draggedItem);

            if (itemsControl is SelectingItemsControl selectingItemsControl)
            {
                selectingItemsControl.SelectedIndex = targetIndex;
            }
        }
        else
        {
            if (itemsControl?.Items is {IsReadOnly: false} itemCollection)
            {
                var draggedItem = itemCollection[draggedIndex];
                itemCollection.RemoveAt(draggedIndex);
                itemCollection.Insert(targetIndex, draggedItem);

                if (itemsControl is SelectingItemsControl selectingItemsControl)
                {
                    selectingItemsControl.SelectedIndex = targetIndex;
                } 
            }
        }
    }

    private void PointerMoved(object? sender, PointerEventArgs e)
    {
        if (IsStaleHandler(sender)) return;
        var properties = e.GetCurrentPoint(AssociatedObject).Properties;
        if (_captured
            && properties.IsLeftButtonPressed)
        {
            if (_itemsControl?.Items is null || _draggedContainer?.RenderTransform is null || !_enableDrag)
            {
                return;
            }

            var orientation = Orientation;
            var position = e.GetPosition(_itemsControl);
            var delta = orientation == Orientation.Horizontal ? position.X - _start.X : position.Y - _start.Y;

            if (!_dragStarted)
            {
                var diff = _start - position;
                var horizontalDragThreshold = HorizontalDragThreshold;
                var verticalDragThreshold = VerticalDragThreshold;

                if (orientation == Orientation.Horizontal)
                {
                    if (Math.Abs(diff.X) > horizontalDragThreshold)
                    {
                        _dragStarted = true;
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    if (Math.Abs(diff.Y) > verticalDragThreshold)
                    {
                        _dragStarted = true;
                    }
                    else
                    {
                        return;
                    }
                }
            }

            if (orientation == Orientation.Horizontal)
            {
                SetTranslateTransform(_draggedContainer, delta, 0);
            }
            else
            {
                SetTranslateTransform(_draggedContainer, 0, delta);
            }

            _draggedIndex = _itemsControl.IndexFromContainer(_draggedContainer);
            _targetIndex = -1;

            var draggedBounds = _draggedContainer.Bounds;

            var draggedStart = orientation == Orientation.Horizontal ? draggedBounds.X : draggedBounds.Y;

            var draggedDeltaStart = orientation == Orientation.Horizontal
                ? draggedBounds.X + delta
                : draggedBounds.Y + delta;

            var draggedDeltaEnd = orientation == Orientation.Horizontal
                ? draggedBounds.X + delta + draggedBounds.Width
                : draggedBounds.Y + delta + draggedBounds.Height;

            var i = 0;

            foreach (var _ in _itemsControl.Items)
            {
                var targetContainer = _itemsControl.ContainerFromIndex(i);
                if (targetContainer?.RenderTransform is null || ReferenceEquals(targetContainer, _draggedContainer))
                {
                    i++;
                    continue;
                }

                var targetBounds = targetContainer.Bounds;

                var targetStart = orientation == Orientation.Horizontal ? targetBounds.X : targetBounds.Y;

                var targetMid = orientation == Orientation.Horizontal
                    ? targetBounds.X + targetBounds.Width / 2
                    : targetBounds.Y + targetBounds.Height / 2;

                var targetIndex = _itemsControl.IndexFromContainer(targetContainer);

                if (targetStart > draggedStart && draggedDeltaEnd >= targetMid)
                {
                    if (orientation == Orientation.Horizontal)
                    {
                        SetTranslateTransform(targetContainer, -draggedBounds.Width, 0);
                    }
                    else
                    {
                        SetTranslateTransform(targetContainer, 0, -draggedBounds.Height);
                    }

                    _targetIndex = _targetIndex == -1 ? targetIndex :
                        targetIndex > _targetIndex ? targetIndex : _targetIndex;
                }
                else if (targetStart < draggedStart && draggedDeltaStart <= targetMid)
                {
                    if (orientation == Orientation.Horizontal)
                    {
                        SetTranslateTransform(targetContainer, draggedBounds.Width, 0);
                    }
                    else
                    {
                        SetTranslateTransform(targetContainer, 0, draggedBounds.Height);
                    }

                    _targetIndex = _targetIndex == -1 ? targetIndex :
                        targetIndex < _targetIndex ? targetIndex : _targetIndex;
                }
                else
                {
                    if (orientation == Orientation.Horizontal)
                    {
                        SetTranslateTransform(targetContainer, 0, 0);
                    }
                    else
                    {
                        SetTranslateTransform(targetContainer, 0, 0);
                    }
                }

                i++;
            }
        }
    }

    private void SetDraggingPseudoClasses(Control control, bool isDragging)
    {
        if (isDragging)
        {
            ((IPseudoClasses)control.Classes).Add(":dragging");
        }
        else
        {
            ((IPseudoClasses)control.Classes).Remove(":dragging");
        }
    }

    private void SetTranslateTransform(Control control, double x, double y)
    {
        var transformBuilder = new TransformOperations.Builder(1);
        transformBuilder.AppendTranslate(x, y);
        control.RenderTransform = transformBuilder.Build();
    }
}