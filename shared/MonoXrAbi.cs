// MonoXR shared ABI — C# mirror of shared/monoxr_abi.h.
// Blittable, Pack = 4, byte-for-byte identical to the C++ structs:
//   MonoXrOverlaySlot  = 204 bytes
//   control header     = 32 bytes  (slots[] start at offset 32)
//   MonoXrHeadPose     = 40 bytes  (after the slots, at offset 3296)
//   MonoXrEyeGaze      = 40 bytes  (after the head pose, at offset 3336)
//   MonoXrTrackingControl = 32 bytes (after the eye gaze, at offset 3376)
//   full control block = 32 + 16*204 + 40 + 40 + 32 = 3408 bytes
using System.Runtime.InteropServices;

namespace MonoXR.Shared;

public static class MonoXrConstants
{
    public const string ControlName = @"Local\MonoXR-Control";
    public const uint Magic = 0x4D584F52u; // 'MXOR'
    public const uint Version = 1u;
    public const int MaxOverlays = 16;
    public const int NameLen = 64; // wchar_t count incl. null

    public const int SlotSize = 204;
    public const int HeaderSize = 32;
    public const int HeadPoseOffset = HeaderSize + MaxOverlays * SlotSize; // 3296
    public const int HeadPoseSize = 40;
    public const int EyeGazeOffset = HeadPoseOffset + HeadPoseSize; // 3336
    public const int EyeGazeSize = 40;
    public const int TrackingOffset = EyeGazeOffset + EyeGazeSize; // 3376
    public const int TrackingSize = 32;
    public const int ControlSize = TrackingOffset + TrackingSize; // 3408

    // Keyed-mutex keys (see header).
    public const ulong KeyProducer = 0ul;
    public const ulong KeyConsumer = 1ul;
}

public enum MonoXrSpace : uint
{
    World = 0, // stationary in the play space (LOCAL)
    Head = 1,  // locked to the headset (VIEW)
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct MonoXrOverlaySlot
{
    public uint Active;
    public uint Visible;
    public uint Space;      // MonoXrSpace
    public uint TexWidth;
    public uint TexHeight;
    public uint Format;     // DXGI_FORMAT

    public float PosX, PosY, PosZ;
    public float QuatX, QuatY, QuatZ, QuatW;
    public float SizeX, SizeY;

    public int ZOrder;
    public float CurveRadius; // 0 = flat quad; >0 = cylinder of this radius in meters

    public ulong FrameIndex;

    public fixed char SharedName[MonoXrConstants.NameLen]; // 64 * 2 = 128 bytes
}

// Experimental head-tracking filter the client sets for the layer (see monoxr_abi.h).
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MonoXrTrackingControl
{
    public uint Flags;          // MonoXrTrackingFlags
    public float Smoothing;     // 0..1
    public float MinMovePosMm;
    public float MinMoveRotDeg;
    public float LockX, LockY, LockZ; // LOCAL space, meters
    public uint Pad0;
}

[System.Flags]
public enum MonoXrTrackingFlags : uint
{
    Smooth = 1,
    MinMove = 2,
    LockPosition = 4,
}

// Headset pose in WORLD (LOCAL) space, published by the layer every frame.
// Seqlock on Seq: odd = mid-write, 0 = never published. Lives after the slots.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MonoXrHeadPose
{
    public uint Seq;
    public uint Pad0;
    public float PosX, PosY, PosZ;
    public float QuatX, QuatY, QuatZ, QuatW;
    public uint Caps;   // MonoXrLayerCaps, written by the layer
}

// What the runtime under the layer can do (MonoXrHeadPose.Caps).
[Flags]
public enum MonoXrLayerCaps : uint
{
    Known = 1,      // the layer has filled the rest in
    Cylinder = 2,   // XR_KHR_composition_layer_cylinder is on: curved overlays are possible
}

// What the layer knows about eye tracking.
public enum MonoXrGazeState : uint
{
    Unknown = 0,     // never published (also: a layer build that predates eye gaze)
    Unsupported = 1, // runtime or headset has no XR_EXT_eye_gaze_interaction
    Idle = 2,        // supported, but not asked for (the tracker is not polled)
    NoSignal = 3,    // asked for, but no usable gaze this frame
    Tracking = 4,    // the pose is a live eye gaze ray
}

// Eye gaze in WORLD (LOCAL) space: a ray from Pos along the quaternion's -Z.
// Seqlock on Seq (covers State and the pose). Request is written by the client.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MonoXrEyeGaze
{
    public uint Seq;
    public uint State;   // MonoXrGazeState
    public uint Request; // client -> layer: 1 = poll the eye tracker
    public float PosX, PosY, PosZ;
    public float QuatX, QuatY, QuatZ, QuatW;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MonoXrControlHeader
{
    public uint Magic;
    public uint Version;
    public uint MaxOverlays;
    public uint ClientPid;
    public ulong ClientHeartbeat;
    public uint LayerActive;
    public uint LayerPid; // game's pid while LayerActive; 0 otherwise
    // slots[MaxOverlays] follow at offset HeaderSize.
}
