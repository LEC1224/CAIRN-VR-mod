#include <windows.h>
#include <openxr/openxr_loader_negotiation.h>
#include <cassert>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include <stdexcept>
#include <algorithm>
#include <map>
struct Pose
{
    float x, y, z, qx, qy, qz, qw;
};
struct Sample
{
    uint64_t seq;
    uint32_t valid, pressed;
    Pose gaze, grip, aim;
    float lx, ly, rx, ry, lt, rt;
    uint32_t buttons;
    Pose otherGrip;
    uint32_t frameHands;
};
int actionId = 0, attempts = 0;
bool focus = true;
bool supportsFrame = false, otherHandTracked = true;
std::vector<std::string> paths;
std::map<XrAction, std::string> actionNames;
std::map<std::string, std::string> frameBindings;
template <class T> T handle(uint64_t id)
{
    return reinterpret_cast<T>(id);
}
XrResult XRAPI_CALL create(const XrInstanceCreateInfo *i, const XrApiLayerCreateInfo *, XrInstance *out)
{
    ++attempts;
    if (!supportsFrame && i->enabledExtensionCount == 2)
        return XR_ERROR_EXTENSION_NOT_PRESENT;
    assert(i->enabledExtensionCount == (supportsFrame ? 2u : 1u) &&
           !strcmp(i->enabledExtensionNames[0], "XR_EXT_eye_gaze_interaction"));
    *out = handle<XrInstance>(1);
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrStringToPath(XrInstance, const char *value, XrPath *p)
{
    auto found = std::find(paths.begin(), paths.end(), value);
    if (found == paths.end()) { paths.emplace_back(value); *p = paths.size(); }
    else *p = 1u + (found - paths.begin());
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrSuggestInteractionProfileBindings(XrInstance,
                                                        const XrInteractionProfileSuggestedBinding *info)
{
    assert(info->countSuggestedBindings);
    if (paths.at(info->interactionProfile-1) == "/interaction_profiles/valve/frame_controller_valve")
        for (uint32_t i=0; i<info->countSuggestedBindings; ++i)
        {
            const auto b=info->suggestedBindings[i];
            frameBindings[actionNames.at(b.action)] = paths.at(b.binding-1);
        }
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrCreateActionSet(XrInstance, const XrActionSetCreateInfo *, XrActionSet *s)
{
    *s = handle<XrActionSet>(1);
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrCreateAction(XrActionSet, const XrActionCreateInfo *info, XrAction *a)
{
    *a = handle<XrAction>(++actionId);
    actionNames[*a] = info->actionName;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrAttachSessionActionSets(XrSession, const XrSessionActionSetsAttachInfo *info)
{
    assert(info->countActionSets == 1);
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrCreateActionSpace(XrSession, const XrActionSpaceCreateInfo *i, XrSpace *s)
{
    *s = (XrSpace)i->action;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrBeginSession(XrSession, const XrSessionBeginInfo *)
{
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrLocateViews(XrSession, const XrViewLocateInfo *, XrViewState *, uint32_t, uint32_t *,
                                  XrView *)
{
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrSyncActions(XrSession, const XrActionsSyncInfo *)
{
    return focus ? XR_SUCCESS : XR_SESSION_NOT_FOCUSED;
}
XrResult XRAPI_CALL xrGetActionStatePose(XrSession, const XrActionStateGetInfo *, XrActionStatePose *p)
{
    p->isActive = 1;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrLocateSpace(XrSpace space, XrSpace base, XrTime time, XrSpaceLocation *p)
{
    assert((uint64_t)base == 42 && time == 128);
    p->locationFlags = (!otherHandTracked && actionNames.at((XrAction)space) == "other_grip_pose") ? 0u : 3u;
    p->pose.orientation = {0, 0, 0, 1};
    p->pose.position = {float(reinterpret_cast<uintptr_t>(space)), 2, 3};
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrGetCurrentInteractionProfile(XrSession, XrPath hand, XrInteractionProfileState *state)
{
    // SteamVR can enable the extension even with a different active controller.
    const auto profile = paths.at(hand-1) == "/user/hand/left" ? "/interaction_profiles/valve/frame_controller_valve" : "/interaction_profiles/oculus/touch_controller";
    return xrStringToPath(XR_NULL_HANDLE, profile, &state->interactionProfile);
}
XrResult XRAPI_CALL xrGetActionStateVector2f(XrSession, const XrActionStateGetInfo *, XrActionStateVector2f *p)
{
    p->isActive = 1;
    p->currentState = {.5f, -.5f};
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrGetActionStateFloat(XrSession, const XrActionStateGetInfo *, XrActionStateFloat *p)
{
    p->isActive = 1;
    p->currentState = .75f;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrGetActionStateBoolean(XrSession, const XrActionStateGetInfo *, XrActionStateBoolean *p)
{
    p->isActive = 1;
    p->currentState = 1;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrDestroySpace(XrSpace)
{
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrDestroySession(XrSession)
{
    return XR_SUCCESS;
}
XrResult XRAPI_CALL xrDestroyInstance(XrInstance)
{
    return XR_SUCCESS;
}
XrResult XRAPI_CALL get(XrInstance, const char *name, PFN_xrVoidFunction *f)
{
#define ENTRY(n)                                                                                               \
    if (!strcmp(#n, name))                                                                                     \
    {                                                                                                          \
        *f = (PFN_xrVoidFunction)n;                                                                            \
        return XR_SUCCESS;                                                                                     \
    }
    ENTRY(xrStringToPath)
    ENTRY(xrGetCurrentInteractionProfile)
    ENTRY(xrSuggestInteractionProfileBindings) ENTRY(xrCreateActionSet) ENTRY(xrCreateAction)
        ENTRY(xrAttachSessionActionSets) ENTRY(xrCreateActionSpace) ENTRY(xrBeginSession) ENTRY(xrLocateViews)
            ENTRY(xrSyncActions) ENTRY(xrGetActionStatePose) ENTRY(xrLocateSpace)
                ENTRY(xrGetActionStateVector2f) ENTRY(xrGetActionStateFloat) ENTRY(xrGetActionStateBoolean)
                    ENTRY(xrDestroySpace) ENTRY(xrDestroySession)
                        ENTRY(xrDestroyInstance) return XR_ERROR_FUNCTION_UNSUPPORTED;
}
int wmain(int argc, wchar_t **argv)
{
    assert(argc == 2);
    auto d = LoadLibraryW(argv[1]);
    assert(d);
    auto rd = (int (*)(Sample *, int))GetProcAddress(d, "CairnReadTracking");
    assert(rd);
    auto negotiate =
        (PFN_xrNegotiateLoaderApiLayerInterface)GetProcAddress(d, "xrNegotiateLoaderApiLayerInterface");
    assert(negotiate);
    XrNegotiateLoaderInfo li{};
    li.minInterfaceVersion = 1;
    li.maxInterfaceVersion = 1;
    XrNegotiateApiLayerRequest layer{};
    assert(negotiate(&li, "XR_APILAYER_CAIRN_controls", &layer) == XR_SUCCESS);
    SetEnvironmentVariableA("CAIRN_VR", "1");
    SetEnvironmentVariableA("CAIRNVR_GAZE", "1");
    SetEnvironmentVariableA("CAIRNVR_REACH", "1");
    XrApiLayerNextInfo ni{};
    ni.nextGetInstanceProcAddr = get;
    ni.nextCreateApiLayerInstance = create;
    XrApiLayerCreateInfo ci{};
    ci.nextInfo = &ni;
    XrInstance instance{};
    XrInstanceCreateInfo ii{XR_TYPE_INSTANCE_CREATE_INFO};
    assert(layer.createApiLayerInstance(&ii, &ci, &instance) == XR_SUCCESS && attempts == 2);
#define BRIDGE(n)                                                                                              \
    PFN_##n n{};                                                                                               \
    assert(layer.getInstanceProcAddr(instance, #n, (PFN_xrVoidFunction *)&n) == XR_SUCCESS)
    BRIDGE(xrBeginSession);
    BRIDGE(xrLocateViews);
    BRIDGE(xrDestroySession);
    BRIDGE(xrDestroyInstance);
    auto s = handle<XrSession>(1);
    assert(xrBeginSession(s, nullptr) == XR_SUCCESS);
    XrViewLocateInfo info{XR_TYPE_VIEW_LOCATE_INFO};
    info.space = handle<XrSpace>(42);
    info.displayTime = 128;
    assert(xrLocateViews(s, &info, nullptr, 2, nullptr, nullptr) == XR_SUCCESS);
    Sample sample{};
    assert(rd(&sample, sizeof(sample)) && sample.seq && sample.pressed == 3 && sample.valid == 31 &&
           sample.grip.x != sample.otherGrip.x && sample.otherGrip.z == -3 && sample.frameHands == 0 &&
           sample.gaze.z == -3 && sample.lx == .5f && sample.lt == .75f && sample.buttons == 0x3fff);
    assert(!rd(&sample, 12));
    focus = false;
    xrLocateViews(s, &info, nullptr, 2, nullptr, nullptr);
    assert(!rd(&sample, sizeof(sample)));
    xrDestroySession(s);
    xrDestroyInstance(instance);
    assert(!rd(&sample, sizeof(sample)));
    supportsFrame = true;
    focus = true;
    otherHandTracked = false;
    SetEnvironmentVariableA("CAIRNVR_LEFTHANDED", "1");
    assert(layer.createApiLayerInstance(&ii, &ci, &instance) == XR_SUCCESS);
    assert(xrBeginSession(s, nullptr) == XR_SUCCESS);
    assert(frameBindings["right_grip_pose"] == "/user/hand/left/input/grip/pose");
    assert(frameBindings["other_grip_pose"] == "/user/hand/right/input/grip/pose");
    assert(frameBindings["select_limb"] == "/user/hand/right/input/squeeze/click");
    assert(frameBindings["x"] == "/user/hand/right/input/x/click");
    xrLocateViews(s, &info, nullptr, 2, nullptr, nullptr);
    assert(rd(&sample, sizeof(sample)) && sample.frameHands == 1 && sample.valid == 15);
    xrDestroySession(s);
    xrDestroyInstance(instance);
    puts("Passed input negotiation, physical hand profiles, handedness, independent pose validity, buttons, focus loss and teardown.");
}
