#include <windows.h>
#include <cmath>
#include <string>
#include "IPlatform.h"
#include "IDeviceManager.h"
#include "ICPUEx.h"

namespace {
HMODULE modulo = nullptr;
IPlatform* plataforma = nullptr;
ICPUEx* cpu = nullptr;
}

extern "C" __declspec(dllexport) void __cdecl CerrarCpu()
{
    cpu = nullptr;
    if (plataforma != nullptr) {
        plataforma->UnInit();
        plataforma = nullptr;
    }
    if (modulo != nullptr) {
        FreeLibrary(modulo);
        modulo = nullptr;
    }
}

extern "C" __declspec(dllexport) int __cdecl AbrirCpu()
{
    if (cpu != nullptr) return 0;
    wchar_t ruta[1024] = {};
    DWORD longitud = sizeof(ruta);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\AMD\\RyzenMasterMonitoringSDK",
        L"InstallationPath", RRF_RT_REG_SZ, nullptr, ruta, &longitud) != ERROR_SUCCESS)
        return -1;
    std::wstring biblioteca(ruta);
    if (!biblioteca.empty() && biblioteca.back() != L'\\') biblioteca += L'\\';
    biblioteca += L"bin\\Platform.dll";
    modulo = LoadLibraryExW(biblioteca.c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (modulo == nullptr) return -2;
    using ObtenerPlataforma = IPlatform& (__stdcall*)();
    auto obtener = reinterpret_cast<ObtenerPlataforma>(GetProcAddress(modulo, "GetPlatform"));
    if (obtener == nullptr) { CerrarCpu(); return -3; }
    plataforma = &obtener();
    if (!plataforma->Init()) { CerrarCpu(); return -4; }
    cpu = static_cast<ICPUEx*>(plataforma->GetIDeviceManager().GetDevice(dtCPU, 0));
    if (cpu == nullptr) { CerrarCpu(); return -5; }
    return 0;
}

extern "C" __declspec(dllexport) int __cdecl LeerTemperaturaCpu(double* temperatura)
{
    if (temperatura == nullptr || cpu == nullptr) return -1;
    CPUParameters parametros{};
    const int resultado = cpu->GetCPUParameters(parametros);
    if (resultado != 0) return resultado;
    if (!std::isfinite(parametros.dTemperature) || parametros.dTemperature <= 0.0) return -2;
    *temperatura = parametros.dTemperature;
    return 0;
}
