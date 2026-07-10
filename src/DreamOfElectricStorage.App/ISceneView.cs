using System;
using System.Collections.Generic;
using System.Numerics;
using DreamOfElectricStorage.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace DreamOfElectricStorage.App;

/// <summary>
/// The shared canvas-loop surface both views expose (draw + camera + basic input).
/// View-specific features (breadcrumb, drill, minimap, drag-move) stay on the concrete
/// type and are only reached when that view is active — see MainPage's mode gating.
/// </summary>
public interface ISceneView
{
    /// <summary>Fires when an animation frame is needed → MainPage invalidates the canvas.</summary>
    event Action? RedrawNeeded;

    GraphCamera Camera { get; }
    bool LightTheme { get; set; }
    bool ReduceMotion { get; set; }

    void SetIndex(MachineIndex machine);
    void Draw(CanvasControl canvas, CanvasDrawingSession session);

    void Pan(Vector2 screenDelta);
    void Zoom(float wheelDelta, Vector2 screenCenter);
    void ZoomHome();

    /// <summary>Update the hovered node; returns true if the hover changed (repaint).</summary>
    bool SetHover(Vector2 screenPoint);

    IReadOnlyList<GraphView.VisibleNode> GetVisibleNodes();
}
