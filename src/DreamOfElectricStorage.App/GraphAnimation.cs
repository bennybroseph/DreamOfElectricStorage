using System;
using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace DreamOfElectricStorage.App;

/// <summary>
/// Per-frame tick source built on CompositionTarget.Rendering (UI thread — keeps the
/// single-writer index contract). Attached only while something animates, per the
/// platform guidance: an idle app does zero per-frame work.
/// </summary>
public sealed class AnimationClock
{
    private readonly Stopwatch _stopwatch = new();
    private long _lastTicks;
    private bool _attached;

    /// <summary>Advance animations; dt in seconds (clamped to 100ms to survive stalls).</summary>
    public event Action<float>? Tick;

    /// <summary>Return true when nothing is animating — the clock detaches after that tick.</summary>
    public Func<bool>? IsIdle { get; set; }

    public void RequestFrames()
    {
        if (_attached)
            return;
        _attached = true;
        _stopwatch.Restart();
        _lastTicks = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        long now = _stopwatch.ElapsedTicks;
        float dt = Math.Clamp((now - _lastTicks) / (float)Stopwatch.Frequency, 0f, 0.1f);
        _lastTicks = now;

        Tick?.Invoke(dt);

        if (IsIdle?.Invoke() ?? true)
        {
            CompositionTarget.Rendering -= OnRendering;
            _attached = false;
        }
    }
}

/// <summary>A 0→1 progress tween with easing. Value is the eased progress.</summary>
public sealed class Tween(float duration, Func<float, float>? easing = null)
{
    private float _elapsed = float.MaxValue;

    public float Value { get; private set; } = 1f;
    public bool Running => _elapsed < duration;

    public void Start()
    {
        _elapsed = 0f;
        Value = 0f;
    }

    /// <summary>Jump straight to the end state.</summary>
    public void Finish()
    {
        _elapsed = float.MaxValue;
        Value = 1f;
    }

    public void Advance(float dt)
    {
        if (!Running)
            return;
        _elapsed += dt;
        Value = (easing ?? Easings.CubicOut)(Math.Clamp(_elapsed / duration, 0f, 1f));
    }
}

public static class Easings
{
    public static float CubicOut(float t) => 1f - MathF.Pow(1f - t, 3f);
    public static float CubicInOut(float t) => t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;

    /// <summary>Frame-rate-independent exponential approach factor (smoothing rate k per second).</summary>
    public static float Approach(float k, float dt) => 1f - MathF.Exp(-k * dt);
}
