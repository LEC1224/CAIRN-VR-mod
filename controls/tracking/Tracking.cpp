#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <openxr/openxr_loader_negotiation.h>
#include <array>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

// This layer is scoped to the CAIRN launcher process and the mod's single OpenXR scene session.
namespace
{
PFN_xrGetInstanceProcAddr nextProc{};
XrInstance instance{};
XrSession session{};
XrActionSet set{};
bool gazeEnabled{}, frameProfile{}, attached{}, wanted{};
enum Action
{
    Gaze,
    GripPose,
    AimPose,
    Select,
    Reach,
    LStick,
    RStick,
    LT,
    RT,
    Up,
    Down,
    Left,
    Right,
    Y,
    B,
    A,
    X,
    L3,
    R3,
    LB,
    RB,
    Menu,
    View,
    Count
};
XrAction actions[Count]{};
XrSpace spaces[3]{};
struct Pose
{
    float x, y, z, qx, qy, qz, qw;
};
struct Sample
{
    unsigned long long sequence;
    unsigned valid, pressed;
    Pose gaze, grip, aim;
    float lx, ly, rx, ry, lt, rt;
    unsigned buttons;
};
static_assert(sizeof(Sample) == 128, "Managed sample ABI mismatch");
Sample latest{};
std::mutex guard;
ULONGLONG sampledAt{};

template <class T> T proc(const char *name)
{
    PFN_xrVoidFunction ptr{};
    return nextProc && XR_SUCCEEDED(nextProc(instance, name, &ptr)) ? reinterpret_cast<T>(ptr) : nullptr;
}
struct Dispatch
{
    PFN_xrStringToPath xrStringToPath{};
    PFN_xrSuggestInteractionProfileBindings xrSuggestInteractionProfileBindings{};
    PFN_xrCreateActionSet xrCreateActionSet{};
    PFN_xrCreateAction xrCreateAction{};
    PFN_xrAttachSessionActionSets xrAttachSessionActionSets{};
    PFN_xrCreateActionSpace xrCreateActionSpace{};
    PFN_xrBeginSession xrBeginSession{};
    PFN_xrLocateViews xrLocateViews{};
    PFN_xrSyncActions xrSyncActions{};
    PFN_xrGetActionStatePose xrGetActionStatePose{};
    PFN_xrLocateSpace xrLocateSpace{};
    PFN_xrGetActionStateVector2f xrGetActionStateVector2f{};
    PFN_xrGetActionStateFloat xrGetActionStateFloat{};
    PFN_xrGetActionStateBoolean xrGetActionStateBoolean{};
    PFN_xrDestroySpace xrDestroySpace{};
    PFN_xrDestroySession xrDestroySession{};
    PFN_xrDestroyInstance xrDestroyInstance{};
} api;
void cacheFunctions()
{
    api.xrStringToPath = proc<PFN_xrStringToPath>("xrStringToPath");
    api.xrSuggestInteractionProfileBindings =
        proc<PFN_xrSuggestInteractionProfileBindings>("xrSuggestInteractionProfileBindings");
    api.xrCreateActionSet = proc<PFN_xrCreateActionSet>("xrCreateActionSet");
    api.xrCreateAction = proc<PFN_xrCreateAction>("xrCreateAction");
    api.xrAttachSessionActionSets = proc<PFN_xrAttachSessionActionSets>("xrAttachSessionActionSets");
    api.xrCreateActionSpace = proc<PFN_xrCreateActionSpace>("xrCreateActionSpace");
    api.xrBeginSession = proc<PFN_xrBeginSession>("xrBeginSession");
    api.xrLocateViews = proc<PFN_xrLocateViews>("xrLocateViews");
    api.xrSyncActions = proc<PFN_xrSyncActions>("xrSyncActions");
    api.xrGetActionStatePose = proc<PFN_xrGetActionStatePose>("xrGetActionStatePose");
    api.xrLocateSpace = proc<PFN_xrLocateSpace>("xrLocateSpace");
    api.xrGetActionStateVector2f = proc<PFN_xrGetActionStateVector2f>("xrGetActionStateVector2f");
    api.xrGetActionStateFloat = proc<PFN_xrGetActionStateFloat>("xrGetActionStateFloat");
    api.xrGetActionStateBoolean = proc<PFN_xrGetActionStateBoolean>("xrGetActionStateBoolean");
    api.xrDestroySpace = proc<PFN_xrDestroySpace>("xrDestroySpace");
    api.xrDestroySession = proc<PFN_xrDestroySession>("xrDestroySession");
    api.xrDestroyInstance = proc<PFN_xrDestroyInstance>("xrDestroyInstance");
}

bool option(const char *name)
{
    char s[8]{};
    return GetEnvironmentVariableA(name, s, sizeof(s)) == 1 && s[0] == '1';
}
void clearSample()
{
    std::lock_guard<std::mutex> lock(guard);
    latest = {};
    sampledAt = 0;
}
XrPath path(const std::string &name)
{
    XrPath p{};
    auto f = api.xrStringToPath;
    if (f)
        f(instance, name.c_str(), &p);
    return p;
}

const char *names[Count] = {"gaze",
                            "right_grip_pose",
                            "right_aim",
                            "select_limb",
                            "reach_or_mark",
                            "left_stick",
                            "right_stick",
                            "left_trigger",
                            "right_trigger",
                            "up",
                            "down",
                            "left",
                            "right",
                            "y",
                            "b",
                            "a",
                            "x",
                            "left_stick_press",
                            "right_stick_press",
                            "left_shoulder",
                            "right_shoulder",
                            "menu",
                            "view"};
void bindProfile(const char *profile, bool frame, bool shoulder)
{
    const char *components[Count] = {nullptr,
                                     "right/input/grip/pose",
                                     "right/input/aim/pose",
                                     "left/input/squeeze/value",
                                     "right/input/squeeze/value",
                                     "left/input/thumbstick",
                                     "right/input/thumbstick",
                                     "left/input/trigger/value",
                                     "right/input/trigger/value",
                                     nullptr,
                                     nullptr,
                                     nullptr,
                                     nullptr,
                                     "left/input/y/click",
                                     "right/input/b/click",
                                     "right/input/a/click",
                                     "left/input/x/click",
                                     "left/input/thumbstick/click",
                                     "right/input/thumbstick/click",
                                     nullptr,
                                     nullptr,
                                     "left/input/menu/click",
                                     nullptr};
    if (frame)
    {
        components[Select] = "left/input/squeeze/click";
        components[Reach] = "right/input/squeeze/click";
        components[Up] = "left/input/dpad_up/click";
        components[Down] = "left/input/dpad_down/click";
        components[Left] = "left/input/dpad_left/click";
        components[Right] = "left/input/dpad_right/click";
        components[Y] = "right/input/y/click";
        components[X] = "right/input/x/click";
        components[LB] = shoulder ? "left/input/shoulder/click" : "left/input/bumper/click";
        components[RB] = shoulder ? "right/input/shoulder/click" : "right/input/bumper/click";
        components[Menu] = "right/input/menu/click";
        components[View] = "left/input/view/click";
    }
    if (option("CAIRNVR_LEFTHANDED"))
    {
        components[GripPose] = "left/input/grip/pose";
        components[AimPose] = "left/input/aim/pose";
        components[Select] = frame ? "right/input/squeeze/click" : "right/input/squeeze/value";
        components[Reach] = frame ? "left/input/squeeze/click" : "left/input/squeeze/value";
    }
    std::vector<XrActionSuggestedBinding> b;
    for (int i = 0; i < Count; i++)
        if (components[i] && actions[i])
            b.push_back({actions[i], path(std::string("/user/hand/") + components[i])});
    XrInteractionProfileSuggestedBinding info{XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING};
    info.interactionProfile = path(profile);
    info.countSuggestedBindings = (uint32_t)b.size();
    info.suggestedBindings = b.data();
    const auto result = api.xrSuggestInteractionProfileBindings(instance, &info);
    if (frame && shoulder && XR_FAILED(result))
        bindProfile(profile, true, false);
}
void createActions()
{
    XrActionSetCreateInfo si{XR_TYPE_ACTION_SET_CREATE_INFO};
    strcpy_s(si.actionSetName, "cairn");
    strcpy_s(si.localizedActionSetName, "Cairn VR");
    if (XR_FAILED(api.xrCreateActionSet(instance, &si, &set)))
        return;
    for (int i = 0; i < Count; i++)
    {
        if (i == Gaze && !gazeEnabled)
            continue;
        XrActionCreateInfo ai{XR_TYPE_ACTION_CREATE_INFO};
        strcpy_s(ai.actionName, names[i]);
        strcpy_s(ai.localizedActionName, names[i]);
        ai.actionType = i <= AimPose                   ? XR_ACTION_TYPE_POSE_INPUT
                        : (i == LStick || i == RStick) ? XR_ACTION_TYPE_VECTOR2F_INPUT
                        : (i == LT || i == RT)         ? XR_ACTION_TYPE_FLOAT_INPUT
                                                       : XR_ACTION_TYPE_BOOLEAN_INPUT;
        if (XR_FAILED(api.xrCreateAction(set, &ai, &actions[i])))
            actions[i] = XR_NULL_HANDLE;
    }
    if (actions[Gaze])
    {
        XrActionSuggestedBinding b{actions[Gaze], path("/user/eyes_ext/input/gaze_ext/pose")};
        XrInteractionProfileSuggestedBinding i{XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING};
        i.interactionProfile = path("/interaction_profiles/ext/eye_gaze_interaction");
        i.countSuggestedBindings = 1;
        i.suggestedBindings = &b;
        api.xrSuggestInteractionProfileBindings(instance, &i);
    }
    if (frameProfile)
        bindProfile("/interaction_profiles/valve/frame_controller_valve", true, true);
    bindProfile("/interaction_profiles/oculus/touch_controller", false, false);
}

XrResult XRAPI_CALL begin(XrSession s, const XrSessionBeginInfo *info)
{
    if (wanted && s != session)
    {
        session = s;
        attached = false;
        if (!set)
            createActions();
        XrSessionActionSetsAttachInfo attach{XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO};
        attach.countActionSets = 1;
        attach.actionSets = &set;
        if (set && XR_SUCCEEDED(api.xrAttachSessionActionSets(s, &attach)))
        {
            attached = true;
            for (int i = 0; i < 3; i++)
                if (actions[i])
                {
                    XrActionSpaceCreateInfo si{XR_TYPE_ACTION_SPACE_CREATE_INFO};
                    si.action = actions[i];
                    si.poseInActionSpace.orientation.w = 1;
                    api.xrCreateActionSpace(s, &si, &spaces[i]);
                }
        }
    }
    return api.xrBeginSession(s, info);
}

XrResult XRAPI_CALL locate(XrSession s, const XrViewLocateInfo *info, XrViewState *state, uint32_t capacity,
                           uint32_t *count, XrView *views)
{
    const XrResult result = api.xrLocateViews(s, info, state, capacity, count, views);
    if (XR_FAILED(result) || s != session || !attached || !capacity)
        return result;
    XrActiveActionSet active{set, XR_NULL_PATH};
    XrActionsSyncInfo sync{XR_TYPE_ACTIONS_SYNC_INFO};
    sync.countActiveActionSets = 1;
    sync.activeActionSets = &active;
    if (api.xrSyncActions(s, &sync) != XR_SUCCESS)
    {
        clearSample();
        return result;
    }
    Sample out{};
    for (int i = 0; i < Count; i++)
    {
        if (!actions[i])
            continue;
        XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO};
        gi.action = actions[i];
        if (i <= AimPose && spaces[i])
        {
            XrActionStatePose ps{XR_TYPE_ACTION_STATE_POSE};
            XrSpaceLocation loc{XR_TYPE_SPACE_LOCATION};
            if (XR_FAILED(api.xrGetActionStatePose(s, &gi, &ps)) || !ps.isActive ||
                XR_FAILED(api.xrLocateSpace(spaces[i], info->space, info->displayTime, &loc)))
                continue;
            constexpr auto required =
                XR_SPACE_LOCATION_ORIENTATION_VALID_BIT | XR_SPACE_LOCATION_POSITION_VALID_BIT;
            if ((loc.locationFlags & required) != required)
                continue;
            auto &p = loc.pose;
            Pose v{p.position.x,     p.position.y,    -p.position.z,  -p.orientation.x,
                   -p.orientation.y, p.orientation.z, p.orientation.w};
            if (i == Gaze)
                out.gaze = v;
            else if (i == GripPose)
                out.grip = v;
            else
                out.aim = v;
            out.valid |= 1u << i;
        }
        else if (i == LStick || i == RStick)
        {
            XrActionStateVector2f vs{XR_TYPE_ACTION_STATE_VECTOR2F};
            if (XR_SUCCEEDED(api.xrGetActionStateVector2f(s, &gi, &vs)) && vs.isActive)
            {
                out.valid |= 8;
                if (i == LStick)
                {
                    out.lx = vs.currentState.x;
                    out.ly = vs.currentState.y;
                }
                else
                {
                    out.rx = vs.currentState.x;
                    out.ry = vs.currentState.y;
                }
            }
        }
        else if (i == LT || i == RT)
        {
            XrActionStateFloat fs{XR_TYPE_ACTION_STATE_FLOAT};
            if (XR_SUCCEEDED(api.xrGetActionStateFloat(s, &gi, &fs)) && fs.isActive)
            {
                if (i == LT)
                    out.lt = fs.currentState;
                else
                    out.rt = fs.currentState;
            }
        }
        else if (i >= Select && i != LStick && i != RStick)
        {
            XrActionStateBoolean bs{XR_TYPE_ACTION_STATE_BOOLEAN};
            if (XR_SUCCEEDED(api.xrGetActionStateBoolean(s, &gi, &bs)) && bs.isActive && bs.currentState)
            {
                if (i == Select || i == Reach)
                    out.pressed |= 1u << (i - Select);
                else if (i >= Up)
                    out.buttons |= 1u << (i - Up); // GamepadButton indices 0..13
            }
        }
    }
    std::lock_guard<std::mutex> lock(guard);
    out.sequence = latest.sequence + 1;
    latest = out;
    sampledAt = GetTickCount64();
    return result;
}
XrResult XRAPI_CALL destroySession(XrSession s)
{
    if (s == session)
    {
        for (auto &space : spaces)
        {
            if (space)
                api.xrDestroySpace(space);
            space = XR_NULL_HANDLE;
        }
        session = XR_NULL_HANDLE;
        attached = false;
        clearSample();
    }
    return api.xrDestroySession(s);
}
XrResult XRAPI_CALL destroyInstance(XrInstance i)
{
    auto f = api.xrDestroyInstance;
    // The runtime destroys all children with their parent instance.
    instance = XR_NULL_HANDLE;
    session = XR_NULL_HANDLE;
    set = XR_NULL_HANDLE;
    attached = false;
    memset(actions, 0, sizeof(actions));
    memset(spaces, 0, sizeof(spaces));
    clearSample();
    return f(i);
}
XrResult XRAPI_CALL getProc(XrInstance i, const char *name, PFN_xrVoidFunction *fn)
{
    auto result = nextProc(i, name, fn);
    if (XR_SUCCEEDED(result) && i && wanted)
    {
        if (!strcmp(name, "xrBeginSession"))
            *fn = (PFN_xrVoidFunction)begin;
        if (!strcmp(name, "xrLocateViews"))
            *fn = (PFN_xrVoidFunction)locate;
        if (!strcmp(name, "xrDestroySession"))
            *fn = (PFN_xrVoidFunction)destroySession;
        if (!strcmp(name, "xrDestroyInstance"))
            *fn = (PFN_xrVoidFunction)destroyInstance;
    }
    return result;
}
XrResult XRAPI_CALL create(const XrInstanceCreateInfo *ci, const XrApiLayerCreateInfo *chain, XrInstance *out)
{
    nextProc = chain->nextInfo->nextGetInstanceProcAddr;
    auto next = chain->nextInfo->nextCreateApiLayerInstance;
    XrApiLayerCreateInfo forward = *chain;
    forward.nextInfo = chain->nextInfo->next;
    wanted = option("CAIRN_VR");
    if (!wanted)
        return next(ci, &forward, out);
    // Some runtimes expose the vendor profile without listing it through a pre-instance layer dispatch.
    // Try optional extensions individually if negotiation rejects their combination.
    const bool requestGaze = option("CAIRNVR_GAZE");
    for (int mask : {3, 2, 1, 0})
    {
        if ((mask & 2) && !requestGaze)
            continue;
        std::vector<const char *> exts;
        for (uint32_t i = 0; i < ci->enabledExtensionCount; i++)
            exts.push_back(ci->enabledExtensionNames[i]);
        auto add = [&](const char *name) {
            for (auto e : exts)
                if (!strcmp(e, name))
                    return;
            exts.push_back(name);
        };
        gazeEnabled = (mask & 2) != 0;
        frameProfile = (mask & 1) != 0;
        if (gazeEnabled)
            add("XR_EXT_eye_gaze_interaction");
        if (frameProfile)
            add("XR_VALVE_frame_controller_interaction");
        XrInstanceCreateInfo request = *ci;
        request.enabledExtensionCount = (uint32_t)exts.size();
        request.enabledExtensionNames = exts.data();
        auto result = next(&request, &forward, out);
        if (XR_SUCCEEDED(result))
        {
            instance = *out;
            cacheFunctions();
            return result;
        }
        if (result != XR_ERROR_EXTENSION_NOT_PRESENT || mask == 0)
            return result;
    }
    return XR_ERROR_INITIALIZATION_FAILED;
}
} // namespace

extern "C" __declspec(dllexport) int CairnReadTracking(Sample *sample, int size)
{
    if (!sample || size != sizeof(Sample))
        return 0;
    std::lock_guard<std::mutex> lock(guard);
    if (!sampledAt || GetTickCount64() - sampledAt > 250)
        return 0;
    *sample = latest;
    return 1;
}
extern "C" __declspec(dllexport) XrResult XRAPI_CALL xrNegotiateLoaderApiLayerInterface(
    const XrNegotiateLoaderInfo *loader, const char *, XrNegotiateApiLayerRequest *layer)
{
    if (!loader || !layer || loader->minInterfaceVersion > 1 || loader->maxInterfaceVersion < 1)
        return XR_ERROR_INITIALIZATION_FAILED;
    layer->layerInterfaceVersion = 1;
    layer->layerApiVersion = XR_MAKE_VERSION(1, 1, 0);
    layer->getInstanceProcAddr = getProc;
    layer->createApiLayerInstance = create;
    return XR_SUCCESS;
}
