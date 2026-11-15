// TaskbarTempCpp — 原生 Win32 托盘温度监控（C 版）
// 数据源：
//   CPU 温度/频率 : PawnIO 内核驱动 + IntelMSR 模块（MSR 0x19C / 0x1A2 / 0xCE / 0xE7 / 0xE8）
//   CPU 负载      : GetSystemTimes
//   内存          : GlobalMemoryStatusEx
//   网速          : GetIfTable2（物理适配器差值）
// 图标渲染：GDI+ 平面 API（正确处理 32bpp Alpha）
// 目标：常驻内存个位数 MB。

#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define _WIN32_WINNT 0x0A00
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <winternl.h>
#include <objidl.h>
#include <shellapi.h>
#include <iphlpapi.h>
#include <netioapi.h>
#include <ipifcons.h>
#include <gdiplus.h>
#include <dxgi1_2.h>
#include <d3dkmthk.h>
#include <dwmapi.h>
#include <string.h>
#include <ctype.h>
#include <stdio.h>
#include <stdarg.h>
#include <intrin.h>
#include <stdlib.h>
#include <wchar.h>
#include <math.h>

using namespace Gdiplus;
using namespace Gdiplus::DllExports;

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "gdiplus.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "cfgmgr32.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dwmapi.lib")

// ---------- 常量 ----------
#define WM_TRAYICON     (WM_APP + 1)
#define TIMER_ID_SAMPLE 1
#define TIMER_ID_HEARTBEAT 2
#define SAMPLE_MS       2000

// MSR
#define MSR_THERM_STATUS          0x019C // 每核热状态：bit31 有效，bits 22:16 距 TjMax 差值
#define MSR_PACKAGE_THERM_STATUS  0x01B1 // CPU 封装热状态
#define MSR_TEMPERATURE_TARGET    0x01A2 // TjMax = bits 23:16
#define MSR_PLATFORM_INFO         0x00CE // 非睿频倍频 = bits 15:8
#define MSR_APERF                 0x00E8
#define MSR_MPERF                 0x00E7

// PawnIO IOCTL（与 LHM/lib 的实现一致）
#define PAWNIO_DEVICE_TYPE  ((41394u) << 16)
#define PAWNIO_CTL_LOAD     (PAWNIO_DEVICE_TYPE | (0x821 << 2))
#define PAWNIO_CTL_EXEC     (PAWNIO_DEVICE_TYPE | (0x841 << 2))
#define PAWNIO_FN_NAME_LEN  32

// ---------- 全局状态 ----------
static HWND   g_hWnd;
static HWND   g_hDetails;
static BOOL   g_preview;
static BOOL   g_previewDark;
static HICON  g_hIcon;
static HMENU  g_hMenu;
static NOTIFYICONDATAW g_nid;
static UINT g_taskbarCreated;
static wchar_t g_lastIconText[8] = L"";
static UINT64 g_lastIconColor = 0;
enum IconStyle { ICON_MONO, ICON_NUMBER, ICON_PILL, ICON_RING };
static IconStyle g_iconStyle = ICON_MONO;
static wchar_t g_configPath[MAX_PATH];

static COLORREF TempColor(double t)
{
    if (t < 55) return RGB(52, 199, 123);
    if (t < 70) return RGB(58, 160, 240);
    if (t < 82) return RGB(233, 168, 51);
    return RGB(226, 74, 74);
}

static void LoadIconStyle(void)
{
    DWORD len = GetModuleFileNameW(NULL, g_configPath, MAX_PATH);
    if (!len || len >= MAX_PATH) { g_configPath[0] = 0; return; }
    wchar_t* slash = wcsrchr(g_configPath, L'\\');
    if (!slash) { g_configPath[0] = 0; return; }
    slash[1] = 0;
    if (wcscat_s(g_configPath, MAX_PATH, L"config.ini") != 0) { g_configPath[0] = 0; return; }
    FILE* file = NULL;
    if (_wfopen_s(&file, g_configPath, L"rt, ccs=UTF-8") != 0 || !file) return;
    wchar_t line[256];
    while (fgetws(line, 256, file))
    {
        wchar_t* key = line;
        while (*key == L' ' || *key == L'\t') key++;
        if (_wcsnicmp(key, L"style", 5) != 0) continue;
        wchar_t* value = wcschr(key, L'=');
        if (!value) continue;
        value++;
        while (*value == L' ' || *value == L'\t') value++;
        wchar_t* end = value;
        while (*end && *end != L'\r' && *end != L'\n' && *end != L' ' && *end != L'\t') end++;
        *end = 0;
        if (_wcsicmp(value, L"number") == 0) g_iconStyle = ICON_NUMBER;
        else if (_wcsicmp(value, L"pill") == 0) g_iconStyle = ICON_PILL;
        else if (_wcsicmp(value, L"ring") == 0) g_iconStyle = ICON_RING;
        break;
    }
    fclose(file);
}

// PawnIO
static HANDLE g_hPawn = INVALID_HANDLE_VALUE;
static BOOL   g_moduleLoaded = FALSE;
static BOOL   g_temperatureTargetsReady = FALSE;
static int    g_openFailStreak = 0;

// CPU 拓扑
typedef struct { WORD group; KAFFINITY mask; BYTE effClass; float tjMax; } CORE_ENTRY;
static CORE_ENTRY g_cores[128];
static DWORD g_coreCount = 0;
static int   g_perfCoreIdx = -1;     // APERF/MPERF 采样核（首个 P 核）
static float g_baseMHz = 0;

// 上一轮计数器
static UINT64 g_prevIdle, g_prevKernel, g_prevUser;
static UINT64 g_prevAperf, g_prevMperf;
static BOOL   g_hasPerfBaseline = FALSE;
static UINT64 g_prevInOct, g_prevOutOct;
static ULONGLONG g_prevNetTime;
static BOOL   g_hasNetBaseline = FALSE;

static int s_readFailStreak = 0;
static BOOL s_loggedFirstTemp = FALSE;
static float g_temperatureSamples[3];
static unsigned g_temperatureCount;
static unsigned g_temperatureIndex;

// 当前样本
typedef struct {
    BOOL  tempValid;
    float tempC;
    float loadPct;
    float freqMHz;
    double memUsedGB, memTotalGB;
    UINT64 netDownBps, netUpBps;
} SAMPLE;
static SAMPLE g_sample;
static wchar_t g_gpuName[128] = L"GPU";
static UINT64 g_gpuSharedTotal;
static UINT64 g_gpuSharedUsed;
static LUID g_gpuLuid;
static D3DKMT_HANDLE g_gpuHandle;
static BOOL g_gpuMemoryValid;
static int g_gpu3dNode = -1;
static UINT64 g_gpuPreviousTicks;
static ULONGLONG g_gpuPreviousTime;
static float g_gpuLoad;
static BOOL g_gpuLoadValid;
static BOOL g_loggedGpuLoad;

static HANDLE g_hSingleInstance;

static void InitGpu(void)
{
    IDXGIFactory1* factory = NULL;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&factory))) return;
    for (UINT i = 0; ; i++)
    {
        IDXGIAdapter1* adapter = NULL;
        if (factory->EnumAdapters1(i, &adapter) == DXGI_ERROR_NOT_FOUND) break;
        if (!adapter) break;
        DXGI_ADAPTER_DESC1 desc = {};
        if (SUCCEEDED(adapter->GetDesc1(&desc)) && !(desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE))
        {
            wcsncpy_s(g_gpuName, desc.Description, _TRUNCATE);
            g_gpuLuid = desc.AdapterLuid;
            D3DKMT_OPENADAPTERFROMLUID open = {};
            open.AdapterLuid = g_gpuLuid;
            if (D3DKMTOpenAdapterFromLuid(&open) == 0)
            {
                g_gpuHandle = open.hAdapter;
                D3DKMT_SEGMENTSIZEINFO sizes = {};
                D3DKMT_QUERYADAPTERINFO query = {};
                query.hAdapter = g_gpuHandle;
                query.Type = KMTQAITYPE_GETSEGMENTSIZE;
                query.pPrivateDriverData = &sizes;
                query.PrivateDriverDataSize = sizeof(sizes);
                if (D3DKMTQueryAdapterInfo(&query) == 0)
                    g_gpuSharedTotal = sizes.SharedSystemMemorySize;
                D3DKMT_QUERYSTATISTICS stats = {};
                stats.Type = D3DKMT_QUERYSTATISTICS_ADAPTER;
                stats.AdapterLuid = g_gpuLuid;
                if (D3DKMTQueryStatistics(&stats) == 0 && stats.QueryResult.AdapterInformation.NodeCount <= 256)
                {
                    for (ULONG node = 0; node < stats.QueryResult.AdapterInformation.NodeCount; node++)
                    {
                        D3DKMT_NODEMETADATA metadata = {};
                        metadata.NodeOrdinalAndAdapterIndex = node;
                        D3DKMT_QUERYADAPTERINFO info = {};
                        info.hAdapter = g_gpuHandle;
                        info.Type = KMTQAITYPE_NODEMETADATA;
                        info.pPrivateDriverData = &metadata;
                        info.PrivateDriverDataSize = sizeof(metadata);
                        if (D3DKMTQueryAdapterInfo(&info) == 0 && metadata.NodeData.EngineType == DXGK_ENGINE_TYPE_3D)
                        {
                            g_gpu3dNode = (int)node;
                            break;
                        }
                    }
                }
            }
            adapter->Release();
            break;
        }
        adapter->Release();
    }
    factory->Release();
}

static void SampleGpuMemory(void)
{
    g_gpuMemoryValid = FALSE;
    g_gpuLoadValid = FALSE;
    if (!g_gpuHandle || !g_gpuSharedTotal) return;
    if (g_gpu3dNode >= 0)
    {
        D3DKMT_QUERYSTATISTICS node = {};
        node.Type = D3DKMT_QUERYSTATISTICS_NODE;
        node.AdapterLuid = g_gpuLuid;
        node.QueryNode.NodeId = (ULONG)g_gpu3dNode;
        if (D3DKMTQueryStatistics(&node) == 0)
        {
            UINT64 ticks = node.QueryResult.NodeInformation.GlobalInformation.RunningTime.QuadPart;
            ULONGLONG now = GetTickCount64();
            if (g_gpuPreviousTime && now > g_gpuPreviousTime && ticks >= g_gpuPreviousTicks)
            {
                double load = 100.0 * (ticks - g_gpuPreviousTicks) / ((now - g_gpuPreviousTime) * 10000.0);
                if (load >= 0.0 && load <= 100.0) { g_gpuLoad = (float)load; g_gpuLoadValid = TRUE; }
            }
            g_gpuPreviousTicks = ticks;
            g_gpuPreviousTime = now;
        }
        else g_gpuPreviousTime = 0;
    }
    D3DKMT_QUERYSTATISTICS query = {};
    query.Type = D3DKMT_QUERYSTATISTICS_ADAPTER;
    query.AdapterLuid = g_gpuLuid;
    if (D3DKMTQueryStatistics(&query) != 0) return;
    ULONG count = query.QueryResult.AdapterInformation.NbSegments;
    if (count > 256) return;
    UINT64 used = 0;
    for (ULONG i = 0; i < count; i++)
    {
        D3DKMT_QUERYSTATISTICS segment = {};
        segment.Type = D3DKMT_QUERYSTATISTICS_SEGMENT;
        segment.AdapterLuid = g_gpuLuid;
        segment.QuerySegment.SegmentId = i;
        if (D3DKMTQueryStatistics(&segment) != 0) return;
        if (segment.QueryResult.SegmentInformation.Aperture == 1)
            used += segment.QueryResult.SegmentInformation.BytesResident;
    }
    g_gpuSharedUsed = used;
    g_gpuMemoryValid = TRUE;
}

// 日志
static void Log(const wchar_t* fmt, ...)
{
    wchar_t path[MAX_PATH];
    wchar_t dir[MAX_PATH];
    GetEnvironmentVariableW(L"LOCALAPPDATA", dir, MAX_PATH);
    swprintf(path, MAX_PATH, L"%s\\TaskbarTempCpp", dir);
    CreateDirectoryW(path, NULL);
    wcscat_s(path, MAX_PATH, L"\\run.log");

    FILE* f = NULL;
    if (_wfopen_s(&f, path, L"a, ccs=UTF-8") == 0 && f)
    {
        SYSTEMTIME st;
        GetLocalTime(&st);
        fwprintf(f, L"%04u-%02u-%02u %02u:%02u:%02u  ", st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
        va_list ap;
        va_start(ap, fmt);
        vfwprintf(f, fmt, ap);
        va_end(ap);
        fwprintf(f, L"\n");
        fclose(f);
    }
}

static void WriteHeartbeat(void)
{
    wchar_t dir[MAX_PATH], path[MAX_PATH];
    if (!GetEnvironmentVariableW(L"LOCALAPPDATA", dir, MAX_PATH)) return;
    swprintf(path, MAX_PATH, L"%s\\TaskbarTempCpp", dir);
    CreateDirectoryW(path, NULL);
    wcscat_s(path, MAX_PATH, L"\\heartbeat.txt");
    FILE* file = NULL;
    if (_wfopen_s(&file, path, L"w, ccs=UTF-8") != 0 || !file) return;
    SYSTEMTIME now; GetLocalTime(&now);
    fwprintf(file, L"%04u-%02u-%02u %02u:%02u:%02u pid=%lu temp=", now.wYear, now.wMonth, now.wDay,
        now.wHour, now.wMinute, now.wSecond, GetCurrentProcessId());
    if (g_sample.tempValid) fwprintf(file, L"%.1f", g_sample.tempC); else fwprintf(file, L"--");
    fwprintf(file, L"\n");
    fclose(file);
}

// ---------- PawnIO ----------
static BOOL PawnOpen(void)
{
    // 两条路径都试：新实例只有 \??\PawnIO 符号链接，旧实例可用 GLOBALROOT 路径
    static const wchar_t* paths[] = { L"\\\\.\\PawnIO", L"\\\\?\\GLOBALROOT\\Device\\PawnIO" };
    for (int i = 0; i < 2; i++)
    {
        g_hPawn = CreateFileW(paths[i], GENERIC_READ | GENERIC_WRITE,
                              FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
        if (g_hPawn != INVALID_HANDLE_VALUE)
            return TRUE;
    }
    g_hPawn = INVALID_HANDLE_VALUE;
    return FALSE;
}

static BOOL PawnLoadModule(void)
{
    HRSRC hr = FindResourceW(NULL, MAKEINTRESOURCEW(101), RT_RCDATA); // IDR_MSR_MODULE
    if (!hr) { Log(L"resource not found"); return FALSE; }
    HGLOBAL hg = LoadResource(NULL, hr);
    DWORD size = SizeofResource(NULL, hr);
    const BYTE* blob = (const BYTE*)LockResource(hg);
    if (!blob || !size) { Log(L"resource empty"); return FALSE; }

    if (!DeviceIoControl(g_hPawn, PAWNIO_CTL_LOAD, (LPVOID)blob, size, NULL, 0, NULL, NULL))
    {
        Log(L"module load failed err=%lu", GetLastError());
        return FALSE;
    }
    return TRUE;
}

static BOOL PawnReadMsr(DWORD index, UINT64* value)
{
    BYTE inBuf[PAWNIO_FN_NAME_LEN + 8];
    memset(inBuf, 0, sizeof(inBuf));
    memcpy(inBuf, "ioctl_read_msr", 14);
    *(UINT64*)(inBuf + PAWNIO_FN_NAME_LEN) = index;
    UINT64 out = 0;
    DWORD br = 0;
    if (!DeviceIoControl(g_hPawn, PAWNIO_CTL_EXEC, inBuf, sizeof(inBuf), &out, sizeof(out), &br, NULL) || br < 8)
        return FALSE;
    *value = out;
    return TRUE;
}

static void PawnInvalidate(void)
{
    if (g_hPawn != INVALID_HANDLE_VALUE) CloseHandle(g_hPawn);
    g_hPawn = INVALID_HANDLE_VALUE;
    g_moduleLoaded = FALSE;
    g_temperatureTargetsReady = FALSE;
    g_hasPerfBaseline = FALSE;
}

// 保证设备可用；失败返回 FALSE（本轮显示 --）
static BOOL EnsurePawn(void)
{
    if (g_moduleLoaded) return TRUE;
    if (PawnOpen())
    {
        if (PawnLoadModule())
        {
            g_moduleLoaded = TRUE;
            g_openFailStreak = 0;
            return TRUE;
        }
        CloseHandle(g_hPawn);
        g_hPawn = INVALID_HANDLE_VALUE;
    }
    g_openFailStreak++;
    if (g_openFailStreak == 10)
    {
        Log(L"pawnio device unavailable; continuing to retry (streak=%d)", g_openFailStreak);
    }
    return FALSE;
}

// ---------- CPU 拓扑 ----------
static void InitCpuTopology(void)
{
    DWORD len = 0;
    GetLogicalProcessorInformationEx(RelationProcessorCore, NULL, &len);
    if (!len) return;
    BYTE* buf = (BYTE*)malloc(len);
    if (!buf) return;
    if (!GetLogicalProcessorInformationEx(RelationProcessorCore, (PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX)buf, &len))
    {
        free(buf);
        return;
    }
    DWORD off = 0;
    while (off + 8 <= len)
    {
        PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX p = (PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX)(buf + off);
        if (p->Relationship == RelationProcessorCore && g_coreCount < 128)
        {
            CORE_ENTRY* c = &g_cores[g_coreCount];
            c->group = p->Processor.GroupMask[0].Group;
            c->mask = p->Processor.GroupMask[0].Mask;
            c->effClass = p->Processor.EfficiencyClass;
            c->tjMax = 100.0f;
            if (g_perfCoreIdx < 0 || c->effClass > g_cores[g_perfCoreIdx].effClass)
                g_perfCoreIdx = (int)g_coreCount; // 选择效率等级最高的核心
            g_coreCount++;
        }
        if (p->Size < 8) break;
        off += p->Size;
    }
    free(buf);
    g_perfCoreIdx = g_perfCoreIdx < 0 ? 0 : g_perfCoreIdx;

    if (EnsurePawn())
    {
        // 每核 TjMax + 基频
        GROUP_AFFINITY old;
        GetThreadGroupAffinity(GetCurrentThread(), &old);
        for (DWORD i = 0; i < g_coreCount; i++)
        {
            GROUP_AFFINITY ga = {0};
            ga.Group = g_cores[i].group;
            ga.Mask = g_cores[i].mask;
            if (SetThreadGroupAffinity(GetCurrentThread(), &ga, NULL))
            {
                UINT64 v;
                if (PawnReadMsr(MSR_TEMPERATURE_TARGET, &v) && ((v >> 16) & 0xFF) != 0)
                    g_cores[i].tjMax = (float)((v >> 16) & 0xFF);
            }
            SetThreadGroupAffinity(GetCurrentThread(), &old, NULL);
        }
        g_temperatureTargetsReady = TRUE;
        UINT64 v;
        if (PawnReadMsr(MSR_PLATFORM_INFO, &v))
            g_baseMHz = (float)(((v >> 8) & 0xFF) * 100);
        if (!g_baseMHz)
        {
            // 回退：CPUID 品牌串里的 GHz
            int regs[13];
            char brand[49];
            for (int i = 0; i < 3; i++)
            {
                regs[0] = 0x80000002 + i;
                __cpuid(regs, regs[0]);
                memcpy(brand + i * 16, &regs[0], 16);
            }
            brand[48] = 0;
            double ghz = 0;
            char* p = strstr(brand, "GHz");
            if (p)
            {
                for (char* q = p; q > brand; q--)
                    if (*q == ' ') { ghz = atof(q + 1); break; }
            }
            g_baseMHz = (float)(ghz * 1000);
        }
    }
    Log(L"cpu cores=%lu perfCore=%d baseMHz=%.0f", g_coreCount, g_perfCoreIdx, g_baseMHz);
}

// ---------- 采样 ----------
static void SampleAll(void)
{
    memset(&g_sample, 0, sizeof(g_sample));
    SampleGpuMemory();
    if (g_gpuLoadValid && !g_loggedGpuLoad)
    {
        Log(L"first GPU 3D load read: %.1f%% (node=%d)", g_gpuLoad, g_gpu3dNode);
        g_loggedGpuLoad = TRUE;
    }

    // --- CPU 温度 + 频率（需要 PawnIO） ---
    if (EnsurePawn())
    {
        GROUP_AFFINITY old;
        GetThreadGroupAffinity(GetCurrentThread(), &old);

        // The driver may have become available after startup or been reopened.
        if (!g_temperatureTargetsReady)
        {
            for (DWORD i = 0; i < g_coreCount; i++)
            {
                GROUP_AFFINITY ga = {};
                ga.Group = g_cores[i].group;
                ga.Mask = g_cores[i].mask;
                UINT64 target;
                if (SetThreadGroupAffinity(GetCurrentThread(), &ga, NULL) &&
                    PawnReadMsr(MSR_TEMPERATURE_TARGET, &target) && ((target >> 16) & 0xFF))
                    g_cores[i].tjMax = (float)((target >> 16) & 0xFF);
            }
            SetThreadGroupAffinity(GetCurrentThread(), &old, NULL);
            g_temperatureTargetsReady = TRUE;
        }

        float maxTemp = -1000.0f;
        BOOL anyValid = FALSE;
        BOOL packageValid = FALSE;
        UINT64 aperf = 0, mperf = 0;
        BOOL aperfValid = FALSE, mperfValid = FALSE;

        for (DWORD i = 0; i < g_coreCount; i++)
        {
            if (packageValid && i != (DWORD)g_perfCoreIdx) continue;
            GROUP_AFFINITY ga = {0};
            ga.Group = g_cores[i].group;
            ga.Mask = g_cores[i].mask;
            if (!SetThreadGroupAffinity(GetCurrentThread(), &ga, NULL))
                continue;

            UINT64 v;
            if (i == 0 && PawnReadMsr(MSR_PACKAGE_THERM_STATUS, &v) && ((v >> 31) & 1))
            {
                maxTemp = g_cores[i].tjMax - (float)((v & 0x007F0000) >> 16);
                anyValid = packageValid = TRUE;
            }
            if (i == (DWORD)g_perfCoreIdx)
            {
                aperfValid = PawnReadMsr(MSR_APERF, &aperf);
                mperfValid = PawnReadMsr(MSR_MPERF, &mperf);
            }
            if (!packageValid && PawnReadMsr(MSR_THERM_STATUS, &v) && ((v >> 31) & 1))
            {
                float temp = g_cores[i].tjMax - (float)((v & 0x007F0000) >> 16);
                if (temp > maxTemp) maxTemp = temp;
                anyValid = TRUE;
            }
        }
        SetThreadGroupAffinity(GetCurrentThread(), &old, NULL);

        if (anyValid)
        {
            g_temperatureSamples[g_temperatureIndex] = maxTemp;
            g_temperatureIndex = (g_temperatureIndex + 1) % 3;
            if (g_temperatureCount < 3) g_temperatureCount++;
            float sorted[3];
            for (unsigned i = 0; i < g_temperatureCount; i++) sorted[i] = g_temperatureSamples[i];
            for (unsigned i = 1; i < g_temperatureCount; i++)
            {
                float value = sorted[i];
                unsigned j = i;
                while (j > 0 && sorted[j - 1] > value) { sorted[j] = sorted[j - 1]; j--; }
                sorted[j] = value;
            }
            g_sample.tempValid = TRUE;
            g_sample.tempC = sorted[g_temperatureCount / 2];
            if (!s_loggedFirstTemp)
            {
                Log(L"first temperature read: %.1f C (%s)", maxTemp, packageValid ? L"package" : L"core max");
                s_loggedFirstTemp = TRUE;
            }
            s_readFailStreak = 0;
        }
        else if (g_moduleLoaded)
        {
            g_temperatureCount = 0;
            g_temperatureIndex = 0;
            // 设备运行中失联（安装器 PnP churn 等）：连续失败后重建句柄
            if (++s_readFailStreak >= 5)
            {
                Log(L"msr reads failing, recycling pawnio handle");
                s_readFailStreak = 0;
                PawnInvalidate();
            }
        }

        if (aperfValid && mperfValid && g_baseMHz > 0 && g_hasPerfBaseline &&
            aperf >= g_prevAperf && mperf > g_prevMperf)
        {
            double ratio = (double)(aperf - g_prevAperf) / (double)(mperf - g_prevMperf);
            if (ratio > 0 && ratio < 8)
                g_sample.freqMHz = (float)(g_baseMHz * ratio);
        }
        g_prevAperf = aperf;
        g_prevMperf = mperf;
        g_hasPerfBaseline = aperfValid && mperfValid;
    }

    // --- CPU 负载 ---
    FILETIME idle, kernel, user;
    if (GetSystemTimes(&idle, &kernel, &user))
    {
        UINT64 i = ((UINT64)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
        UINT64 kn = ((UINT64)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime;
        UINT64 us = ((UINT64)user.dwHighDateTime << 32) | user.dwLowDateTime;
        UINT64 total = kn + us;
        if (total > g_prevKernel + g_prevUser && g_prevKernel)
        {
            UINT64 dTotal = total - (g_prevKernel + g_prevUser);
            UINT64 dIdle = i - g_prevIdle;
            if (dTotal > 0)
                g_sample.loadPct = (float)(100.0 * (double)(dTotal - dIdle) / (double)dTotal);
        }
        g_prevIdle = i; g_prevKernel = kn; g_prevUser = us;
    }

    // --- 内存 ---
    MEMORYSTATUSEX ms;
    ms.dwLength = sizeof(ms);
    if (GlobalMemoryStatusEx(&ms))
    {
        g_sample.memTotalGB = ms.ullTotalPhys / 1073741824.0;
        g_sample.memUsedGB = (ms.ullTotalPhys - ms.ullAvailPhys) / 1073741824.0;
    }

    // --- 网速 ---
    PMIB_IF_TABLE2 table = NULL;
    if (GetIfTable2(&table) == NO_ERROR && table)
    {
        UINT64 inOct = 0, outOct = 0;
        for (ULONG i = 0; i < table->NumEntries; i++)
        {
            MIB_IF_ROW2* row = &table->Table[i];
            if (row->Type != IF_TYPE_ETHERNET_CSMACD && row->Type != IF_TYPE_IEEE80211)
                continue;
            // 排除虚拟/过滤伪接口（WAN Miniport、Wi-Fi Direct、NDIS 过滤器）
            wchar_t desc[300];
            wcsncpy_s(desc, 300, row->Description, _TRUNCATE);
            _wcslwr_s(desc, 300);
            if (wcsstr(desc, L"wan miniport") || wcsstr(desc, L"kernel debug") ||
                wcsstr(desc, L"wi-fi direct") || wcsstr(desc, L"virtual adapter"))
                continue;
            wchar_t alias[300];
            wcsncpy_s(alias, 300, row->Alias, _TRUNCATE);
            size_t alen = wcslen(alias);
            if (alen >= 5 && alias[alen - 5] == L'-' && iswdigit((wint_t)alias[alen - 1]))
                continue;
            inOct += row->InOctets;
            outOct += row->OutOctets;
        }
        FreeMibTable(table);
        ULONGLONG now = GetTickCount64();
        ULONGLONG elapsed = now - g_prevNetTime;
        if (g_hasNetBaseline && elapsed > 0)
        {
            g_sample.netDownBps = inOct > g_prevInOct ? (UINT64)((inOct - g_prevInOct) * 1000.0 / elapsed) : 0;
            g_sample.netUpBps = outOct > g_prevOutOct ? (UINT64)((outOct - g_prevOutOct) * 1000.0 / elapsed) : 0;
        }
        g_prevInOct = inOct; g_prevOutOct = outOct;
        g_prevNetTime = now;
        g_hasNetBaseline = TRUE;
    }
}

// ---------- 图标渲染（GDI+ 平面 API） ----------
static GdiplusStartupInput g_gdiplusStartup;
static ULONG_PTR g_gdiplusToken;

static HICON RenderIcon(const wchar_t* text, COLORREF rgb, int size)
{
    HICON hicon = NULL;
    GpBitmap* bmp = NULL;
    GpGraphics* gfx = NULL;
    GpFontFamily* family = NULL;
    GpFont* font = NULL;
    GpSolidFill* textBrush = NULL;
    float em;
    if (g_iconStyle == ICON_MONO) em = wcslen(text) >= 3 ? size * (size <= 16 ? 0.54f : 0.60f) : size * (size <= 16 ? 0.76f : 0.80f);
    else if (g_iconStyle == ICON_NUMBER) em = size * (wcslen(text) >= 3 ? 0.60f : 0.78f);
    else em = size * (wcslen(text) >= 3 ? 0.40f : 0.50f);
    em = roundf(em * 2.0f) / 2.0f;
    if (em < 6.0f) em = 6.0f;

    if (GdipCreateBitmapFromScan0(size, size, 0, PixelFormat32bppPARGB, NULL, &bmp) != Ok)
        return NULL;
    if (GdipGetImageGraphicsContext(bmp, &gfx) != Ok) goto done;
    GdipSetTextRenderingHint(gfx, TextRenderingHintAntiAliasGridFit);
    GdipSetSmoothingMode(gfx, SmoothingModeAntiAlias);

    if (g_iconStyle == ICON_PILL || g_iconStyle == ICON_RING)
    {
        float margin = g_iconStyle == ICON_RING ? 1.0f : 0.0f;
        float extent = size - (g_iconStyle == ICON_RING ? 3.0f : 1.0f);
        float radius = (float)(g_iconStyle == ICON_RING ? size / 3 : size / 4);
        if (radius < 3) radius = 3;
        float diameter = radius * 2;
        GpPath* path = NULL;
        if (GdipCreatePath(FillModeAlternate, &path) == Ok)
        {
            GdipAddPathArc(path, margin, margin, diameter, diameter, 180, 90);
            GdipAddPathArc(path, margin + extent - diameter, margin, diameter, diameter, 270, 90);
            GdipAddPathArc(path, margin + extent - diameter, margin + extent - diameter, diameter, diameter, 0, 90);
            GdipAddPathArc(path, margin, margin + extent - diameter, diameter, diameter, 90, 90);
            GdipClosePathFigure(path);
            ARGB accent = 0xFF000000 | (GetRValue(rgb) << 16) | (GetGValue(rgb) << 8) | GetBValue(rgb);
            if (g_iconStyle == ICON_RING)
            {
                GpSolidFill* background = NULL; GpPen* pen = NULL;
                if (GdipCreateSolidFill(0xFF16181E, &background) == Ok) { GdipFillPath(gfx, background, path); GdipDeleteBrush(background); }
                if (GdipCreatePen1(accent, size / 16.0f > 1 ? size / 16.0f : 1.0f, UnitPixel, &pen) == Ok) { GdipDrawPath(gfx, pen, path); GdipDeletePen(pen); }
            }
            else
            {
                int r = GetRValue(rgb), g = GetGValue(rgb), b = GetBValue(rgb);
                ARGB light = 0xFF000000 | ((r + 45 > 255 ? 255 : r + 45) << 16) | ((g + 45 > 255 ? 255 : g + 45) << 8) | (b + 45 > 255 ? 255 : b + 45);
                ARGB dark = 0xFF000000 | ((r < 40 ? 0 : r - 40) << 16) | ((g < 40 ? 0 : g - 40) << 8) | (b < 40 ? 0 : b - 40);
                RectF rect = {margin, margin, extent, extent}; GpLineGradient* gradient = NULL;
                if (GdipCreateLineBrushFromRect(&rect, light, dark, LinearGradientModeVertical, WrapModeTile, &gradient) == Ok) { GdipFillPath(gfx, gradient, path); GdipDeleteBrush(gradient); }
            }
            GdipDeletePath(path);
        }
    }

    if (GdipCreateFontFamilyFromName(g_iconStyle == ICON_MONO ? L"Segoe UI Variable Small Semibold" : L"Segoe UI", NULL, &family) != Ok || !family)
        GdipCreateFontFamilyFromName(L"Segoe UI", NULL, &family);
    if (!family) goto done;

    if (GdipCreateFont(family, em, g_iconStyle == ICON_MONO ? FontStyleRegular : FontStyleBold, UnitPixel, &font) != Ok || !font)
        goto done;

    if (GdipCreateSolidFill(g_iconStyle == ICON_PILL || g_iconStyle == ICON_RING ? 0xFFFFFFFF : 0xFF000000 | (GetRValue(rgb) << 16) | (GetGValue(rgb) << 8) | GetBValue(rgb), &textBrush) != Ok) goto done;
    {
        RectF layout = {0, 0, 0, 0}, measured = {};
        if (GdipMeasureString(gfx, text, -1, font, &layout, NULL, &measured, NULL, NULL) != Ok) goto done;
        float offset = g_iconStyle == ICON_MONO ? 0.04f : g_iconStyle == ICON_NUMBER ? 0.06f : 0.03f;
        RectF rect = {(size - measured.Width) / 2.0f, (size - measured.Height) / 2.0f - size * offset, 0, 0};
        if (g_iconStyle == ICON_NUMBER)
        {
            GpSolidFill* shadow = NULL;
            if (GdipCreateSolidFill(0x96000000, &shadow) == Ok)
            {
                float o = size / 22.0f > 0.7f ? size / 22.0f : 0.7f;
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++)
                    if (x || y) { RectF shifted = rect; shifted.X += x * o; shifted.Y += y * o; GdipDrawString(gfx, text, -1, font, &shifted, NULL, shadow); }
                GdipDeleteBrush(shadow);
            }
        }
        GdipDrawString(gfx, text, -1, font, &rect, NULL, textBrush);
    }
    GdipCreateHICONFromBitmap(bmp, &hicon);

done:
    if (textBrush) GdipDeleteBrush(textBrush);
    if (font) GdipDeleteFont(font);
    if (family) GdipDeleteFontFamily(family);
    if (gfx) GdipDeleteGraphics(gfx);
    if (bmp) GdipDisposeImage(bmp);
    return hicon;
}

// ---------- 格式化 ----------
static void FormatSpeed(UINT64 bps, wchar_t* out, size_t cch)
{
    if (bps >= 1024 * 1024) swprintf(out, cch, L"%.1f MB/s", bps / 1048576.0);
    else if (bps >= 1024) swprintf(out, cch, L"%.0f KB/s", bps / 1024.0);
    else swprintf(out, cch, L"%llu B/s", bps);
}

static void UpdateDetails(void)
{
    if (g_hDetails) InvalidateRect(g_hDetails, NULL, FALSE);
}

static void DrawDetailRow(HDC dc, int y, const wchar_t* title, const wchar_t* value, COLORREF label, COLORREF text)
{
    SetTextColor(dc, label);
    TextOutW(dc, 75, y, title, (int)wcslen(title));
    SetTextColor(dc, text);
    TextOutW(dc, 140, y, value, (int)wcslen(value));
}

static void DrawDetailCard(HDC dc, int top, int bottom, const wchar_t* category, const wchar_t* title,
    COLORREF fill, COLORREF border, COLORREF tile, COLORREF accent, COLORREF text, HFONT titleFont)
{
    HBRUSH brush = CreateSolidBrush(fill);
    HPEN pen = CreatePen(PS_SOLID, 1, border);
    HGDIOBJ oldBrush = SelectObject(dc, brush), oldPen = SelectObject(dc, pen);
    RoundRect(dc, 12, top, 338, bottom, 20, 20);
    SelectObject(dc, oldBrush); SelectObject(dc, oldPen);
    DeleteObject(brush); DeleteObject(pen);
    brush = CreateSolidBrush(tile);
    oldBrush = SelectObject(dc, brush);
    oldPen = SelectObject(dc, GetStockObject(NULL_PEN));
    RoundRect(dc, 20, top + 10, 60, top + 50, 10, 10);
    SelectObject(dc, oldBrush); SelectObject(dc, oldPen); DeleteObject(brush);
    HPEN iconPen = CreatePen(PS_SOLID, 1, accent);
    oldPen = SelectObject(dc, iconPen);
    oldBrush = SelectObject(dc, GetStockObject(NULL_BRUSH));
    int x = 25, y = top + 15;
    if (wcscmp(category, L"CPU") == 0)
    {
        Rectangle(dc, x + 5, y + 5, x + 17, y + 17);
        for (int i = 0; i < 3; i++)
        {
            int d = 7 + i * 4;
            MoveToEx(dc, x + d, y + 1, NULL); LineTo(dc, x + d, y + 5);
            MoveToEx(dc, x + d, y + 17, NULL); LineTo(dc, x + d, y + 21);
            MoveToEx(dc, x + 1, y + d, NULL); LineTo(dc, x + 5, y + d);
            MoveToEx(dc, x + 17, y + d, NULL); LineTo(dc, x + 21, y + d);
        }
    }
    else if (wcscmp(category, L"GPU") == 0)
    {
        Rectangle(dc, x + 2, y + 5, x + 21, y + 18);
        Ellipse(dc, x + 6, y + 7, x + 16, y + 16);
        MoveToEx(dc, x + 21, y + 9, NULL); LineTo(dc, x + 24, y + 9);
        MoveToEx(dc, x + 21, y + 14, NULL); LineTo(dc, x + 24, y + 14);
    }
    else
    {
        Rectangle(dc, x + 2, y + 6, x + 22, y + 17);
        for (int i = 0; i < 4; i++)
        {
            int d = 5 + i * 4;
            MoveToEx(dc, x + d, y + 8, NULL); LineTo(dc, x + d, y + 15);
            MoveToEx(dc, x + d, y + 17, NULL); LineTo(dc, x + d, y + 20);
        }
    }
    SelectObject(dc, oldPen); SelectObject(dc, oldBrush); DeleteObject(iconPen);
    HGDIOBJ oldFont = SelectObject(dc, titleFont);
    SetTextColor(dc, text);
    RECT titleRect = {75, top + 8, 330, top + 28};
    DrawTextW(dc, title, -1, &titleRect, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
    SelectObject(dc, oldFont);
}

static void PaintDetails(HWND hwnd, HDC target)
{
    RECT bounds; GetClientRect(hwnd, &bounds);
    HDC dc = CreateCompatibleDC(target);
    HBITMAP bitmap = CreateCompatibleBitmap(target, bounds.right, bounds.bottom);
    HGDIOBJ oldBitmap = SelectObject(dc, bitmap);
    DWORD light = 1, size = sizeof(light);
    RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
        L"AppsUseLightTheme", RRF_RT_REG_DWORD, NULL, &light, &size);
    HIGHCONTRASTW hc = {sizeof(hc)};
    SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(hc), &hc, 0);
    BOOL high = (hc.dwFlags & HCF_HIGHCONTRASTON) != 0;
    BOOL dark = !high && (light == 0 || g_previewDark);
    COLORREF bg = high ? GetSysColor(COLOR_WINDOW) : dark ? RGB(30,33,38) : RGB(235,240,247);
    COLORREF fill = high ? GetSysColor(COLOR_WINDOW) : dark ? RGB(39,43,50) : RGB(250,252,255);
    COLORREF border = high ? GetSysColor(COLOR_WINDOWTEXT) : dark ? RGB(67,74,84) : RGB(220,229,240);
    COLORREF tile = high ? GetSysColor(COLOR_HIGHLIGHT) : dark ? RGB(49,61,79) : RGB(226,237,252);
    COLORREF accent = high ? GetSysColor(COLOR_HIGHLIGHTTEXT) : dark ? RGB(174,201,244) : RGB(49,105,179);
    COLORREF text = high ? GetSysColor(COLOR_WINDOWTEXT) : dark ? RGB(241,244,248) : RGB(0,0,0);
    COLORREF label = high ? GetSysColor(COLOR_GRAYTEXT) : dark ? RGB(181,190,202) : RGB(96,106,119);
    HBRUSH background = CreateSolidBrush(bg); FillRect(dc, &bounds, background); DeleteObject(background);
    SetMapMode(dc, MM_ANISOTROPIC);
    SetWindowExtEx(dc, 350, 300, NULL); SetViewportExtEx(dc, bounds.right, bounds.bottom, NULL);
    SetBkMode(dc, TRANSPARENT);
    HFONT normal = CreateFontW(-14,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Microsoft YaHei UI");
    HFONT bold = CreateFontW(-15,0,0,0,FW_SEMIBOLD,FALSE,FALSE,FALSE,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Microsoft YaHei UI");
    HGDIOBJ oldFont = SelectObject(dc, normal);
    wchar_t cpu[128] = L"CPU"; DWORD cpuBytes = sizeof(cpu);
    RegGetValueW(HKEY_LOCAL_MACHINE, L"HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0", L"ProcessorNameString", RRF_RT_REG_SZ, NULL, cpu, &cpuBytes);
    DrawDetailCard(dc, 10, 126, L"CPU", cpu, fill, border, tile, accent, text, bold);
    DrawDetailCard(dc, 132, 218, L"GPU", g_gpuName, fill, border, tile, accent, text, bold);
    DrawDetailCard(dc, 224, 288, L"RAM", L"物理内存", fill, border, tile, accent, text, bold);
    wchar_t value[100], down[24], up[24];
    if (g_sample.tempValid) swprintf(value, 100, L"%.1f °C", g_sample.tempC); else wcscpy_s(value, 100, L"未读到温度");
    DrawDetailRow(dc, 39, L"温度", value, label, text);
    swprintf(value, 100, L"%.0f %%", g_sample.loadPct); DrawDetailRow(dc, 59, L"负载", value, label, text);
    if (g_sample.freqMHz > 0) swprintf(value, 100, L"%.2f GHz", g_sample.freqMHz / 1000.0f); else wcscpy_s(value, 100, L"—");
    DrawDetailRow(dc, 79, L"频率", value, label, text);
    FormatSpeed(g_sample.netDownBps, down, 24); FormatSpeed(g_sample.netUpBps, up, 24);
    swprintf(value, 100, L"↓ %s   ↑ %s", down, up); DrawDetailRow(dc, 99, L"网速", value, label, text);
    if (g_gpuLoadValid) swprintf(value, 100, L"%.1f %%", g_gpuLoad); else wcscpy_s(value, 100, L"—");
    DrawDetailRow(dc, 161, L"负载", value, label, text);
    if (g_gpuMemoryValid) swprintf(value, 100, L"共享 %.1f / %.1f GB", g_gpuSharedUsed / 1073741824.0, g_gpuSharedTotal / 1073741824.0); else wcscpy_s(value, 100, L"—");
    DrawDetailRow(dc, 183, L"显存", value, label, text);
    swprintf(value, 100, L"%.1f / %.1f GB", g_sample.memUsedGB, g_sample.memTotalGB);
    DrawDetailRow(dc, 253, L"用量", value, label, text);
    SelectObject(dc, oldFont); DeleteObject(normal); DeleteObject(bold);
    SetMapMode(dc, MM_TEXT);
    BitBlt(target, 0, 0, bounds.right, bounds.bottom, dc, 0, 0, SRCCOPY);
    SelectObject(dc, oldBitmap); DeleteObject(bitmap); DeleteDC(dc);
}

static void ApplyDetailsTitleTheme(HWND hwnd)
{
    DWORD light = 1, bytes = sizeof(light);
    RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", L"AppsUseLightTheme", RRF_RT_REG_DWORD, NULL, &light, &bytes);
    HIGHCONTRASTW contrast = {sizeof(contrast)};
    SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(contrast), &contrast, 0);
    BOOL high = (contrast.dwFlags & HCF_HIGHCONTRASTON) != 0;
    BOOL darkTitle = !high && (light == 0 || g_previewDark);
    DwmSetWindowAttribute(hwnd, 20, &darkTitle, sizeof(darkTitle));
    COLORREF caption = high ? (COLORREF)-1 : darkTitle ? RGB(30,33,38) : RGB(235,240,247);
    COLORREF border = high ? (COLORREF)-1 : darkTitle ? RGB(67,74,84) : RGB(220,229,240);
    COLORREF titleText = high ? (COLORREF)-1 : darkTitle ? RGB(241,244,248) : RGB(0,0,0);
    DwmSetWindowAttribute(hwnd, 34, &border, sizeof(border));
    DwmSetWindowAttribute(hwnd, 35, &caption, sizeof(caption));
    DwmSetWindowAttribute(hwnd, 36, &titleText, sizeof(titleText));
}

static LRESULT CALLBACK DetailsProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    if (msg == WM_PAINT) { PAINTSTRUCT ps; HDC dc = BeginPaint(hwnd, &ps); PaintDetails(hwnd, dc); EndPaint(hwnd, &ps); return 0; }
    if (msg == WM_ERASEBKGND) return 1;
    if (msg == WM_SETTINGCHANGE || msg == WM_THEMECHANGED) { ApplyDetailsTitleTheme(hwnd); InvalidateRect(hwnd, NULL, FALSE); }
    if (msg == WM_DESTROY)
    {
        g_hDetails = NULL;
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

static void ShowDetails(void)
{
    if (!g_hDetails)
    {
        HINSTANCE instance = GetModuleHandleW(NULL);
        WNDCLASSW wc = {0};
        wc.lpfnWndProc = DetailsProc;
        wc.hInstance = instance;
        wc.lpszClassName = L"TaskbarTempCppDetails";
        wc.hCursor = LoadCursorW(NULL, IDC_ARROW);
        wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
        RegisterClassW(&wc);
        UINT dpi = GetDpiForSystem();
        RECT frame = {0, 0, MulDiv(350, dpi, 96), MulDiv(300, dpi, 96)};
        DWORD exStyle = g_preview ? 0 : WS_EX_TOOLWINDOW;
        AdjustWindowRectExForDpi(&frame, WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU, FALSE, exStyle, dpi);
        g_hDetails = CreateWindowExW(exStyle, wc.lpszClassName, L"TaskbarTemp 详情",
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU, CW_USEDEFAULT, CW_USEDEFAULT,
            frame.right - frame.left, frame.bottom - frame.top, NULL, NULL, instance, NULL);
        if (!g_hDetails) return;
    }
    UpdateDetails();
    POINT point; GetCursorPos(&point);
    MONITORINFO monitor = {sizeof(monitor)};
    if (GetMonitorInfoW(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST), &monitor))
    {
        RECT window; GetWindowRect(g_hDetails, &window);
        int margin = MulDiv(12, GetDpiForSystem(), 96);
        int x = monitor.rcWork.right - (window.right - window.left) - margin;
        int y = monitor.rcWork.bottom - (window.bottom - window.top) - margin;
        if (x < monitor.rcWork.left + margin) x = monitor.rcWork.left + margin;
        if (y < monitor.rcWork.top + margin) y = monitor.rcWork.top + margin;
        SetWindowPos(g_hDetails, NULL, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
    ApplyDetailsTitleTheme(g_hDetails);
    ShowWindow(g_hDetails, SW_SHOWNORMAL);
    SetForegroundWindow(g_hDetails);
}

static void CopyTemperature(void)
{
    if (!g_sample.tempValid || !OpenClipboard(g_hWnd)) return;
    wchar_t text[32];
    swprintf(text, 32, L"%.1f °C", g_sample.tempC);
    size_t bytes = (wcslen(text) + 1) * sizeof(wchar_t);
    HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, bytes);
    if (memory)
    {
        void* destination = GlobalLock(memory);
        if (destination)
        {
            memcpy(destination, text, bytes);
            GlobalUnlock(memory);
            if (!EmptyClipboard() || !SetClipboardData(CF_UNICODETEXT, memory)) GlobalFree(memory);
        }
        else GlobalFree(memory);
    }
    CloseClipboard();
}

static void OpenIconSettings(void)
{
    if (!g_configPath[0]) return;
    if (GetFileAttributesW(g_configPath) == INVALID_FILE_ATTRIBUTES)
    {
        FILE* file = NULL;
        if (_wfopen_s(&file, g_configPath, L"wt, ccs=UTF-8") != 0 || !file)
        {
            MessageBoxW(g_hWnd, L"无法创建配置文件。", L"TaskbarTemp", MB_OK | MB_ICONERROR);
            return;
        }
        fwprintf(file, L"# style = mono | number | pill | ring\nstyle = mono\n");
        fclose(file);
    }
    wchar_t quoted[MAX_PATH + 3];
    swprintf(quoted, MAX_PATH + 3, L"\"%s\"", g_configPath);
    ShellExecuteW(g_hWnd, L"open", L"notepad.exe", quoted, NULL, SW_SHOWNORMAL);
}

static void ManageAutostart(BOOL install)
{
    wchar_t exe[MAX_PATH], systemDir[MAX_PATH], powershell[MAX_PATH];
    if (!GetModuleFileNameW(NULL, exe, MAX_PATH) || !GetSystemDirectoryW(systemDir, MAX_PATH)) return;
    swprintf(powershell, MAX_PATH, L"%s\\WindowsPowerShell\\v1.0\\powershell.exe", systemDir);
    const wchar_t* installScript =
        L"$ErrorActionPreference='Stop';try{"
        L"$owner='Ccmra404.TaskbarTemp.Startup.v1';"
        L"$t=Get-ScheduledTask -TaskName 'TaskbarTemp' -TaskPath '\\' -ErrorAction SilentlyContinue;"
        L"$exe=[IO.Path]::GetFullPath($env:TASKBARTEMP_STARTUP_EXE);$existing=$null;"
        L"if($t -and $t.Actions.Count -eq 1 -and $t.Actions[0].Execute){$existing=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($t.Actions[0].Execute.Trim().Trim([char]34)))};"
        L"$same=$existing -and [string]::Equals($existing,$exe,[StringComparison]::OrdinalIgnoreCase);"
        L"if($t -and $t.Description -ne $owner -and -not $same){exit 3};"
        L"$a=New-ScheduledTaskAction -Execute $exe;$trigger=New-ScheduledTaskTrigger -AtLogOn;"
        L"$p=New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest;"
        L"$s=New-ScheduledTaskSettingsSet -DontStopIfGoingOnBatteries -AllowStartIfOnBatteries -ExecutionTimeLimit (New-TimeSpan -Seconds 0) -StartWhenAvailable;"
        L"Register-ScheduledTask -TaskName 'TaskbarTemp' -TaskPath '\\' -Action $a -Trigger $trigger -Principal $p -Settings $s -Description $owner -Force | Out-Null;exit 0}catch{exit 1}";
    const wchar_t* removeScript =
        L"$ErrorActionPreference='Stop';try{"
        L"$t=Get-ScheduledTask -TaskName 'TaskbarTemp' -TaskPath '\\' -ErrorAction SilentlyContinue;"
        L"$exe=[IO.Path]::GetFullPath($env:TASKBARTEMP_STARTUP_EXE);$existing=$null;"
        L"if($t -and $t.Actions.Count -eq 1 -and $t.Actions[0].Execute){$existing=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($t.Actions[0].Execute.Trim().Trim([char]34)))};"
        L"if($existing -and [string]::Equals($existing,$exe,[StringComparison]::OrdinalIgnoreCase)){Unregister-ScheduledTask -TaskName 'TaskbarTemp' -TaskPath '\\' -Confirm:$false;exit 0};exit 3}catch{exit 1}";
    wchar_t command[8192];
    swprintf(command, 8192, L"\"%s\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"%s\"", powershell, install ? installScript : removeScript);
    SetEnvironmentVariableW(L"TASKBARTEMP_STARTUP_EXE", exe);
    STARTUPINFOW startup = {sizeof(startup)};
    PROCESS_INFORMATION process = {};
    BOOL started = CreateProcessW(powershell, command, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &startup, &process);
    SetEnvironmentVariableW(L"TASKBARTEMP_STARTUP_EXE", NULL);
    if (!started) { MessageBoxW(g_hWnd, L"无法启动开机自启设置。", L"TaskbarTemp", MB_OK | MB_ICONERROR); return; }
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD result = 1; GetExitCodeProcess(process.hProcess, &result);
    CloseHandle(process.hThread); CloseHandle(process.hProcess);
    const wchar_t* message = result == 0 ? (install ? L"已设置开机自启，使用电池时也会运行。" : L"已移除开机自启。") :
        result == 3 ? L"同名任务属于其他程序或安装目录，未修改。" : L"设置失败，请检查任务计划程序。";
    MessageBoxW(g_hWnd, message, L"TaskbarTemp", MB_OK | (result == 0 ? MB_ICONINFORMATION : MB_ICONWARNING));
}

// ---------- 托盘 ----------
static void UpdateTray(void)
{
    wchar_t text[8];
    UINT64 colorKey;
    DWORD light = 1, bytes = sizeof(light);
    RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", L"SystemUsesLightTheme", RRF_RT_REG_DWORD, NULL, &light, &bytes);
    COLORREF glyph = g_iconStyle == ICON_MONO ? (light ? RGB(26, 26, 30) : RGB(245, 245, 245)) :
        (g_sample.tempValid ? TempColor(g_sample.tempC) : RGB(150, 150, 150));
    if (g_sample.tempValid)
    {
        swprintf(text, 8, L"%.0f", g_sample.tempC);
        colorKey = (UINT64)glyph << 8 | wcslen(text);
    }
    else
    {
        wcscpy_s(text, 8, L"--");
        colorKey = (UINT64)glyph << 8 | 2;
    }

    BOOL iconChanged = (wcscmp(text, g_lastIconText) != 0) || colorKey != g_lastIconColor;
    if (iconChanged)
    {
        int iconSize = GetSystemMetrics(SM_CXSMICON);
        if (iconSize < 16) iconSize = 16;
        COLORREF rgb = (COLORREF)(colorKey >> 8);
        HICON hNew = RenderIcon(text, rgb, iconSize);
        if (hNew)
        {
            if (g_hIcon) DestroyIcon(g_hIcon);
            g_hIcon = hNew;
            g_nid.hIcon = g_hIcon;
        }
        wcscpy_s(g_lastIconText, 8, text);
        g_lastIconColor = colorKey;
    }

    wchar_t tip[128];
    wchar_t down[24], up[24];
    FormatSpeed(g_sample.netDownBps, down, 24);
    FormatSpeed(g_sample.netUpBps, up, 24);
    if (g_sample.tempValid)
        swprintf(tip, 128, L"CPU %.0f°C  %.0f%%  %.2f GHz\nRAM %.1f / %.1f GB\n↓ %s  ↑ %s",
                 g_sample.tempC, g_sample.loadPct, g_sample.freqMHz / 1000.0f,
                 g_sample.memUsedGB, g_sample.memTotalGB, down, up);
    else
        swprintf(tip, 128, L"CPU 温度不可读（检查 PawnIO）\nRAM %.1f / %.1f GB\n↓ %s  ↑ %s",
                 g_sample.memUsedGB, g_sample.memTotalGB, down, up);
    if (g_gpuLoadValid)
    {
        wchar_t gpuTip[32];
        swprintf(gpuTip, 32, L"\nGPU %.0f%%", g_gpuLoad);
        if (wcslen(tip) + wcslen(gpuTip) < 128) wcscat_s(tip, 128, gpuTip);
    }

    if (wcscmp(tip, g_nid.szTip) != 0)
    {
        wcscpy_s(g_nid.szTip, 128, tip);
        g_nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
        Shell_NotifyIconW(NIM_MODIFY, &g_nid);
    }
    else if (iconChanged)
    {
        g_nid.uFlags = NIF_ICON;
        Shell_NotifyIconW(NIM_MODIFY, &g_nid);
    }
}

// ---------- 窗口过程 ----------
static LRESULT CALLBACK WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    if (g_taskbarCreated && msg == g_taskbarCreated)
    {
        g_nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
        if (!Shell_NotifyIconW(NIM_ADD, &g_nid)) Log(L"tray restore failed after Explorer restart");
        return 0;
    }
    switch (msg)
    {
    case WM_TIMER:
        if (wParam == TIMER_ID_SAMPLE)
        {
            SampleAll();
            UpdateTray();
            UpdateDetails();
        }
        else if (wParam == TIMER_ID_HEARTBEAT) WriteHeartbeat();
        return 0;

    case WM_QUERYENDSESSION:
        Log(L"session ending requested");
        return TRUE;
    case WM_ENDSESSION:
        if (wParam) Log(L"session ending confirmed");
        return 0;

    case WM_TRAYICON:
        if (lParam == WM_RBUTTONUP || lParam == WM_CONTEXTMENU)
        {
            POINT pt;
            GetCursorPos(&pt);
            SetForegroundWindow(hWnd);
            TrackPopupMenu(g_hMenu, TPM_RIGHTBUTTON, pt.x, pt.y, 0, hWnd, NULL);
            PostMessage(hWnd, WM_NULL, 0, 0);
        }
        else if (lParam == WM_LBUTTONUP)
        {
            ShowDetails();
        }
        return 0;

    case WM_COMMAND:
        if (LOWORD(wParam) == 2) ShowDetails();
        else if (LOWORD(wParam) == 3) { SampleAll(); UpdateTray(); UpdateDetails(); }
        else if (LOWORD(wParam) == 4) CopyTemperature();
        else if (LOWORD(wParam) == 5) OpenIconSettings();
        else if (LOWORD(wParam) == 6) ManageAutostart(TRUE);
        else if (LOWORD(wParam) == 7) ManageAutostart(FALSE);
        else if (LOWORD(wParam) == 1)
            DestroyWindow(hWnd);
        return 0;

    case WM_DESTROY:
        if (g_hDetails) DestroyWindow(g_hDetails);
        if (g_gpuHandle) { D3DKMT_CLOSEADAPTER close = {}; close.hAdapter = g_gpuHandle; D3DKMTCloseAdapter(&close); g_gpuHandle = 0; }
        Shell_NotifyIconW(NIM_DELETE, &g_nid);
        if (g_hIcon) DestroyIcon(g_hIcon);
        PawnInvalidate();
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hWnd, msg, wParam, lParam);
}

// ---------- 入口 ----------
int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE hPrev, LPWSTR cmd, int show)
{
    (void)hPrev; (void)show;
    g_previewDark = wcscmp(cmd, L"--preview-dark") == 0;
    g_preview = g_previewDark || wcscmp(cmd, L"--preview") == 0;

    g_hSingleInstance = CreateMutexW(NULL, TRUE, L"Global\\TaskbarTemp_SingleInstance");
    if (GetLastError() == ERROR_ALREADY_EXISTS)
    {
        MessageBoxW(NULL, L"TaskbarTempCpp 已在运行（看任务栏托盘）。", L"TaskbarTempCpp", MB_OK | MB_ICONINFORMATION);
        return 0;
    }

    if (GdiplusStartup(&g_gdiplusToken, &g_gdiplusStartup, NULL) != Ok)
        return 1;
    LoadIconStyle();

    WNDCLASSW wc = {0};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInst;
    wc.lpszClassName = L"TaskbarTempCppWnd";
    RegisterClassW(&wc);
    g_taskbarCreated = RegisterWindowMessageW(L"TaskbarCreated");
    g_hWnd = CreateWindowExW(0, L"TaskbarTempCppWnd", L"TaskbarTempCpp", WS_OVERLAPPED,
                             0, 0, 0, 0, NULL, NULL, hInst, NULL);

    g_hMenu = CreatePopupMenu();
    AppendMenuW(g_hMenu, MF_STRING, 2, L"详情");
    AppendMenuW(g_hMenu, MF_STRING, 3, L"立即刷新");
    AppendMenuW(g_hMenu, MF_STRING, 4, L"复制数值");
    AppendMenuW(g_hMenu, MF_STRING, 5, L"图标样式设置（重启后生效）");
    AppendMenuW(g_hMenu, MF_STRING, 6, L"安装开机自启");
    AppendMenuW(g_hMenu, MF_STRING, 7, L"移除开机自启");
    AppendMenuW(g_hMenu, MF_SEPARATOR, 0, NULL);
    AppendMenuW(g_hMenu, MF_STRING, 1, L"退出");

    int iconSize0 = GetSystemMetrics(SM_CXSMICON);
    if (iconSize0 < 16) iconSize0 = 16;
    g_hIcon = RenderIcon(L"--", RGB(150, 150, 150), iconSize0);

    memset(&g_nid, 0, sizeof(g_nid));
    g_nid.cbSize = sizeof(g_nid);
    g_nid.hIcon = g_hIcon;
    g_nid.hWnd = g_hWnd;
    g_nid.uID = 1;
    g_nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    g_nid.uCallbackMessage = WM_TRAYICON;
    wcscpy_s(g_nid.szTip, 128, L"CPU 启动中…");
    if (!Shell_NotifyIconW(NIM_ADD, &g_nid))
    {
        Log(L"Shell_NotifyIcon add failed");
        return 1;
    }

    InitCpuTopology();
    InitGpu();
    Log(L"gpu adapter=%s shared capacity=%.1f GB", g_gpuName, g_gpuSharedTotal / 1073741824.0);
    SampleAll();
    Log(L"gpu shared read: valid=%d used=%.2f GB total=%.2f GB", g_gpuMemoryValid,
        g_gpuSharedUsed / 1073741824.0, g_gpuSharedTotal / 1073741824.0);
    UpdateTray();
    if (g_preview) ShowDetails();
    SetTimer(g_hWnd, TIMER_ID_SAMPLE, SAMPLE_MS, NULL);
    SetTimer(g_hWnd, TIMER_ID_HEARTBEAT, 30000, NULL);
    WriteHeartbeat();
    Log(L"started, cores=%lu icon-style=%d", g_coreCount, (int)g_iconStyle);

    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    GdiplusShutdown(g_gdiplusToken);
    if (g_hSingleInstance) CloseHandle(g_hSingleInstance);
    Log(L"exit");
    return 0;
}
