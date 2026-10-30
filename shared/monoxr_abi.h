// MonoXR shared ABI — single source of truth for the cross-process contract
// between the MonoGame client (MonoXR.Client, C#) and the OpenXR API layer
// (XR_APILAYER_NOVELTY_monoxr, C++).
//
// The C# mirror of this file is shared/MonoXrAbi.cs. If you change one, change
// the other. Layout is Pack=4 and must match byte-for-byte.
#ifndef MONOXR_ABI_H
#define MONOXR_ABI_H

#include <stdint.h>

// Named kernel objects (Local\ = per-session namespace, good enough since the
// game and the MonoGame client run in the same interactive session).
#define MONOXR_CONTROL_NAME L"Local\\MonoXR-Control"

#define MONOXR_MAGIC        0x4D584F52u   // 'MXOR'
#define MONOXR_VERSION      1u
#define MONOXR_MAX_OVERLAYS 16u
#define MONOXR_NAME_LEN     64u           // wchar_t count incl. null

// Which eye an overlay is shown to (bits 1-2 of MonoXrOverlaySlot.visible).
// The values are XrEyeVisibility's, so the layer passes them straight through.
enum MonoXrEye {
    MONOXR_EYE_BOTH  = 0,
    MONOXR_EYE_LEFT  = 1,
    MONOXR_EYE_RIGHT = 2,
};
#define MONOXR_VISIBLE_SHOWN     1u
#define MONOXR_VISIBLE_EYE_SHIFT 1u
#define MONOXR_VISIBLE_EYE_MASK  3u

// Reference space an overlay's pose is expressed in.
enum MonoXrSpace {
    MONOXR_SPACE_WORLD = 0,  // stationary in the play space (XR_REFERENCE_SPACE_TYPE_LOCAL)
    MONOXR_SPACE_HEAD  = 1,  // locked to the headset (XR_REFERENCE_SPACE_TYPE_VIEW)
};

#pragma pack(push, 4)

// One overlay = one MonoGame RenderTarget published as a shared texture.
struct MonoXrOverlaySlot {
    uint32_t active;    // client owns this slot
    // Bit 0: draw it this frame. Bits 1-2: which eye sees it (MonoXrEye; 0 = both,
    // so every older client, which writes only 0 or 1, still means "both eyes").
    // Kept inside this word so the control block layout and version are unchanged.
    uint32_t visible;
    uint32_t space;     // MonoXrSpace
    uint32_t texWidth;
    uint32_t texHeight;
    uint32_t format;    // DXGI_FORMAT of the shared texture & requested swapchain

    // Pose of the quad centre, in the chosen reference space (meters / quaternion).
    float posX, posY, posZ;
    float quatX, quatY, quatZ, quatW;

    // Quad size in meters (world) or in meters at 1m (head-locked).
    float sizeX, sizeY;

    // Sort order; higher draws on top. Also used to keep a stable layer order.
    int32_t zOrder;

    // 0 = a flat quad. Above 0 the overlay is curved onto a cylinder of this
    // radius in meters (XR_KHR_composition_layer_cylinder), its axis vertical and
    // its centre `curveRadius` behind the quad's centre along the quad's +Z, so
    // the quad's size becomes the arc length and height of the curved surface.
    // The layer falls back to the flat quad when the runtime has no cylinder
    // layers. (Was padding, so older clients write 0 = flat.)
    float curveRadius;

    // Bumped by the client every time a fresh frame is copied into the shared
    // texture. The layer copies only when this changes (cheap idle path).
    uint64_t frameIndex;

    // Name passed to IDXGIResource1::CreateSharedHandle on the client side and to
    // ID3D11Device1::OpenSharedResourceByName on the layer side. Empty => no texture yet.
    wchar_t sharedName[MONOXR_NAME_LEN];
};

// Headset pose in WORLD (LOCAL) space, published by the layer every frame so
// the client can recenter overlays on where the user is actually sitting.
// Seqlock: the layer bumps seq to odd, writes the pose, bumps it to even; a
// reader retries if seq was odd or changed across its read. 0 = never published
// (no session yet, or a layer build that predates this block).
struct MonoXrHeadPose {
    uint32_t seq;
    uint32_t _pad0;
    float posX, posY, posZ;
    float quatX, quatY, quatZ, quatW;
    uint32_t caps;      // MonoXrLayerCaps, written by the layer; 0 = not published (or an older layer)
};

// What the runtime under the layer can do (MonoXrHeadPose.caps).
enum MonoXrLayerCaps {
    MONOXR_CAP_KNOWN    = 1,   // the layer has filled the rest in
    MONOXR_CAP_CYLINDER = 2,   // XR_KHR_composition_layer_cylinder is on: curved overlays are possible
};

// What the layer knows about eye tracking (MonoXrEyeGaze.state).
enum MonoXrGazeState {
    MONOXR_GAZE_UNKNOWN     = 0,  // layer has not published yet (also: layer predates this block)
    MONOXR_GAZE_UNSUPPORTED = 1,  // runtime or headset has no XR_EXT_eye_gaze_interaction
    MONOXR_GAZE_IDLE        = 2,  // supported, but the client has not asked for it (tracker not polled)
    MONOXR_GAZE_NO_SIGNAL   = 3,  // asked for, but no usable gaze this frame (eyes lost, not calibrated)
    MONOXR_GAZE_TRACKING    = 4,  // pose below is a live eye gaze ray
};

// Eye gaze in WORLD (LOCAL) space, from XR_EXT_eye_gaze_interaction. The ray
// starts at pos and runs along the quaternion's -Z axis, exactly like the head
// pose. Same seqlock as MonoXrHeadPose guards seq/state/pose. `request` is the
// one field the client writes (1 = poll the eye tracker): the layer leaves the
// tracker alone until it is asked, so nothing reads the user's eyes unless the
// app is actually using them.
struct MonoXrEyeGaze {
    uint32_t seq;
    uint32_t state;     // MonoXrGazeState
    uint32_t request;   // client -> layer
    float posX, posY, posZ;
    float quatX, quatY, quatZ, quatW;
};

// EXPERIMENTAL head-tracking filter. The layer applies it to the view poses the
// game gets back from xrLocateViews (the overlays and the published head pose
// keep using the real, unfiltered tracking). The client writes it; the layer
// ignores it while the client's heartbeat has stopped.
enum MonoXrTrackingFlags {
    MONOXR_TRACK_SMOOTH  = 1,   // low-pass the pose by `smoothing`
    MONOXR_TRACK_MINMOVE = 2,   // hold still until it moves further than the minimums
    MONOXR_TRACK_LOCKPOS = 4,   // keep only the rotation; put the head at `lock`
};

struct MonoXrTrackingControl {
    uint32_t flags;             // MonoXrTrackingFlags
    float smoothing;            // 0..1: 0 = none, 1 = heaviest (about a quarter second)
    float minMovePosMm;         // position must move at least this far (millimetres)
    float minMoveRotDeg;        // rotation must move at least this far (degrees)
    float lockX, lockY, lockZ;  // where LOCAL space puts the head when LOCKPOS is on (meters)
    uint32_t _pad0;
};

struct MonoXrControlBlock {
    uint32_t magic;         // MONOXR_MAGIC
    uint32_t version;       // MONOXR_VERSION
    uint32_t maxOverlays;   // MONOXR_MAX_OVERLAYS
    uint32_t clientPid;     // 0 when no client attached
    uint64_t clientHeartbeat;   // client bumps periodically; layer can detect death
    uint32_t layerActive;   // layer sets 1 while a session is running
    uint32_t layerPid;      // game's process id while layerActive; lets the client
                            // detect a game that died without clearing layerActive
    struct MonoXrOverlaySlot slots[MONOXR_MAX_OVERLAYS];

    // Appended after the slots (rather than grown into the header) so the
    // header/slot offsets are unchanged and a version-1 layer or client that
    // predates it keeps working — it simply never sees a head pose.
    struct MonoXrHeadPose headPose;

    // After the head pose for the same reason: a layer or client without it
    // maps the smaller block and simply never sees eye gaze.
    struct MonoXrEyeGaze eyeGaze;

    // After the eye gaze for the same reason.
    struct MonoXrTrackingControl tracking;
};

#pragma pack(pop)

// Layout guard: these sizes/offsets are hard-coded in shared/MonoXrAbi.cs.
#ifdef __cplusplus
#include <cstddef>
static_assert(sizeof(MonoXrOverlaySlot) == 204, "MonoXrOverlaySlot must match MonoXrAbi.cs SlotSize");
static_assert(sizeof(MonoXrHeadPose) == 40, "MonoXrHeadPose must match MonoXrAbi.cs HeadPoseSize");
static_assert(offsetof(MonoXrControlBlock, slots) == 32, "header must match MonoXrAbi.cs HeaderSize");
static_assert(offsetof(MonoXrControlBlock, headPose) == 3296, "headPose must match MonoXrAbi.cs HeadPoseOffset");
static_assert(sizeof(MonoXrEyeGaze) == 40, "MonoXrEyeGaze must match MonoXrAbi.cs EyeGazeSize");
static_assert(offsetof(MonoXrControlBlock, eyeGaze) == 3336, "eyeGaze must match MonoXrAbi.cs EyeGazeOffset");
static_assert(sizeof(MonoXrTrackingControl) == 32, "MonoXrTrackingControl must match MonoXrAbi.cs TrackingSize");
static_assert(offsetof(MonoXrControlBlock, tracking) == 3376, "tracking must match MonoXrAbi.cs TrackingOffset");
static_assert(sizeof(MonoXrControlBlock) == 3408, "MonoXrControlBlock must match MonoXrAbi.cs ControlSize");
#endif

// Keyed-mutex handshake on each shared texture:
//   producer (client) acquires KEY_PRODUCER, copies RT -> shared, releases KEY_CONSUMER
//   consumer (layer)  acquires KEY_CONSUMER, copies shared -> swapchain, releases KEY_PRODUCER
// Initial owner is the producer (created acquired at KEY_PRODUCER).
#define MONOXR_KEY_PRODUCER 0ull
#define MONOXR_KEY_CONSUMER 1ull

#endif // MONOXR_ABI_H
