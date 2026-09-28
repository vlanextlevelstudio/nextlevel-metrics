#include "SDK/ADLXHelper/Windows/Cpp/ADLXHelper.h"
#include "SDK/Include/IPerformanceMonitoring.h"

using namespace adlx;

namespace {
ADLXHelper helper;
IADLXPerformanceMonitoringServicesPtr services;
bool initialized = false;
}

extern "C" __declspec(dllexport) void __cdecl CerrarFps()
{
    services.Release();
    if (initialized) {
        helper.Terminate();
        initialized = false;
    }
}

extern "C" __declspec(dllexport) int __cdecl AbrirFps()
{
    if (initialized) return 0;
    const ADLX_RESULT result = helper.Initialize();
    if (ADLX_FAILED(result)) return result;
    initialized = true;
    const ADLX_RESULT serviceResult = helper.GetSystemServices()->GetPerformanceMonitoringServices(&services);
    if (ADLX_FAILED(serviceResult)) {
        CerrarFps();
        return serviceResult;
    }
    return 0;
}

extern "C" __declspec(dllexport) int __cdecl LeerFps(int* fps)
{
    if (!initialized || fps == nullptr) return -1;
    IADLXFPSPtr current;
    const ADLX_RESULT currentResult = services->GetCurrentFPS(&current);
    if (ADLX_FAILED(currentResult)) return currentResult;
    adlx_int value = 0;
    const ADLX_RESULT fpsResult = current->FPS(&value);
    if (ADLX_FAILED(fpsResult)) return fpsResult;
    if (value <= 0) return -2;
    *fps = value;
    return 0;
}
