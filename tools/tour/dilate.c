// Time dilation for filming Mira (never merged, never in a package): every clock of the process runs `factor` times
// slower than the wall's, so that a runner without a graphics card draws each moment of an animation, filmed in real
// time and played back `factor` times faster. Loaded by Checks/TimeDilation.cs; `MiraDilate(factor)` may be called again
// (1 = real time) and each clock continues from where it is.
//
// Hooked (a 5-byte jump to a stub near each function): ntdll's RtlQueryPerformanceCounter and kernel32/kernelbase's
// QueryPerformanceCounter, GetTickCount(64), GetSystemTime(Precise)AsFileTime, Query(Unbiased)InterruptTime(Precise).
// The real time comes from NtQueryPerformanceCounter, a system call none of them uses.
#include <windows.h>
#include <stdio.h>

typedef LONG(NTAPI *NtQpc)(PLARGE_INTEGER, PLARGE_INTEGER);
static NtQpc RealCounter;
static double RealFrequency, QpcFrequency;

typedef struct
{
    double factor;
    LONGLONG real;   // NtQueryPerformanceCounter when this state began
    double qpc;      // the clocks shown at that moment
    double fileTime; // 100 ns
    double tick;     // ms
    double unbiased; // 100 ns
    double interrupt;
} State;
static State* volatile Now;

// Seconds of the dilated clock since the state began.
static double Elapsed(const State* s)
{
    LARGE_INTEGER c, f;
    RealCounter(&c, &f);
    return (double)(c.QuadPart - s->real) / RealFrequency / s->factor;
}

static BOOL WINAPI MyQpc(LARGE_INTEGER* value)
{
    const State* s = Now;
    value->QuadPart = (LONGLONG)(s->qpc + Elapsed(s) * QpcFrequency);
    return TRUE;
}
static void WINAPI MyFileTime(FILETIME* value)
{
    const State* s = Now;
    ULONGLONG t = (ULONGLONG)(s->fileTime + Elapsed(s) * 1e7);
    value->dwLowDateTime = (DWORD)t; value->dwHighDateTime = (DWORD)(t >> 32);
}
static ULONGLONG WINAPI MyTick64(void) { const State* s = Now; return (ULONGLONG)(s->tick + Elapsed(s) * 1000); }
static DWORD WINAPI MyTick(void) { return (DWORD)MyTick64(); }
static BOOL WINAPI MyUnbiased(ULONGLONG* value) { const State* s = Now; *value = (ULONGLONG)(s->unbiased + Elapsed(s) * 1e7); return TRUE; }
static void WINAPI MyUnbiasedPrecise(ULONGLONG* value) { MyUnbiased(value); }
static void WINAPI MyInterrupt(ULONGLONG* value) { const State* s = Now; *value = (ULONGLONG)(s->interrupt + Elapsed(s) * 1e7); }

typedef struct { const char* module; const char* name; void* replacement; } Hook;
static const Hook Hooks[] = {
    { "ntdll.dll", "RtlQueryPerformanceCounter", (void*)MyQpc },
    { "kernel32.dll", "QueryPerformanceCounter", (void*)MyQpc },
    { "kernelbase.dll", "QueryPerformanceCounter", (void*)MyQpc },
    { "kernel32.dll", "GetSystemTimePreciseAsFileTime", (void*)MyFileTime },
    { "kernelbase.dll", "GetSystemTimePreciseAsFileTime", (void*)MyFileTime },
    { "kernel32.dll", "GetSystemTimeAsFileTime", (void*)MyFileTime },
    { "kernelbase.dll", "GetSystemTimeAsFileTime", (void*)MyFileTime },
    { "kernel32.dll", "GetTickCount64", (void*)MyTick64 },
    { "kernelbase.dll", "GetTickCount64", (void*)MyTick64 },
    { "kernel32.dll", "GetTickCount", (void*)MyTick },
    { "kernelbase.dll", "GetTickCount", (void*)MyTick },
    { "kernel32.dll", "QueryUnbiasedInterruptTime", (void*)MyUnbiased },
    { "kernelbase.dll", "QueryUnbiasedInterruptTime", (void*)MyUnbiased },
    { "kernelbase.dll", "QueryUnbiasedInterruptTimePrecise", (void*)MyUnbiasedPrecise },
    { "kernelbase.dll", "QueryInterruptTime", (void*)MyInterrupt },
    { "kernelbase.dll", "QueryInterruptTimePrecise", (void*)MyInterrupt },
};
#define HOOKS (sizeof(Hooks) / sizeof(Hooks[0]))
static void* Patched[HOOKS];
static char Report[4096];

// A page within 2 GB of the target, for the 14-byte absolute jump the 5-byte relative one lands on.
static BYTE* Near(BYTE* target)
{
    SYSTEM_INFO info; GetSystemInfo(&info);
    ULONG_PTR step = info.dwAllocationGranularity;
    ULONG_PTR base = ((ULONG_PTR)target & ~(step - 1));
    for (ULONG_PTR distance = step; distance < 0x70000000; distance += step)
    {
        BYTE* page = (BYTE*)VirtualAlloc((void*)(base - distance), 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (page) return page;
        page = (BYTE*)VirtualAlloc((void*)(base + distance), 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (page) return page;
    }
    return NULL;
}

static int Install(void)
{
    int done = 0;
    for (size_t i = 0; i < HOOKS; i++)
    {
        HMODULE module = GetModuleHandleA(Hooks[i].module);
        BYTE* target = module ? (BYTE*)GetProcAddress(module, Hooks[i].name) : NULL;
        size_t length = strlen(Report);
        if (!target) { _snprintf(Report + length, sizeof(Report) - length, "absent %s!%s\n", Hooks[i].module, Hooks[i].name); continue; }
        int seen = 0;
        for (size_t j = 0; j < i; j++) if (Patched[j] == target) seen = 1;
        if (seen) { _snprintf(Report + length, sizeof(Report) - length, "same %s!%s\n", Hooks[i].module, Hooks[i].name); continue; }
        BYTE* stub = Near(target);
        if (!stub) { _snprintf(Report + length, sizeof(Report) - length, "nostub %s!%s\n", Hooks[i].module, Hooks[i].name); continue; }
        stub[0] = 0xFF; stub[1] = 0x25; *(DWORD*)(stub + 2) = 0; *(void**)(stub + 6) = Hooks[i].replacement;
        DWORD old;
        if (!VirtualProtect(target, 5, PAGE_EXECUTE_READWRITE, &old)) { _snprintf(Report + length, sizeof(Report) - length, "noprotect %s!%s\n", Hooks[i].module, Hooks[i].name); continue; }
        BYTE jump[5]; jump[0] = 0xE9; *(LONG*)(jump + 1) = (LONG)(stub - (target + 5));
        memcpy(target, jump, 5);
        VirtualProtect(target, 5, old, &old);
        FlushInstructionCache(GetCurrentProcess(), target, 5);
        Patched[i] = target; done++;
        _snprintf(Report + length, sizeof(Report) - length, "hooked %s!%s\n", Hooks[i].module, Hooks[i].name);
    }
    return done;
}

// The clocks as they read now (each through its own function, hooked or not yet), under a new factor.
static State* Read(double factor)
{
    State* s = (State*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(State));
    s->factor = factor;
    LARGE_INTEGER c, f; RealCounter(&c, &f); s->real = c.QuadPart;
    LARGE_INTEGER q; QueryPerformanceCounter(&q); s->qpc = (double)q.QuadPart;
    FILETIME ft; GetSystemTimePreciseAsFileTime(&ft); s->fileTime = (double)(((ULONGLONG)ft.dwHighDateTime << 32) | ft.dwLowDateTime);
    s->tick = (double)GetTickCount64();
    ULONGLONG u; QueryUnbiasedInterruptTime(&u); s->unbiased = (double)u; s->interrupt = (double)u;
    return s;
}

static int Ready;
static LONGLONG WallReal; static double WallFile;
// factor > 0: the clocks from now on, dilated by it. Returns the number of functions hooked (on the first call).
__declspec(dllexport) int MiraDilate(double factor)
{
    if (factor <= 0) factor = 1;
    if (!Ready)
    {
        RealCounter = (NtQpc)GetProcAddress(GetModuleHandleA("ntdll.dll"), "NtQueryPerformanceCounter");
        if (!RealCounter) return -1;
        LARGE_INTEGER c, f; RealCounter(&c, &f); RealFrequency = (double)f.QuadPart;
        LARGE_INTEGER q; QueryPerformanceFrequency(&q); QpcFrequency = (double)q.QuadPart;
        Now = Read(1); WallReal = Now->real; WallFile = Now->fileTime;
        Ready = Install();
        if (!Ready) return 0;
    }
    State* next = Read(factor);
    InterlockedExchangePointer((PVOID volatile*)&Now, next);
    return Ready;
}
// The wall clock, never dilated: 100 ns since 1601, as a FILETIME.
__declspec(dllexport) LONGLONG MiraRealFileTime(void)
{
    LARGE_INTEGER c, f; RealCounter(&c, &f);
    return (LONGLONG)(WallFile + (double)(c.QuadPart - WallReal) / RealFrequency * 1e7);
}
__declspec(dllexport) const char* MiraDilateReport(void) { return Report; }
