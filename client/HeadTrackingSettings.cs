using System.Numerics;

namespace MonoXR.Client;

/// <summary>
/// EXPERIMENTAL filter on the head pose the OpenXR game receives (what it renders
/// the world from). Applied by the layer; the overlays keep using the real
/// tracking. Set with <see cref="MonoGameOverlayHost.HeadTracking"/>.
/// </summary>
public readonly record struct HeadTrackingSettings
{
    /// <summary>Ease the pose toward the real one, taking out jitter at the cost of a little lag.</summary>
    public bool Smooth { get; init; }

    /// <summary>How much, 0 (none) to 1 (heaviest, about a quarter of a second).</summary>
    public float Smoothing { get; init; }

    /// <summary>Hold still until the head has moved further than the minimums below.</summary>
    public bool MinMove { get; init; }

    /// <summary>Smallest position change that counts, in millimetres.</summary>
    public float MinMovePositionMm { get; init; }

    /// <summary>Smallest rotation change that counts, in degrees.</summary>
    public float MinMoveRotationDeg { get; init; }

    /// <summary>Ignore position tracking: keep the rotation, put the head at <see cref="LockPosition"/>.</summary>
    public bool LockHeadPosition { get; init; }

    /// <summary>Where the head is put when locked, in the OpenXR LOCAL space (meters).</summary>
    public Vector3 LockPosition { get; init; }
}
