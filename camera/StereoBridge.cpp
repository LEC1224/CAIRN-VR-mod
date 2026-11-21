// CAIRN VR. The graphics copy and OpenXR image release run on Unity's render thread.
#define NOMINMAX
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <stdexcept>
#include <vector>

using Microsoft::WRL::ComPtr;
#define API extern "C" __declspec(dllexport)
namespace {
struct Eye {
    XrSwapchain chain{};
    std::vector<XrSwapchainImageD3D11KHR> images;
    int width{}, height{};
    ComPtr<ID3D11Texture2D> source, fromUnity, forXR;
    ComPtr<IDXGIKeyedMutex> unityLock, xrLock;
};
struct Display { int width[2], height[2], format; };
struct Tracking { XrPosef pose; XrFovf fov; };
std::mutex gate;
std::string error;
XrInstance instance{};
XrSystemId systemId{};
XrSession session{};
XrSpace space{};
XrFrameState frame{XR_TYPE_FRAME_STATE};
XrEnvironmentBlendMode blendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
std::array<XrView,2> views{};
std::array<Eye,2> eyes{};
ComPtr<ID3D11DeviceContext> context, unityContext;
ComPtr<ID3D11Device> displayDevice;
void d3d(HRESULT hr, const char* action) {
    if(FAILED(hr)) throw std::runtime_error(std::string(action)+" failed: "+std::to_string(hr));
}
void makeBridge(Eye& eye, ID3D11Device* unity, DXGI_FORMAT pixelFormat) {
    D3D11_TEXTURE2D_DESC td{};
    td.Width=eye.width;
    td.Height=eye.height;
    td.MipLevels=1;
    td.ArraySize=1;
    td.SampleDesc.Count=1;
    // The transfer is a byte copy. Share an unorm surface in the same
    // typeless format family as the Unity image and sRGB runtime image.
    td.Format=pixelFormat;
    if(pixelFormat==DXGI_FORMAT_R8G8B8A8_UNORM_SRGB) td.Format=DXGI_FORMAT_R8G8B8A8_UNORM;
    if(pixelFormat==DXGI_FORMAT_B8G8R8A8_UNORM_SRGB) td.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
    td.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
    td.MiscFlags=D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    d3d(unity->CreateTexture2D(&td,nullptr,&eye.fromUnity),"create transfer surface");
    ComPtr<IDXGIResource> resource;
    d3d(eye.fromUnity.As(&resource),"query transfer surface");
    HANDLE shared{};
    d3d(resource->GetSharedHandle(&shared),"export transfer surface");
    d3d(displayDevice->OpenSharedResource(shared,IID_PPV_ARGS(&eye.forXR)),"import transfer surface");
    d3d(eye.fromUnity.As(&eye.unityLock),"query Unity synchronization");
    d3d(eye.forXR.As(&eye.xrLock),"query OpenXR synchronization");
}
void lockSurface(IDXGIKeyedMutex* sync,UINT64 key) {
    auto status=sync->AcquireSync(key,150);
    if(status!=S_OK)throw std::runtime_error("Eye transfer timed out: "+std::to_string(status));
}
bool running{}, frameOpen{}, queued{}, queuedDraw{};
std::atomic<uint32_t> submitted{}, skipped{};
std::atomic<uint32_t> renderingNotRequested{}, trackingUnavailable{}, lastViewFlags{};
std::atomic<int32_t> sessionState{XR_SESSION_STATE_UNKNOWN};
uint32_t eventId{};

void check(XrResult result,const char* call) {
    if (XR_FAILED(result)) throw std::runtime_error(std::string(call)+" returned OpenXR "+std::to_string(result));
}
void endFrame(bool draw) {
    if (!frameOpen) return;
    XrCompositionLayerProjectionView projectionViews[2]{};
    for (int i=0;i<2;++i) {
        auto& out=projectionViews[i];
        out.type=XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW;
        out.pose=views[i].pose;
        out.fov=views[i].fov;
        out.subImage.swapchain=eyes[i].chain;
        out.subImage.imageRect.extent={eyes[i].width,eyes[i].height};
    }
    XrCompositionLayerProjection projection{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
    projection.space=space;
    projection.viewCount=2;
    projection.views=projectionViews;
    auto layer=reinterpret_cast<const XrCompositionLayerBaseHeader*>(&projection);
    XrFrameEndInfo info{XR_TYPE_FRAME_END_INFO};
    info.displayTime=frame.predictedDisplayTime;
    info.environmentBlendMode=blendMode;
    info.layerCount=draw?1:0;
    info.layers=draw?&layer:nullptr;
    frameOpen=false;
    check(xrEndFrame(session,&info),"xrEndFrame");
    if(draw) ++submitted; else ++skipped;
}
void closeSession() {
    // Destruction cancels any open frame. Calling xrEndFrame here would run
    // SteamVR's D3D work on the main thread during error recovery or quit.
    frameOpen=false;
    for(auto& eye:eyes) {
        if(eye.chain) xrDestroySwapchain(eye.chain);
        eye=Eye{};
    }
    if(space) xrDestroySpace(space);
    if(session) xrDestroySession(session);
    if(instance) xrDestroyInstance(instance);
    space=XR_NULL_HANDLE; session=XR_NULL_HANDLE; instance=XR_NULL_HANDLE;
    running=false; queued=false; queuedDraw=false;
    context.Reset();
    unityContext.Reset();
    displayDevice.Reset();
    sessionState=XR_SESSION_STATE_UNKNOWN;
    ++eventId;
}
void copyEye(Eye& eye) {
    uint32_t index=0;
    XrSwapchainImageAcquireInfo acquire{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
    check(xrAcquireSwapchainImage(eye.chain,&acquire,&index),"xrAcquireSwapchainImage");
    XrSwapchainImageWaitInfo wait{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
    wait.timeout=XR_INFINITE_DURATION;
    check(xrWaitSwapchainImage(eye.chain,&wait),"xrWaitSwapchainImage");
    lockSurface(eye.unityLock.Get(),0);
    unityContext->CopyResource(eye.fromUnity.Get(),eye.source.Get());
    unityContext->Flush();
    d3d(eye.unityLock->ReleaseSync(1),"offer rendered Unity image");
    lockSurface(eye.xrLock.Get(),1);
    context->CopyResource(eye.images.at(index).texture,eye.forXR.Get());
    context->Flush();
    d3d(eye.xrLock->ReleaseSync(0),"return rendered Unity image");
    XrSwapchainImageReleaseInfo release{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
    check(xrReleaseSwapchainImage(eye.chain,&release),"xrReleaseSwapchainImage");
}
void __stdcall submit(int token) {
    std::lock_guard guard(gate);
    if(!queued || static_cast<uint32_t>(token)!=eventId) return;
    queued=false;
    try {
        if(queuedDraw) for(auto& eye:eyes) copyEye(eye);
        endFrame(queuedDraw);
    } catch(const std::exception& ex) {
        error=ex.what();
        closeSession();
    }
}
void poll() {
    XrEventDataBuffer event{XR_TYPE_EVENT_DATA_BUFFER};
    XrResult result;
    while((result=xrPollEvent(instance,&event))==XR_SUCCESS) {
        if(event.type==XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) throw std::runtime_error("OpenXR runtime is restarting");
        if(event.type==XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
            const auto& state=reinterpret_cast<XrEventDataSessionStateChanged&>(event);
            sessionState=state.state;
            if(state.state==XR_SESSION_STATE_READY && !running) {
                XrSessionBeginInfo info{XR_TYPE_SESSION_BEGIN_INFO};
                info.primaryViewConfigurationType=XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                check(xrBeginSession(session,&info),"xrBeginSession");
                running=true;
            } else if(state.state==XR_SESSION_STATE_STOPPING && running) {
                check(xrEndSession(session),"xrEndSession"); running=false;
            } else if(state.state==XR_SESSION_STATE_EXITING || state.state==XR_SESSION_STATE_LOSS_PENDING) {
                throw std::runtime_error("OpenXR session ended");
            }
        }
        event={XR_TYPE_EVENT_DATA_BUFFER};
    }
    check(result,"xrPollEvent");
}
}

API int vr_open(ID3D11Texture2D* texture,float scale,Display* display) {
    std::lock_guard guard(gate);
    closeSession();
    try {
        if(!texture || !display) throw std::runtime_error("Unity has no D3D11 texture");
        ComPtr<ID3D11Device> device;
        texture->GetDevice(&device);
        device->GetImmediateContext(&unityContext);
        ComPtr<IDXGIDevice> dxgi;
        d3d(device.As(&dxgi),"identify Unity GPU");
        ComPtr<IDXGIAdapter> adapter;
        d3d(dxgi->GetAdapter(&adapter),"get Unity adapter");
        d3d(D3D11CreateDevice(adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                nullptr,0,D3D11_SDK_VERSION,&displayDevice,nullptr,&context),"create OpenXR device");
        const char* extensions[]={XR_KHR_D3D11_ENABLE_EXTENSION_NAME};
        XrInstanceCreateInfo info{XR_TYPE_INSTANCE_CREATE_INFO};
        strcpy_s(info.applicationInfo.applicationName,"CAIRN VR");
        strcpy_s(info.applicationInfo.engineName,"Unity");
        info.applicationInfo.apiVersion=XR_MAKE_VERSION(1,0,0);
        info.enabledExtensionCount=1; info.enabledExtensionNames=extensions;
        check(xrCreateInstance(&info,&instance),"xrCreateInstance");
        XrSystemGetInfo sys{XR_TYPE_SYSTEM_GET_INFO};
        sys.formFactor=XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
        check(xrGetSystem(instance,&sys,&systemId),"xrGetSystem");
        PFN_xrGetD3D11GraphicsRequirementsKHR requirements=nullptr;
        check(xrGetInstanceProcAddr(instance,"xrGetD3D11GraphicsRequirementsKHR",reinterpret_cast<PFN_xrVoidFunction*>(&requirements)),"D3D11 extension");
        XrGraphicsRequirementsD3D11KHR graphics{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
        check(requirements(instance,systemId,&graphics),"xrGetD3D11GraphicsRequirementsKHR");
        XrGraphicsBindingD3D11KHR binding{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR};
        binding.device=displayDevice.Get();
        XrSessionCreateInfo create{XR_TYPE_SESSION_CREATE_INFO};
        create.systemId=systemId; create.next=&binding;
        check(xrCreateSession(instance,&create,&session),"xrCreateSession");
        XrReferenceSpaceCreateInfo ref{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
        ref.referenceSpaceType=XR_REFERENCE_SPACE_TYPE_LOCAL;
        ref.poseInReferenceSpace.orientation.w=1;
        check(xrCreateReferenceSpace(session,&ref,&space),"xrCreateReferenceSpace");
        uint32_t n=0;
        check(xrEnumerateEnvironmentBlendModes(instance,systemId,XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,0,&n,nullptr),"count blend modes");
        std::vector<XrEnvironmentBlendMode> modes(n);
        check(xrEnumerateEnvironmentBlendModes(instance,systemId,XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,n,&n,modes.data()),"enumerate blend modes");
        blendMode=std::find(modes.begin(),modes.end(),XR_ENVIRONMENT_BLEND_MODE_OPAQUE)==modes.end()?modes.at(0):XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
        check(xrEnumerateSwapchainFormats(session,0,&n,nullptr),"count formats");
        std::vector<int64_t> formats(n); check(xrEnumerateSwapchainFormats(session,n,&n,formats.data()),"enumerate formats");
        display->format=0;
        for(auto candidate:{DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,DXGI_FORMAT_R8G8B8A8_UNORM,DXGI_FORMAT_B8G8R8A8_UNORM}) {
            if(std::find(formats.begin(),formats.end(),candidate)!=formats.end()) { display->format=candidate; break; }
        }
        if(!display->format) throw std::runtime_error("OpenXR has no supported 8-bit eye texture format");
        std::array<XrViewConfigurationView,2> configs{};
        for(auto& c:configs) c.type=XR_TYPE_VIEW_CONFIGURATION_VIEW;
        check(xrEnumerateViewConfigurationViews(instance,systemId,XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,2,&n,configs.data()),"enumerate stereo views");
        if(n!=2) throw std::runtime_error("OpenXR did not supply two stereo views");
        for(int i=0;i<2;++i) {
            auto& e=eyes[i]; auto& c=configs[i];
            display->width[i]=e.width=std::clamp<int>(static_cast<int>(c.recommendedImageRectWidth*scale),1,c.maxImageRectWidth);
            display->height[i]=e.height=std::clamp<int>(static_cast<int>(c.recommendedImageRectHeight*scale),1,c.maxImageRectHeight);
            makeBridge(e,device.Get(),static_cast<DXGI_FORMAT>(display->format));
            XrSwapchainCreateInfo sc{XR_TYPE_SWAPCHAIN_CREATE_INFO};
            sc.usageFlags=XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT|XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT;
            sc.format=display->format; sc.sampleCount=1; sc.width=e.width; sc.height=e.height; sc.faceCount=1; sc.arraySize=1; sc.mipCount=1;
            check(xrCreateSwapchain(session,&sc,&e.chain),"xrCreateSwapchain");
            check(xrEnumerateSwapchainImages(e.chain,0,&n,nullptr),"count eye textures");
            e.images.resize(n,{XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
            check(xrEnumerateSwapchainImages(e.chain,n,&n,reinterpret_cast<XrSwapchainImageBaseHeader*>(e.images.data())),"enumerate eye textures");
        }
        submitted=0; skipped=0;
        renderingNotRequested=0; trackingUnavailable=0; lastViewFlags=0;
        error.clear(); return 1;
    } catch(const std::exception& ex) {error=ex.what();closeSession();return 0;}
}
API int vr_frame(Tracking* out) {
    // SteamVR may take time to finish GPU work in the render callback.
    // Unity's main thread must stay available to pump messages and graphics jobs.
    std::unique_lock guard(gate,std::try_to_lock);
    if(!guard.owns_lock()) return 0;
    if(!session) return -1;
    if(queued || frameOpen) return 0;
    try {
        poll(); if(!running) return 0;
        XrFrameWaitInfo wi{XR_TYPE_FRAME_WAIT_INFO}; check(xrWaitFrame(session,&wi,&frame),"xrWaitFrame");
        XrFrameBeginInfo bi{XR_TYPE_FRAME_BEGIN_INFO}; check(xrBeginFrame(session,&bi),"xrBeginFrame"); frameOpen=true;
        if(!frame.shouldRender) { ++renderingNotRequested; return 2; }
        XrViewLocateInfo locate{XR_TYPE_VIEW_LOCATE_INFO};
        locate.space=space; locate.displayTime=frame.predictedDisplayTime; locate.viewConfigurationType=XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
        XrViewState state{XR_TYPE_VIEW_STATE}; uint32_t count=0;
        for(auto& v:views) v.type=XR_TYPE_VIEW;
        check(xrLocateViews(session,&locate,&state,2,&count,views.data()),"xrLocateViews");
        lastViewFlags=static_cast<uint32_t>(state.viewStateFlags);
        const XrViewStateFlags valid = XR_VIEW_STATE_ORIENTATION_VALID_BIT | XR_VIEW_STATE_POSITION_VALID_BIT;
        if(count!=2 || (state.viewStateFlags & valid) != valid) { ++trackingUnavailable; return 2; }
        for(int i=0;i<2;++i) out[i]={views[i].pose,views[i].fov};
        return 1;
    } catch(const std::exception& ex) {error=ex.what();closeSession();return -1;}
}
API int vr_queue(ID3D11Texture2D* left,ID3D11Texture2D* right) {
    std::lock_guard guard(gate);
    if(!frameOpen || queued || ((left==nullptr)!=(right==nullptr))) return -1;
    queuedDraw=left!=nullptr;
    if(queuedDraw) {eyes[0].source=left; eyes[1].source=right;}
    queued=true;
    return static_cast<int>(++eventId);
}
API void* vr_submit_event() {return reinterpret_cast<void*>(&submit);}
API void vr_counts(uint32_t* drawn,uint32_t* empty) { *drawn=submitted.load(); *empty=skipped.load(); }
API void vr_tracking_status(int32_t* state,uint32_t* notRequested,uint32_t* tracking,uint32_t* flags) {
    *state=sessionState.load(); *notRequested=renderingNotRequested.load();
    *tracking=trackingUnavailable.load(); *flags=lastViewFlags.load();
}
API void vr_close() { std::lock_guard guard(gate); closeSession(); }
API void vr_error(char* dest,int size) { std::lock_guard guard(gate); if(size>0) strncpy_s(dest,size,error.c_str(),_TRUNCATE); }
