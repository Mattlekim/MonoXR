// XR_APILAYER_NOVELTY_monoxr
//
// An OpenXR API layer that appends quad composition layers fed by an external
// MonoGame process. Each overlay slot in the Local\MonoXR-Control shared memory
// points at a named shared D3D11 texture; every frame the layer copies that
// texture into an XrSwapchain image and submits an XrCompositionLayerQuad with
// the pose/size the client requested.
//
// Graphics API: D3D11 only in this version (XR_KHR_D3D11_enable).

#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>

#include <cstddef>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <mutex>
#include <share.h>
#include <string>
#include <vector>

#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <openxr/openxr_loader_negotiation.h>

#include "monoxr_abi.h"

// ---------------------------------------------------------------------------
// Logging
//
// Verbose, step-by-step tracing. The log is appended (not truncated) so that
// multiple processes loading the layer (e.g. the OpenXR loader probing, then
// the game itself) all show up. Location, in order of preference:
//   1. %MONOXR_LOG%              (explicit override — a path we control)
//   2. %TEMP%\MonoXR\layer.log
//   3. C:\MonoXR\layer.log       (fallback if TEMP is unavailable)
// Every line is prefixed with a timestamp, the process id, and the process
// image name, so we can tell exactly which process loaded the layer.
// ---------------------------------------------------------------------------
static const char* ProcessName() {
    static char name[MAX_PATH] = {};
    if (!name[0]) {
        char full[MAX_PATH] = {};
        DWORD n = GetModuleFileNameA(nullptr, full, MAX_PATH);
        const char* base = full;
        for (DWORD i = 0; i < n; ++i) if (full[i] == '\\' || full[i] == '/') base = full + i + 1;
        strncpy_s(name, base, _TRUNCATE);
        if (!name[0]) strncpy_s(name, "?", _TRUNCATE);
    }
    return name;
}

static FILE* OpenLog() {
    // _fsopen with _SH_DENYNO so other processes (tail, editors) can read the
    // log while the game holds it open.
    char path[MAX_PATH];
    // 1. explicit override
    DWORD n = GetEnvironmentVariableA("MONOXR_LOG", path, MAX_PATH);
    if (n && n < MAX_PATH) {
        FILE* f = _fsopen(path, "a", _SH_DENYNO);
        if (f) return f;
    }
    // 2. %TEMP%\MonoXR\layer.log
    n = GetEnvironmentVariableA("TEMP", path, MAX_PATH);
    if (n && n < MAX_PATH) {
        std::string dir = std::string(path) + "\\MonoXR";
        CreateDirectoryA(dir.c_str(), nullptr);
        std::string file = dir + "\\layer.log";
        FILE* f = _fsopen(file.c_str(), "a", _SH_DENYNO);
        if (f) return f;
    }
    // 3. C:\MonoXR\layer.log
    CreateDirectoryA("C:\\MonoXR", nullptr);
    FILE* f = _fsopen("C:\\MonoXR\\layer.log", "a", _SH_DENYNO);
    if (f) return f;
    return nullptr;
}

static void LogLine(const char* fmt, ...) {
    char msg[1024];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(msg, sizeof(msg), _TRUNCATE, fmt, ap);
    va_end(ap);

    SYSTEMTIME st;
    GetLocalTime(&st);
    char line[1200];
    _snprintf_s(line, sizeof(line), _TRUNCATE,
                "%02d:%02d:%02d.%03d [pid %lu %s] %s",
                st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                GetCurrentProcessId(), ProcessName(), msg);

    OutputDebugStringA("[MonoXR] ");
    OutputDebugStringA(line);
    OutputDebugStringA("\n");

    static FILE* f = nullptr;
    static bool tried = false;
    if (!tried) { tried = true; f = OpenLog(); }
    if (f) { fprintf(f, "%s\n", line); fflush(f); }
}

// ---------------------------------------------------------------------------
// Down-chain dispatch table
// ---------------------------------------------------------------------------
static PFN_xrGetInstanceProcAddr        g_nextGipa = nullptr;

static PFN_xrCreateSession               down_xrCreateSession = nullptr;
static PFN_xrDestroySession              down_xrDestroySession = nullptr;
static PFN_xrEndFrame                    down_xrEndFrame = nullptr;
static PFN_xrCreateReferenceSpace        down_xrCreateReferenceSpace = nullptr;
static PFN_xrDestroySpace                down_xrDestroySpace = nullptr;
static PFN_xrLocateSpace                 down_xrLocateSpace = nullptr;
static PFN_xrEnumerateSwapchainFormats   down_xrEnumerateSwapchainFormats = nullptr;
static PFN_xrCreateSwapchain             down_xrCreateSwapchain = nullptr;
static PFN_xrDestroySwapchain            down_xrDestroySwapchain = nullptr;
static PFN_xrEnumerateSwapchainImages    down_xrEnumerateSwapchainImages = nullptr;
static PFN_xrAcquireSwapchainImage       down_xrAcquireSwapchainImage = nullptr;
static PFN_xrWaitSwapchainImage          down_xrWaitSwapchainImage = nullptr;
static PFN_xrReleaseSwapchainImage       down_xrReleaseSwapchainImage = nullptr;

// Eye gaze (XR_EXT_eye_gaze_interaction). The layer owns one pose action bound
// to the gaze path; the app's own action sets are left alone and ours is merged
// into its xrAttachSessionActionSets / xrSyncActions calls.
static PFN_xrGetSystemProperties             down_xrGetSystemProperties = nullptr;
static PFN_xrStringToPath                    down_xrStringToPath = nullptr;
static PFN_xrCreateActionSet                 down_xrCreateActionSet = nullptr;
static PFN_xrDestroyActionSet                down_xrDestroyActionSet = nullptr;
static PFN_xrCreateAction                    down_xrCreateAction = nullptr;
static PFN_xrDestroyAction                   down_xrDestroyAction = nullptr;
static PFN_xrSuggestInteractionProfileBindings down_xrSuggestInteractionProfileBindings = nullptr;
static PFN_xrAttachSessionActionSets         down_xrAttachSessionActionSets = nullptr;
static PFN_xrSyncActions                     down_xrSyncActions = nullptr;
static PFN_xrCreateActionSpace               down_xrCreateActionSpace = nullptr;
static PFN_xrGetActionStatePose              down_xrGetActionStatePose = nullptr;

static XrInstance g_instance = XR_NULL_HANDLE;
static bool       g_eyeExtEnabled = false;   // XR_EXT_eye_gaze_interaction is on for this instance
static bool       g_cylinderExtEnabled = false; // XR_KHR_composition_layer_cylinder is on for this instance
static XrPath     g_eyeProfilePath = XR_NULL_PATH;
static XrPath     g_eyeGazePosePath = XR_NULL_PATH;

// ---------------------------------------------------------------------------
// Per-overlay GPU resources, lazily built to match a control-block slot.
// ---------------------------------------------------------------------------
struct SlotResources {
    XrSwapchain swapchain = XR_NULL_HANDLE;
    std::vector<ID3D11Texture2D*> images;   // owned by runtime, do not release
    ID3D11Texture2D* sharedTex = nullptr;   // opened from client's named handle
    IDXGIKeyedMutex* mutex = nullptr;

    // cached to detect when the client changes the texture
    wchar_t  name[MONOXR_NAME_LEN] = {};
    uint32_t width = 0, height = 0, format = 0;
    uint64_t lastFrameIndex = ~0ull;
    bool hasContent = false;    // at least one frame copied into the swapchain

    void releaseShared() {
        if (mutex) { mutex->Release(); mutex = nullptr; }
        if (sharedTex) { sharedTex->Release(); sharedTex = nullptr; }
        // width/height/format cache the SWAPCHAIN dimensions, not the shared
        // texture — clearing them here forced a needless swapchain rebuild on
        // every client reconnect, which reset hasContent and dropped the quad.
        name[0] = 0; lastFrameIndex = ~0ull;
        hasContent = false;
    }
};

struct SessionState {
    XrSession session = XR_NULL_HANDLE;
    ID3D11Device* device = nullptr;         // app's device (captured, not owned)
    ID3D11DeviceContext* context = nullptr; // app's immediate context (not owned)
    XrSpace worldSpace = XR_NULL_HANDLE;    // LOCAL
    XrSpace headSpace = XR_NULL_HANDLE;     // VIEW
    SlotResources slots[MONOXR_MAX_OVERLAYS];

    // Shared control block
    HANDLE mapping = nullptr;
    MonoXrControlBlock* control = nullptr;
    bool controlHasHeadPose = false;        // mapping is big enough for control->headPose
    bool controlHasEyeGaze = false;         // ... and for control->eyeGaze
    int openRetry = 0;

    // Eye gaze. gazeSupported: the system reports eye gaze interaction and our
    // action exists. gazeAttached: our action set went into the app's
    // xrAttachSessionActionSets, so the action space is usable. gazeSynced: our
    // set has been part of at least one successful xrSyncActions.
    XrActionSet gazeSet = XR_NULL_HANDLE;
    XrAction    gazeAction = XR_NULL_HANDLE;
    XrSpace     gazeSpace = XR_NULL_HANDLE;
    bool gazeSupported = false;
    bool gazeAttached = false;
    bool gazeSynced = false;

    // Client liveness, for eye gaze: a request left behind by a client that
    // died must not keep the eye tracker running.
    uint64_t lastHeartbeat = 0;
    ULONGLONG lastHeartbeatTick = 0;
};

// Bindings the app itself suggested for the eye gaze profile. Suggesting a
// profile again replaces the earlier suggestion, so ours has to carry theirs.
static std::vector<XrActionSuggestedBinding> g_appEyeBindings;

static std::mutex g_lock;
static SessionState g_session;   // single-session layer (typical for overlays)

// ---------------------------------------------------------------------------
// Control-block mapping (created by the client; we just open it)
// ---------------------------------------------------------------------------
static void TryOpenControl(SessionState& s) {
    if (s.control) return;
    if (s.openRetry-- > 0) return;      // throttle re-open attempts
    s.openRetry = 90;                   // ~ once per second at 90Hz

    HANDLE h = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, MONOXR_CONTROL_NAME);
    if (!h) return;
    // A client that predates the head-pose block created a smaller mapping;
    // fall back to the original size and just don't publish the pose.
    // Likewise for eye gaze, which sits after the head pose.
    bool hasHeadPose = true;
    bool hasEyeGaze = true;
    void* view = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(MonoXrControlBlock));
    if (!view) {
        hasEyeGaze = false;
        view = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, offsetof(MonoXrControlBlock, eyeGaze));
    }
    if (!view) {
        hasHeadPose = false;
        view = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, offsetof(MonoXrControlBlock, headPose));
    }
    if (!view) { CloseHandle(h); return; }

    auto* cb = reinterpret_cast<MonoXrControlBlock*>(view);
    if (cb->magic != MONOXR_MAGIC || cb->version != MONOXR_VERSION) {
        LogLine("control block magic/version mismatch (%08x/%u)", cb->magic, cb->version);
        UnmapViewOfFile(view); CloseHandle(h); return;
    }
    s.mapping = h;
    s.control = cb;
    s.controlHasHeadPose = hasHeadPose;
    s.controlHasEyeGaze = hasEyeGaze;
    s.control->layerPid = GetCurrentProcessId();
    s.control->layerActive = 1;
    LogLine("attached to MonoXR control block, client pid=%u", cb->clientPid);
}

static void CloseControl(SessionState& s) {
    if (s.control) { s.control->layerActive = 0; s.control->layerPid = 0; UnmapViewOfFile(s.control); s.control = nullptr; }
    if (s.mapping) { CloseHandle(s.mapping); s.mapping = nullptr; }
}

// ---------------------------------------------------------------------------
// Swapchain / shared-texture management for one slot
// ---------------------------------------------------------------------------
// Pick a swapchain format the runtime actually supports. The client renders
// R8G8B8A8_UNORM (28); some runtimes (e.g. SteamVR) only expose the sRGB
// variant (29) for the color path. Both are copy-compatible with UNORM data
// via CopyResource, so we fall back to sRGB, then to whatever the runtime
// lists first, rather than failing to create the swapchain (which would make
// the overlay silently invisible).
static int64_t ChooseSwapchainFormat(SessionState& s, uint32_t requested) {
    uint32_t count = 0;
    if (XR_FAILED(down_xrEnumerateSwapchainFormats(s.session, 0, &count, nullptr)) || count == 0)
        return static_cast<int64_t>(requested); // can't enumerate; try as-is
    std::vector<int64_t> formats(count);
    down_xrEnumerateSwapchainFormats(s.session, count, &count, formats.data());

    auto has = [&](int64_t f) {
        for (auto x : formats) if (x == f) return true;
        return false;
    };

    const int64_t DXGI_R8G8B8A8_UNORM = 28;
    const int64_t DXGI_R8G8B8A8_UNORM_SRGB = 29;

    if (has(static_cast<int64_t>(requested))) return static_cast<int64_t>(requested);
    if (requested == DXGI_R8G8B8A8_UNORM && has(DXGI_R8G8B8A8_UNORM_SRGB)) {
        LogLine("format %u unsupported; falling back to R8G8B8A8_UNORM_SRGB (29)", requested);
        return DXGI_R8G8B8A8_UNORM_SRGB;
    }
    LogLine("format %u unsupported; falling back to runtime format[0]=%lld (of %u offered)",
            requested, (long long)formats[0], count);
    return formats[0];
}

static bool EnsureSwapchain(SessionState& s, SlotResources& r,
                            const MonoXrOverlaySlot& slot) {
    if (r.swapchain != XR_NULL_HANDLE &&
        r.width == slot.texWidth && r.height == slot.texHeight && r.format == slot.format)
        return true;

    if (r.swapchain != XR_NULL_HANDLE) {
        down_xrDestroySwapchain(r.swapchain);
        r.swapchain = XR_NULL_HANDLE;
        r.images.clear();
        r.lastFrameIndex = ~0ull;
        r.hasContent = false;
    }

    int64_t chosenFormat = ChooseSwapchainFormat(s, slot.format);

    XrSwapchainCreateInfo ci{ XR_TYPE_SWAPCHAIN_CREATE_INFO };
    ci.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
    ci.format = chosenFormat;
    ci.sampleCount = 1;
    ci.width = slot.texWidth;
    ci.height = slot.texHeight;
    ci.faceCount = 1;
    ci.arraySize = 1;
    ci.mipCount = 1;

    XrResult res = down_xrCreateSwapchain(s.session, &ci, &r.swapchain);
    if (XR_FAILED(res)) { LogLine("xrCreateSwapchain failed %d (fmt=%u)", res, slot.format); return false; }

    uint32_t count = 0;
    down_xrEnumerateSwapchainImages(r.swapchain, 0, &count, nullptr);
    std::vector<XrSwapchainImageD3D11KHR> imgs(count, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
    down_xrEnumerateSwapchainImages(r.swapchain, count, &count,
        reinterpret_cast<XrSwapchainImageBaseHeader*>(imgs.data()));
    r.images.clear();
    for (auto& i : imgs) r.images.push_back(i.texture);

    r.width = slot.texWidth; r.height = slot.texHeight; r.format = slot.format;
    LogLine("created swapchain %ux%u requestedFmt=%u actualFmt=%lld images=%u",
            r.width, r.height, r.format, (long long)chosenFormat, count);
    return true;
}

static bool EnsureSharedTexture(SessionState& s, SlotResources& r,
                                const MonoXrOverlaySlot& slot) {
    if (r.sharedTex && wcsncmp(r.name, slot.sharedName, MONOXR_NAME_LEN) == 0)
        return true;

    r.releaseShared();
    if (slot.sharedName[0] == 0) return false;

    ID3D11Device1* dev1 = nullptr;
    if (FAILED(s.device->QueryInterface(__uuidof(ID3D11Device1), (void**)&dev1)) || !dev1)
        return false;

    ID3D11Texture2D* tex = nullptr;
    HRESULT hr = dev1->OpenSharedResourceByName(slot.sharedName, DXGI_SHARED_RESOURCE_READ,
                                                __uuidof(ID3D11Texture2D), (void**)&tex);
    dev1->Release();
    if (FAILED(hr) || !tex) { LogLine("OpenSharedResourceByName failed hr=0x%08x", hr); return false; }

    IDXGIKeyedMutex* km = nullptr;
    tex->QueryInterface(__uuidof(IDXGIKeyedMutex), (void**)&km); // may be null if no keyed mutex

    r.sharedTex = tex;
    r.mutex = km;
    wcsncpy_s(r.name, slot.sharedName, MONOXR_NAME_LEN - 1);
    LogLine("opened shared texture '%ls' (keyedMutex=%d)", slot.sharedName, km ? 1 : 0);
    return true;
}

// Copy shared texture -> next swapchain image, but ONLY when the client has
// published a new frame (slot.frameIndex changed) since our last copy. When
// nothing new arrived, the quad simply re-references the swapchain — the
// runtime keeps showing the most recently released image at zero cost.
// This must never stall the game's render thread, so the keyed mutex is taken
// with a 0 timeout: if the client is mid-update we keep last frame's image
// and try again next frame.
static bool RenderSlot(SessionState& s, SlotResources& r, const MonoXrOverlaySlot& slot,
                       XrSwapchainSubImage& outSub) {
    if (!EnsureSwapchain(s, r, slot)) return false;
    if (!EnsureSharedTexture(s, r, slot)) return false;

    // frameIndex == 0 means the client has not published anything into this
    // texture yet (slots are reused, so a freshly created overlay resets it) —
    // copying now would show an uninitialized texture.
    if (slot.frameIndex != 0 && slot.frameIndex != r.lastFrameIndex) {
        // Non-blocking producer/consumer handoff on the shared texture. If
        // there is no keyed mutex we copy unsynchronised. AcquireSync returns
        // WAIT_TIMEOUT (0x102) when the client holds the mutex — a POSITIVE
        // HRESULT, so SUCCEEDED() would wrongly treat it as acquired and we
        // would copy mid-write and corrupt the mutex state with an unmatched
        // ReleaseSync. Only S_OK means we actually own it.
        bool locked = !r.mutex || r.mutex->AcquireSync(MONOXR_KEY_CONSUMER, 0) == S_OK;
        if (locked) {
            uint32_t idx = 0;
            XrSwapchainImageAcquireInfo ai{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO };
            if (XR_SUCCEEDED(down_xrAcquireSwapchainImage(r.swapchain, &ai, &idx))) {
                XrSwapchainImageWaitInfo wi{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO };
                wi.timeout = XR_INFINITE_DURATION;
                if (XR_SUCCEEDED(down_xrWaitSwapchainImage(r.swapchain, &wi))) {
                    s.context->CopyResource(r.images[idx], r.sharedTex);
                    r.lastFrameIndex = slot.frameIndex;
                    r.hasContent = true;
                }
                XrSwapchainImageReleaseInfo ri{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
                down_xrReleaseSwapchainImage(r.swapchain, &ri);
            }
            if (r.mutex) r.mutex->ReleaseSync(MONOXR_KEY_PRODUCER);
        }
    }

    if (!r.hasContent) return false; // nothing ever copied yet — no quad

    outSub.swapchain = r.swapchain;
    outSub.imageArrayIndex = 0;
    outSub.imageRect = { {0,0}, { (int32_t)slot.texWidth, (int32_t)slot.texHeight } };
    return true;
}

// Publish the headset pose (VIEW located in LOCAL at this frame's display
// time) so the client can recenter world overlays on the user. Seqlock writer:
// the interlocked bumps are full barriers, so a reader never sees a torn pose
// under an even, unchanged seq.
static void PublishHeadPose(SessionState& s, XrTime displayTime) {
    if (!s.controlHasHeadPose || !down_xrLocateSpace || !s.headSpace || !s.worldSpace) return;

    XrSpaceLocation loc{ XR_TYPE_SPACE_LOCATION };
    if (XR_FAILED(down_xrLocateSpace(s.headSpace, s.worldSpace, displayTime, &loc))) return;
    const XrSpaceLocationFlags need = XR_SPACE_LOCATION_POSITION_VALID_BIT |
                                      XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
    if ((loc.locationFlags & need) != need) return;

    MonoXrHeadPose& h = s.control->headPose;
    volatile LONG* seq = reinterpret_cast<volatile LONG*>(&h.seq);
    if (InterlockedIncrement(seq) % 2 == 0) InterlockedIncrement(seq); // resync to odd
    h.posX = loc.pose.position.x; h.posY = loc.pose.position.y; h.posZ = loc.pose.position.z;
    h.quatX = loc.pose.orientation.x; h.quatY = loc.pose.orientation.y;
    h.quatZ = loc.pose.orientation.z; h.quatW = loc.pose.orientation.w;
    if (InterlockedIncrement(seq) == 0) InterlockedExchangeAdd(seq, 2); // 0 means "never published"
}

// True while the client is alive (its heartbeat moved within the last couple of
// seconds) and has asked for eye gaze.
static bool EyeGazeRequested(SessionState& s) {
    if (!s.control || !s.controlHasEyeGaze || !s.control->eyeGaze.request) return false;
    ULONGLONG now = GetTickCount64();
    uint64_t hb = s.control->clientHeartbeat;
    if (hb != s.lastHeartbeat) { s.lastHeartbeat = hb; s.lastHeartbeatTick = now; }
    return now - s.lastHeartbeatTick < 2000;
}

// Publish the eye gaze ray (same seqlock as the head pose) and what the layer
// knows about eye tracking. The tracker is only polled while the client has set
// `request`; until then the state just says whether it could be.
static void PublishEyeGaze(SessionState& s, XrTime displayTime) {
    if (!s.controlHasEyeGaze) return;
    MonoXrEyeGaze& g = s.control->eyeGaze;

    uint32_t state = MONOXR_GAZE_UNSUPPORTED;
    XrPosef pose{};
    pose.orientation.w = 1.0f;

    if (s.gazeSupported && s.gazeAttached) {
        if (!EyeGazeRequested(s)) {
            state = MONOXR_GAZE_IDLE;
        } else {
            state = MONOXR_GAZE_NO_SIGNAL;
            if (s.gazeSynced && s.gazeSpace && s.worldSpace) {
                XrActionStateGetInfo gi{ XR_TYPE_ACTION_STATE_GET_INFO };
                gi.action = s.gazeAction;
                XrActionStatePose st{ XR_TYPE_ACTION_STATE_POSE };
                if (XR_SUCCEEDED(down_xrGetActionStatePose(s.session, &gi, &st)) && st.isActive) {
                    XrSpaceLocation loc{ XR_TYPE_SPACE_LOCATION };
                    const XrSpaceLocationFlags need = XR_SPACE_LOCATION_POSITION_VALID_BIT |
                                                      XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
                    if (XR_SUCCEEDED(down_xrLocateSpace(s.gazeSpace, s.worldSpace, displayTime, &loc)) &&
                        (loc.locationFlags & need) == need) {
                        pose = loc.pose;
                        state = MONOXR_GAZE_TRACKING;
                    }
                }
            }
        }
    }

    volatile LONG* seq = reinterpret_cast<volatile LONG*>(&g.seq);
    if (InterlockedIncrement(seq) % 2 == 0) InterlockedIncrement(seq); // resync to odd
    g.state = state;
    g.posX = pose.position.x; g.posY = pose.position.y; g.posZ = pose.position.z;
    g.quatX = pose.orientation.x; g.quatY = pose.orientation.y;
    g.quatZ = pose.orientation.z; g.quatW = pose.orientation.w;
    if (InterlockedIncrement(seq) == 0) InterlockedExchangeAdd(seq, 2); // 0 means "never published"

    static uint32_t lastState = ~0u;
    if (state != lastState) {
        lastState = state;
        LogLine("eye gaze state -> %u (1 unsupported, 2 idle, 3 no signal, 4 tracking)", state);
    }
}

// ---------------------------------------------------------------------------
// Hooked entry points
// ---------------------------------------------------------------------------
static XrResult XRAPI_CALL MonoXr_xrEndFrame(XrSession session, const XrFrameEndInfo* frameEndInfo) {
    std::lock_guard<std::mutex> guard(g_lock);
    SessionState& s = g_session;

    if (s.session != session || !s.device || !s.context)
        return down_xrEndFrame(session, frameEndInfo);

    TryOpenControl(s);
    if (!s.control)
        return down_xrEndFrame(session, frameEndInfo);

    // Re-assert liveness every frame so the client's view self-heals even if
    // something cleared these (e.g. a client that wrongly decided we died).
    s.control->layerActive = 1;
    s.control->layerPid = GetCurrentProcessId();

    PublishHeadPose(s, frameEndInfo->displayTime);
    PublishEyeGaze(s, frameEndInfo->displayTime);

    // Storage must outlive the down-call; reserve so pointers stay stable.
    static std::vector<XrCompositionLayerQuad> quads;
    static std::vector<XrCompositionLayerCylinderKHR> cylinders;
    quads.clear();
    cylinders.clear();
    quads.reserve(MONOXR_MAX_OVERLAYS);
    cylinders.reserve(MONOXR_MAX_OVERLAYS);
    std::vector<const XrCompositionLayerBaseHeader*> layers;

    // Keep the app's own layers first (world renders under the overlays).
    for (uint32_t i = 0; i < frameEndInfo->layerCount; ++i)
        layers.push_back(frameEndInfo->layers[i]);

    for (uint32_t i = 0; i < MONOXR_MAX_OVERLAYS; ++i) {
        MonoXrOverlaySlot slot = s.control->slots[i]; // copy snapshot
        SlotResources& r = s.slots[i];

        if (!slot.active || !slot.visible) {
            if (r.swapchain != XR_NULL_HANDLE) { /* keep for reuse */ }
            continue;
        }
        if (slot.texWidth == 0 || slot.texHeight == 0) continue;

        XrSwapchainSubImage sub{};
        if (!RenderSlot(s, r, slot, sub)) continue;

        // A curved overlay goes out as a cylinder layer when the runtime has them;
        // otherwise it stays the flat quad it always was.
        if (g_cylinderExtEnabled && slot.curveRadius > 0.05f && slot.sizeX > 0.0f && slot.sizeY > 0.0f) {
            XrCompositionLayerCylinderKHR c{ XR_TYPE_COMPOSITION_LAYER_CYLINDER_KHR };
            c.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT |
                           XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT;
            c.space = (slot.space == MONOXR_SPACE_HEAD) ? s.headSpace : s.worldSpace;
            c.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
            c.subImage = sub;
            // The cylinder layer's pose is the cylinder's centre; the surface sits
            // `radius` in front of it (-Z). Move the centre back from the quad's
            // centre along the quad's +Z so the surface passes through the quad.
            const float qx = slot.quatX, qy = slot.quatY, qz = slot.quatZ, qw = slot.quatW;
            const float R = slot.curveRadius;
            // Rotate (0, 0, R) by the quaternion: the third column of its matrix, times R.
            const float bx = 2.0f * (qx * qz + qw * qy) * R;
            const float by = 2.0f * (qy * qz - qw * qx) * R;
            const float bz = (1.0f - 2.0f * (qx * qx + qy * qy)) * R;
            c.pose.position = { slot.posX + bx, slot.posY + by, slot.posZ + bz };
            c.pose.orientation = { qx, qy, qz, qw };
            c.radius = R;
            c.centralAngle = std::fmin(slot.sizeX / R, 6.2831f); // arc length / radius, at most a full turn
            c.aspectRatio = slot.sizeX / slot.sizeY;   // arc length / height
            cylinders.push_back(c);
            layers.push_back(reinterpret_cast<const XrCompositionLayerBaseHeader*>(&cylinders.back()));
            continue;
        }

        XrCompositionLayerQuad q{ XR_TYPE_COMPOSITION_LAYER_QUAD };
        q.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT |
                       XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT;
        q.space = (slot.space == MONOXR_SPACE_HEAD) ? s.headSpace : s.worldSpace;
        q.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
        q.subImage = sub;
        q.pose.position = { slot.posX, slot.posY, slot.posZ };
        q.pose.orientation = { slot.quatX, slot.quatY, slot.quatZ, slot.quatW };
        q.size = { slot.sizeX, slot.sizeY };

        quads.push_back(q);
        layers.push_back(reinterpret_cast<const XrCompositionLayerBaseHeader*>(&quads.back()));
    }

    XrFrameEndInfo patched = *frameEndInfo;
    patched.layerCount = (uint32_t)layers.size();
    patched.layers = layers.data();
    XrResult efRes = down_xrEndFrame(session, &patched);

    // Log only when the submission actually changes (quad count or compositor
    // result) — steady-state frames stay silent instead of repeating the same
    // line every second. The quad detail still prints on each change, so a
    // "quad submitted but nothing in VR" case remains diagnosable.
    static size_t lastQuadCount = (size_t)-1;
    static size_t lastCylinderCount = (size_t)-1;
    static XrResult lastEfRes = XR_SUCCESS;
    static bool loggedOnce = false;
    if (!loggedOnce || quads.size() != lastQuadCount || cylinders.size() != lastCylinderCount || efRes != lastEfRes) {
        loggedOnce = true;
        lastQuadCount = quads.size();
        lastCylinderCount = cylinders.size();
        lastEfRes = efRes;
        LogLine("endFrame: appManaged=%u quads=%zu cylinders=%zu totalLayers=%zu down_xrEndFrame=%d",
                frameEndInfo->layerCount, quads.size(), cylinders.size(), layers.size(), efRes);
        for (size_t qi = 0; qi < quads.size(); ++qi) {
            const auto& q = quads[qi];
            const char* sp = (q.space == s.headSpace) ? "HEAD"
                           : (q.space == s.worldSpace) ? "WORLD" : "?";
            LogLine("  quad[%zu] space=%s pos=(%.2f,%.2f,%.2f) size=(%.2f,%.2f) "
                    "quat=(%.2f,%.2f,%.2f,%.2f) flags=0x%llx",
                    qi, sp, q.pose.position.x, q.pose.position.y, q.pose.position.z,
                    q.size.width, q.size.height,
                    q.pose.orientation.x, q.pose.orientation.y,
                    q.pose.orientation.z, q.pose.orientation.w,
                    (unsigned long long)q.layerFlags);
        }
    }

    return efRes;
}

// Create the layer's gaze pose action and bind it to the eye gaze path. Done
// per session, before the app can attach action sets (the session is brand
// new), so the action is always eligible to go into the app's attach call.
static void SetUpEyeGaze(SessionState& s, XrSystemId systemId) {
    if (!g_eyeExtEnabled || !g_instance) return;

    XrSystemEyeGazeInteractionPropertiesEXT eye{ XR_TYPE_SYSTEM_EYE_GAZE_INTERACTION_PROPERTIES_EXT };
    XrSystemProperties props{ XR_TYPE_SYSTEM_PROPERTIES };
    props.next = &eye;
    if (XR_FAILED(down_xrGetSystemProperties(g_instance, systemId, &props)) ||
        !eye.supportsEyeGazeInteraction) {
        LogLine("eye gaze: system does not support eye gaze interaction");
        return;
    }

    XrActionSetCreateInfo setInfo{ XR_TYPE_ACTION_SET_CREATE_INFO };
    strcpy_s(setInfo.actionSetName, "monoxr_gaze");
    strcpy_s(setInfo.localizedActionSetName, "MonoXR gaze");
    XrResult r = down_xrCreateActionSet(g_instance, &setInfo, &s.gazeSet);
    if (XR_FAILED(r)) { LogLine("eye gaze: xrCreateActionSet failed (%d)", r); s.gazeSet = XR_NULL_HANDLE; return; }

    XrActionCreateInfo actInfo{ XR_TYPE_ACTION_CREATE_INFO };
    actInfo.actionType = XR_ACTION_TYPE_POSE_INPUT;
    strcpy_s(actInfo.actionName, "gaze");
    strcpy_s(actInfo.localizedActionName, "Eye gaze");
    r = down_xrCreateAction(s.gazeSet, &actInfo, &s.gazeAction);
    if (XR_FAILED(r)) {
        LogLine("eye gaze: xrCreateAction failed (%d)", r);
        down_xrDestroyActionSet(s.gazeSet);
        s.gazeSet = XR_NULL_HANDLE; s.gazeAction = XR_NULL_HANDLE;
        return;
    }

    down_xrStringToPath(g_instance, "/interaction_profiles/ext/eye_gaze_interaction", &g_eyeProfilePath);
    down_xrStringToPath(g_instance, "/user/eyes_ext/input/gaze_ext/pose", &g_eyeGazePosePath);

    std::vector<XrActionSuggestedBinding> bindings = g_appEyeBindings;
    bindings.push_back({ s.gazeAction, g_eyeGazePosePath });
    XrInteractionProfileSuggestedBinding sug{ XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING };
    sug.interactionProfile = g_eyeProfilePath;
    sug.countSuggestedBindings = (uint32_t)bindings.size();
    sug.suggestedBindings = bindings.data();
    r = down_xrSuggestInteractionProfileBindings(g_instance, &sug);
    if (XR_FAILED(r)) {
        LogLine("eye gaze: suggesting bindings failed (%d)", r);
        down_xrDestroyAction(s.gazeAction);
        down_xrDestroyActionSet(s.gazeSet);
        s.gazeSet = XR_NULL_HANDLE; s.gazeAction = XR_NULL_HANDLE;
        return;
    }

    s.gazeSupported = true;
    LogLine("eye gaze: supported; gaze action ready");
}

// The app records its action sets itself; ours has to ride along or it never
// gets attached (a session takes exactly one attach call).
static XrResult XRAPI_CALL MonoXr_xrAttachSessionActionSets(XrSession session,
                                                            const XrSessionActionSetsAttachInfo* info) {
    XrActionSet extra = XR_NULL_HANDLE;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        if (g_session.session == session && g_session.gazeSupported && !g_session.gazeAttached)
            extra = g_session.gazeSet;
    }
    if (!extra || !info) return down_xrAttachSessionActionSets(session, info);

    std::vector<XrActionSet> sets(info->actionSets, info->actionSets + info->countActionSets);
    sets.push_back(extra);
    XrSessionActionSetsAttachInfo patched = *info;
    patched.countActionSets = (uint32_t)sets.size();
    patched.actionSets = sets.data();
    XrResult res = down_xrAttachSessionActionSets(session, &patched);
    if (XR_FAILED(res)) return res;

    std::lock_guard<std::mutex> guard(g_lock);
    SessionState& s = g_session;
    if (s.session == session) {
        XrActionSpaceCreateInfo sp{ XR_TYPE_ACTION_SPACE_CREATE_INFO };
        sp.action = s.gazeAction;
        sp.poseInActionSpace.orientation.w = 1.0f;
        XrResult sr = down_xrCreateActionSpace(session, &sp, &s.gazeSpace);
        s.gazeAttached = XR_SUCCEEDED(sr);
        LogLine("eye gaze: action set attached; action space %s (%d)", s.gazeAttached ? "ok" : "FAILED", sr);
    }
    return res;
}

// Poll the eye tracker only while the client wants it.
static XrResult XRAPI_CALL MonoXr_xrSyncActions(XrSession session, const XrActionsSyncInfo* syncInfo) {
    XrActionSet extra = XR_NULL_HANDLE;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        SessionState& s = g_session;
        if (s.session == session && s.gazeAttached && EyeGazeRequested(s))
            extra = s.gazeSet;
    }
    if (!extra || !syncInfo) return down_xrSyncActions(session, syncInfo);

    std::vector<XrActiveActionSet> sets(syncInfo->activeActionSets,
                                        syncInfo->activeActionSets + syncInfo->countActiveActionSets);
    sets.push_back({ extra, XR_NULL_PATH });
    XrActionsSyncInfo patched = *syncInfo;
    patched.countActiveActionSets = (uint32_t)sets.size();
    patched.activeActionSets = sets.data();
    XrResult res = down_xrSyncActions(session, &patched);
    if (XR_SUCCEEDED(res)) {
        std::lock_guard<std::mutex> guard(g_lock);
        if (g_session.session == session) g_session.gazeSynced = true;
    }
    return res;
}

// If the app binds the eye gaze profile itself, ours must be included or the
// app's call would replace the layer's binding.
static XrResult XRAPI_CALL MonoXr_xrSuggestInteractionProfileBindings(
        XrInstance instance, const XrInteractionProfileSuggestedBinding* suggested) {
    if (!suggested || !g_eyeExtEnabled || !down_xrStringToPath)
        return down_xrSuggestInteractionProfileBindings(instance, suggested);

    XrPath eyeProfile = XR_NULL_PATH;
    down_xrStringToPath(instance, "/interaction_profiles/ext/eye_gaze_interaction", &eyeProfile);
    if (eyeProfile == XR_NULL_PATH || suggested->interactionProfile != eyeProfile)
        return down_xrSuggestInteractionProfileBindings(instance, suggested);

    std::vector<XrActionSuggestedBinding> bindings(
        suggested->suggestedBindings, suggested->suggestedBindings + suggested->countSuggestedBindings);
    XrAction ours = XR_NULL_HANDLE;
    XrPath oursPath = XR_NULL_PATH;
    {
        std::lock_guard<std::mutex> guard(g_lock);
        g_appEyeBindings = bindings;   // kept for a session created after this call
        if (g_session.gazeSupported && !g_session.gazeAttached) {
            ours = g_session.gazeAction;
            oursPath = g_eyeGazePosePath;
        }
    }
    if (!ours) return down_xrSuggestInteractionProfileBindings(instance, suggested);

    bindings.push_back({ ours, oursPath });
    XrInteractionProfileSuggestedBinding patched = *suggested;
    patched.countSuggestedBindings = (uint32_t)bindings.size();
    patched.suggestedBindings = bindings.data();
    return down_xrSuggestInteractionProfileBindings(instance, &patched);
}

static XrResult XRAPI_CALL MonoXr_xrCreateSession(XrInstance instance,
                                                  const XrSessionCreateInfo* createInfo,
                                                  XrSession* session) {
    // Log the graphics binding type in the next chain so we can see whether the
    // app is D3D11 (what we support) or something else (D3D12/Vulkan/OpenGL).
    int firstNextType = -1;
    if (createInfo && createInfo->next)
        firstNextType = (int)((const XrBaseInStructure*)createInfo->next)->type;
    LogLine("xrCreateSession called; createFlags=0x%llx next[0].type=%d",
            createInfo ? (unsigned long long)createInfo->createFlags : 0ull, firstNextType);

    XrResult res = down_xrCreateSession(instance, createInfo, session);
    LogLine("  down_xrCreateSession returned %d", res);
    if (XR_FAILED(res)) return res;

    std::lock_guard<std::mutex> guard(g_lock);
    SessionState& s = g_session;
    s = SessionState{};
    s.session = *session;

    // Find the D3D11 graphics binding in the next chain.
    for (const XrBaseInStructure* b = (const XrBaseInStructure*)createInfo->next; b; b = b->next) {
        if (b->type == XR_TYPE_GRAPHICS_BINDING_D3D11_KHR) {
            auto* g = reinterpret_cast<const XrGraphicsBindingD3D11KHR*>(b);
            s.device = g->device;
            if (s.device) s.device->GetImmediateContext(&s.context);
            LogLine("captured D3D11 device %p", (void*)s.device);
            break;
        }
    }
    if (!s.device) LogLine("WARNING: no D3D11 graphics binding found; overlays disabled for this session");

    // Reference spaces for world- and head-locked overlays.
    XrReferenceSpaceCreateInfo rs{ XR_TYPE_REFERENCE_SPACE_CREATE_INFO };
    rs.poseInReferenceSpace.orientation.w = 1.0f;
    rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    down_xrCreateReferenceSpace(*session, &rs, &s.worldSpace);
    rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW;
    down_xrCreateReferenceSpace(*session, &rs, &s.headSpace);

    SetUpEyeGaze(s, createInfo->systemId);

    TryOpenControl(s);
    return res;
}

static XrResult XRAPI_CALL MonoXr_xrDestroySession(XrSession session) {
    std::lock_guard<std::mutex> guard(g_lock);
    SessionState& s = g_session;
    if (s.session == session) {
        for (auto& r : s.slots) {
            if (r.swapchain != XR_NULL_HANDLE) down_xrDestroySwapchain(r.swapchain);
            r.releaseShared();
        }
        if (s.worldSpace) down_xrDestroySpace(s.worldSpace);
        if (s.headSpace) down_xrDestroySpace(s.headSpace);
        if (s.gazeSpace) down_xrDestroySpace(s.gazeSpace);
        if (s.gazeAction) down_xrDestroyAction(s.gazeAction);
        if (s.gazeSet) down_xrDestroyActionSet(s.gazeSet);
        if (s.context) s.context->Release();
        CloseControl(s);
        s = SessionState{};
    }
    return down_xrDestroySession(session);
}

// ---------------------------------------------------------------------------
// gipa: hand out our wrappers, defer everything else down-chain
// ---------------------------------------------------------------------------
static XrResult XRAPI_CALL MonoXr_xrGetInstanceProcAddr(XrInstance instance, const char* name,
                                                        PFN_xrVoidFunction* function) {
    auto give = [&](void* f) { *function = reinterpret_cast<PFN_xrVoidFunction>(f); return XR_SUCCESS; };
    if (!name || !function) return XR_ERROR_VALIDATION_FAILURE;

    // Log each distinct function the app resolves, once, so we can see whether
    // the app ever asks for session/frame entry points (i.e. whether it renders
    // through this instance at all).
    {
        static std::mutex seenLock;
        static std::vector<std::string> seen;
        std::lock_guard<std::mutex> g(seenLock);
        bool isNew = true;
        for (auto& n : seen) if (n == name) { isNew = false; break; }
        if (isNew) { seen.emplace_back(name); LogLine("gipa request: %s", name); }
    }

    if (strcmp(name, "xrCreateSession") == 0)  return give((void*)MonoXr_xrCreateSession);
    if (strcmp(name, "xrDestroySession") == 0) return give((void*)MonoXr_xrDestroySession);
    if (strcmp(name, "xrEndFrame") == 0)       return give((void*)MonoXr_xrEndFrame);
    if (strcmp(name, "xrAttachSessionActionSets") == 0)
        return give((void*)MonoXr_xrAttachSessionActionSets);
    if (strcmp(name, "xrSyncActions") == 0)    return give((void*)MonoXr_xrSyncActions);
    if (strcmp(name, "xrSuggestInteractionProfileBindings") == 0)
        return give((void*)MonoXr_xrSuggestInteractionProfileBindings);

    if (g_nextGipa) return g_nextGipa(instance, name, function);
    *function = nullptr;
    return XR_ERROR_FUNCTION_UNSUPPORTED;
}

// ---------------------------------------------------------------------------
// Layer instance creation: descend the chain and resolve our dispatch table
// ---------------------------------------------------------------------------
static bool RuntimeHasExtension(PFN_xrGetInstanceProcAddr gipa, const char* name) {
    PFN_xrEnumerateInstanceExtensionProperties enumExt = nullptr;
    if (XR_FAILED(gipa(XR_NULL_HANDLE, "xrEnumerateInstanceExtensionProperties",
                       reinterpret_cast<PFN_xrVoidFunction*>(&enumExt))) || !enumExt)
        return false;
    uint32_t count = 0;
    if (XR_FAILED(enumExt(nullptr, 0, &count, nullptr)) || count == 0) return false;
    std::vector<XrExtensionProperties> props(count, XrExtensionProperties{ XR_TYPE_EXTENSION_PROPERTIES });
    if (XR_FAILED(enumExt(nullptr, count, &count, props.data()))) return false;
    for (uint32_t i = 0; i < count; ++i)
        if (strcmp(props[i].extensionName, name) == 0) return true;
    return false;
}

static XrResult XRAPI_CALL MonoXr_xrCreateApiLayerInstance(const XrInstanceCreateInfo* info,
                                                           const XrApiLayerCreateInfo* apiLayerInfo,
                                                           XrInstance* instance) {
    LogLine("xrCreateApiLayerInstance called (app='%s')",
            (info && info->applicationInfo.applicationName[0]) ? info->applicationInfo.applicationName : "?");
    if (!apiLayerInfo || !apiLayerInfo->nextInfo) {
        LogLine("  -> FAIL: null apiLayerInfo/nextInfo");
        return XR_ERROR_INITIALIZATION_FAILED;
    }

    PFN_xrGetInstanceProcAddr nextGipa = apiLayerInfo->nextInfo->nextGetInstanceProcAddr;
    PFN_xrCreateApiLayerInstance nextCreate = apiLayerInfo->nextInfo->nextCreateApiLayerInstance;

    XrApiLayerCreateInfo local = *apiLayerInfo;
    local.nextInfo = apiLayerInfo->nextInfo->next; // advance to the next layer

    // Turn on XR_EXT_eye_gaze_interaction and XR_KHR_composition_layer_cylinder
    // for the app if the runtime has them and the app has not asked for them:
    // the app never has to know, the layer reads the gaze and draws curved
    // overlays itself. If the runtime refuses the extra extensions, try them one
    // at a time, and finally create the instance exactly as the app asked.
    bool appHasEye = false, appHasCyl = false;
    for (uint32_t i = 0; info && i < info->enabledExtensionCount; ++i) {
        if (strcmp(info->enabledExtensionNames[i], XR_EXT_EYE_GAZE_INTERACTION_EXTENSION_NAME) == 0) appHasEye = true;
        if (strcmp(info->enabledExtensionNames[i], XR_KHR_COMPOSITION_LAYER_CYLINDER_EXTENSION_NAME) == 0) appHasCyl = true;
    }
    const bool addEye = !appHasEye && info && RuntimeHasExtension(nextGipa, XR_EXT_EYE_GAZE_INTERACTION_EXTENSION_NAME);
    const bool addCyl = !appHasCyl && info && RuntimeHasExtension(nextGipa, XR_KHR_COMPOSITION_LAYER_CYLINDER_EXTENSION_NAME);

    XrResult res = XR_ERROR_INITIALIZATION_FAILED;
    bool eyeEnabled = appHasEye, cylEnabled = appHasCyl;
    bool created = false;
    struct Attempt { bool eye, cyl; };
    const Attempt attempts[] = { { addEye, addCyl }, { addEye, false }, { false, addCyl } };
    for (size_t ai = 0; ai < 3 && !created; ++ai) {
        const Attempt& at = attempts[ai];
        if (!at.eye && !at.cyl) continue;
        if (ai > 0 && at.eye == attempts[ai - 1].eye && at.cyl == attempts[ai - 1].cyl) continue;
        std::vector<const char*> names(info->enabledExtensionNames,
                                       info->enabledExtensionNames + info->enabledExtensionCount);
        if (at.eye) names.push_back(XR_EXT_EYE_GAZE_INTERACTION_EXTENSION_NAME);
        if (at.cyl) names.push_back(XR_KHR_COMPOSITION_LAYER_CYLINDER_EXTENSION_NAME);
        XrInstanceCreateInfo patched = *info;
        patched.enabledExtensionCount = (uint32_t)names.size();
        patched.enabledExtensionNames = names.data();
        local = *apiLayerInfo;
        local.nextInfo = apiLayerInfo->nextInfo->next;
        res = nextCreate(&patched, &local, instance);
        LogLine("  created instance with added extensions (eye gaze %d, cylinder %d): %d", at.eye, at.cyl, res);
        if (XR_SUCCEEDED(res)) {
            created = true;
            eyeEnabled = appHasEye || at.eye;
            cylEnabled = appHasCyl || at.cyl;
        }
    }
    if (!created) {
        local = *apiLayerInfo;
        local.nextInfo = apiLayerInfo->nextInfo->next;
        res = nextCreate(info, &local, instance);
    }
    LogLine("  chained nextCreateApiLayerInstance returned %d (eye gaze ext %s, cylinder ext %s)", res,
            eyeEnabled ? "on" : "off", cylEnabled ? "on" : "off");
    if (XR_FAILED(res)) return res;

    g_nextGipa = nextGipa;
    g_instance = *instance;
    g_eyeExtEnabled = eyeEnabled;
    g_cylinderExtEnabled = cylEnabled;

    #define RESOLVE(fn) nextGipa(*instance, #fn, (PFN_xrVoidFunction*)&down_##fn)
    RESOLVE(xrCreateSession);
    RESOLVE(xrDestroySession);
    RESOLVE(xrEndFrame);
    RESOLVE(xrCreateReferenceSpace);
    RESOLVE(xrDestroySpace);
    RESOLVE(xrLocateSpace);
    RESOLVE(xrEnumerateSwapchainFormats);
    RESOLVE(xrCreateSwapchain);
    RESOLVE(xrDestroySwapchain);
    RESOLVE(xrEnumerateSwapchainImages);
    RESOLVE(xrAcquireSwapchainImage);
    RESOLVE(xrWaitSwapchainImage);
    RESOLVE(xrReleaseSwapchainImage);
    RESOLVE(xrGetSystemProperties);
    RESOLVE(xrStringToPath);
    RESOLVE(xrCreateActionSet);
    RESOLVE(xrDestroyActionSet);
    RESOLVE(xrCreateAction);
    RESOLVE(xrDestroyAction);
    RESOLVE(xrSuggestInteractionProfileBindings);
    RESOLVE(xrAttachSessionActionSets);
    RESOLVE(xrSyncActions);
    RESOLVE(xrCreateActionSpace);
    RESOLVE(xrGetActionStatePose);
    #undef RESOLVE

    LogLine("xrCreateApiLayerInstance ok; endFrame=%p session=%p",
            (void*)down_xrEndFrame, (void*)down_xrCreateSession);
    return XR_SUCCESS;
}

// ---------------------------------------------------------------------------
// Loader negotiation entry point
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport) XrResult XRAPI_CALL
xrNegotiateLoaderApiLayerInterface(const XrNegotiateLoaderInfo* loaderInfo,
                                   const char* layerName,
                                   XrNegotiateApiLayerRequest* apiLayerRequest) {
    LogLine("xrNegotiateLoaderApiLayerInterface called (layerName=%s)",
            layerName ? layerName : "(null)");

    if (!loaderInfo || !apiLayerRequest) {
        LogLine("  -> FAIL: null loaderInfo/apiLayerRequest");
        return XR_ERROR_INITIALIZATION_FAILED;
    }
    LogLine("  loaderInfo: structType=%d version=%u minInterface=%u maxInterface=%u minApi=0x%llx maxApi=0x%llx",
            (int)loaderInfo->structType, loaderInfo->structVersion,
            loaderInfo->minInterfaceVersion, loaderInfo->maxInterfaceVersion,
            (unsigned long long)loaderInfo->minApiVersion,
            (unsigned long long)loaderInfo->maxApiVersion);
    LogLine("  apiLayerRequest: structType=%d version=%u",
            (int)apiLayerRequest->structType, apiLayerRequest->structVersion);
    LogLine("  our XR_CURRENT_LOADER_API_LAYER_VERSION=%u XR_CURRENT_API_VERSION=0x%llx",
            XR_CURRENT_LOADER_API_LAYER_VERSION,
            (unsigned long long)XR_CURRENT_API_VERSION);

    if (loaderInfo->structType != XR_LOADER_INTERFACE_STRUCT_LOADER_INFO ||
        apiLayerRequest->structType != XR_LOADER_INTERFACE_STRUCT_API_LAYER_REQUEST) {
        LogLine("  -> FAIL: struct type mismatch");
        return XR_ERROR_INITIALIZATION_FAILED;
    }

    apiLayerRequest->layerInterfaceVersion = XR_CURRENT_LOADER_API_LAYER_VERSION;
    apiLayerRequest->layerApiVersion = XR_CURRENT_API_VERSION;
    apiLayerRequest->getInstanceProcAddr = MonoXr_xrGetInstanceProcAddr;
    apiLayerRequest->createApiLayerInstance = MonoXr_xrCreateApiLayerInstance;

    LogLine("xrNegotiateLoaderApiLayerInterface ok -> layer accepted");
    return XR_SUCCESS;
}

// ---------------------------------------------------------------------------
// DllMain: earliest possible proof that the loader mapped our DLL into a
// process. If this line never appears for the game process, the loader never
// even found/loaded the layer (registration / arch / manifest problem), as
// opposed to a negotiation or session problem.
// ---------------------------------------------------------------------------
BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH)
        LogLine("DllMain DLL_PROCESS_ATTACH — MonoXR layer DLL mapped into process");
    return TRUE;
}
