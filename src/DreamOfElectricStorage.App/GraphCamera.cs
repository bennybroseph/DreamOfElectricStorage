using System;
using System.Numerics;

namespace DreamOfElectricStorage.App;

/// <summary>
/// Animated pan/zoom camera: current state eases toward targets with exponential
/// smoothing (frame-rate independent, interruptible mid-flight). Drag-pan writes both
/// current and target so panning stays 1:1 crisp; everything else flies.
/// </summary>
public sealed class GraphCamera
{
    private const float MaxZoom = 12f;
    private const float SmoothingRate = 10f;

    private Vector2 _panTarget;
    private float _zoomTarget = 1f;

    public Vector2 Pan { get; private set; }
    public float Zoom { get; private set; } = 1f;

    /// <summary>Where the zoom is heading — lets callers tell inward from outward motion.</summary>
    public float ZoomTarget => _zoomTarget;

    /// <summary>Content-aware floor set by ZoomToFit — you can't zoom out past ~1/3 of the fitted view.</summary>
    public float MinZoom { get; private set; } = 0.02f;

    public bool Settled =>
        (Pan - _panTarget).LengthSquared() < 0.01f && MathF.Abs(Zoom - _zoomTarget) < 0.0005f;

    public Matrix3x2 Transform => Matrix3x2.CreateScale(Zoom) * Matrix3x2.CreateTranslation(Pan);
    public Vector2 ScreenToWorld(Vector2 screen) => (screen - Pan) / Zoom;
    public Vector2 WorldToScreen(Vector2 world) => world * Zoom + Pan;

    public void Advance(float dt)
    {
        float a = Easings.Approach(SmoothingRate, dt);
        Pan += (_panTarget - Pan) * a;
        Zoom += (_zoomTarget - Zoom) * a;
        if (Settled)
        {
            Pan = _panTarget;
            Zoom = _zoomTarget;
        }
    }

    public void JumpTo(Vector2 pan, float zoom)
    {
        Pan = _panTarget = pan;
        Zoom = _zoomTarget = Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    /// <summary>Reduce-motion mode: flights become jumps.</summary>
    public bool Instant { get; set; }

    public void FlyTo(Vector2 pan, float zoom)
    {
        if (Instant)
        {
            JumpTo(pan, zoom);
            return;
        }
        _panTarget = pan;
        _zoomTarget = Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    /// <summary>Crisp 1:1 pan (no easing lag while the hand is on the canvas).</summary>
    public void PanBy(Vector2 screenDelta)
    {
        Pan += screenDelta;
        _panTarget = Pan;
    }

    /// <summary>Animated zoom about the cursor. Targets computed from target state so repeated wheel ticks compose.</summary>
    public void ZoomAboutPoint(float wheelDelta, Vector2 screenCenter)
    {
        float factor = MathF.Pow(1.001f, wheelDelta);
        float newZoom = Math.Clamp(_zoomTarget * factor, MinZoom, MaxZoom);
        _panTarget = screenCenter - (screenCenter - _panTarget) * (newZoom / _zoomTarget);
        _zoomTarget = newZoom;
    }

    /// <summary>
    /// Frames content of the given world extent (radius from origin) in the viewport and
    /// sets the content-aware zoom floor. Instant when <paramref name="fly"/> is false.
    /// </summary>
    public void ZoomToFit(Vector2 viewport, float contentExtent, bool fly)
    {
        float fitZoom = FitZoom(viewport, contentExtent);
        MinZoom = fitZoom / 3f;

        if (fly)
            FlyTo(viewport / 2f, fitZoom);
        else
            JumpTo(viewport / 2f, fitZoom);
    }

    /// <summary>Refreshes the zoom floor for new content without moving the camera (re-root path).</summary>
    public void UpdateMinZoom(Vector2 viewport, float contentExtent) =>
        MinZoom = FitZoom(viewport, contentExtent) / 3f;

    public static float FitZoom(Vector2 viewport, float contentExtent) => Math.Clamp(
        MathF.Min(viewport.X, viewport.Y) / MathF.Max(contentExtent * 2.2f, 1f),
        0.0001f, MaxZoom);

    /// <summary>
    /// Re-expresses the camera in a child coordinate frame (re-root INTO a node at
    /// <paramref name="nodePos"/> whose contents were previewed at <paramref name="scale"/>).
    /// Screen-invariant: every world point maps to the same pixel before and after, for
    /// both current state and in-flight targets — a re-root mid-flight stays seamless.
    /// </summary>
    public void RemapDown(Vector2 nodePos, float scale)
    {
        Pan += nodePos * Zoom;
        Zoom *= scale;
        _panTarget += nodePos * _zoomTarget;
        _zoomTarget *= scale;
    }

    /// <summary>Inverse of <see cref="RemapDown"/>: re-root OUT to the parent frame.</summary>
    public void RemapUp(Vector2 nodePos, float scale)
    {
        Zoom /= scale;
        Pan -= nodePos * Zoom;
        _zoomTarget /= scale;
        _panTarget -= nodePos * _zoomTarget;
    }
}
