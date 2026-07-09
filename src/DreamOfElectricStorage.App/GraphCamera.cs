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

    public void FlyTo(Vector2 pan, float zoom)
    {
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
        float fitZoom = Math.Clamp(
            MathF.Min(viewport.X, viewport.Y) / MathF.Max(contentExtent * 2.2f, 1f),
            0.0001f, MaxZoom);
        MinZoom = fitZoom / 3f;

        if (fly)
            FlyTo(viewport / 2f, fitZoom);
        else
            JumpTo(viewport / 2f, fitZoom);
    }
}
