// Content Warning iOS native helpers, called from C# through DllImport("__Internal").
//
//  * CWCrash_*  a fatal signal appends "FATAL SIGNAL" + a native backtrace to Documents/ContentWarning-iOS.log.
//  * CWVe_*     the game's video encoder library (PlayEveryWare "libve", Windows-only) for iOS. The patcher points
//               NativeVeMethods at these. For now: the CPU check passes and encoding reports "not available";
//               the AVFoundation encoder replaces these stubs later.
#import <Foundation/Foundation.h>
#include <signal.h>
#include <execinfo.h>
#include <fcntl.h>
#include <unistd.h>
#include <string.h>
#include <stdint.h>

// ======================================================================================================
// Crash logging: the previous handler (Unity's crash reporter / the default) still runs afterwards.
// ======================================================================================================
static char gCrashLogPath[1024];
static struct sigaction gPrevHandlers[NSIG];
static volatile sig_atomic_t gInCrash;

static void CWWrite(int fd, const char *s) { if (s) write(fd, s, strlen(s)); }

static void CWCrashHandler(int sig, siginfo_t *info, void *ctx)
{
    if (!gInCrash)
    {
        gInCrash = 1;
        int fd = open(gCrashLogPath, O_WRONLY | O_APPEND | O_CREAT, 0644);
        if (fd >= 0)
        {
            char buf[128];
            snprintf(buf, sizeof(buf), "\n=== FATAL SIGNAL %d (%s) at address %p ===\nnative backtrace:\n", sig, sys_signame[sig], info ? info->si_addr : NULL);
            CWWrite(fd, buf);
            void *frames[64];
            int n = backtrace(frames, 64);
            backtrace_symbols_fd(frames, n, fd);
            CWWrite(fd, "=== end of crash report ===\n");
            close(fd);
        }
    }
    struct sigaction *prev = &gPrevHandlers[sig];
    if (prev->sa_flags & SA_SIGINFO) { if (prev->sa_sigaction) { prev->sa_sigaction(sig, info, ctx); return; } }
    else if (prev->sa_handler != SIG_DFL && prev->sa_handler != SIG_IGN && prev->sa_handler != NULL) { prev->sa_handler(sig); return; }
    signal(sig, SIG_DFL);
    raise(sig);
}

extern "C" void CWCrash_Install(const char *logPath)
{
    if (logPath == NULL) return;
    strlcpy(gCrashLogPath, logPath, sizeof(gCrashLogPath));
    int sigs[] = { SIGSEGV, SIGBUS, SIGILL, SIGFPE, SIGABRT, SIGTRAP };
    for (int s : sigs)
    {
        struct sigaction sa;
        memset(&sa, 0, sizeof(sa));
        sa.sa_sigaction = CWCrashHandler;
        sa.sa_flags = SA_SIGINFO | SA_ONSTACK;
        sigemptyset(&sa.sa_mask);
        sigaction(s, &sa, &gPrevHandlers[s]);
    }
    NSLog(@"[CW-iOS] crash logger installed -> %s", logPath);
}

// ======================================================================================================
// libve (PlayEveryWare.VideoEncoding.NativeVeMethods) - stubs until the AVFoundation encoder lands.
// C# bools are marshalled as one byte (UnmanagedType.I1).
// ======================================================================================================
extern "C" bool CWVe_initialize(void *mountPointBuffer, uint32_t bufferSizeBytes) { return true; }
extern "C" bool CWVe_cpu_has_required_simd_extensions() { return true; }
extern "C" bool CWVe_initialize_encoding(void *config, void **handle) { if (handle) *handle = NULL; return false; }
extern "C" bool CWVe_encode_video_frame_from_memory(void *handle, void *buffer, uint32_t rows, uint32_t rowBytes, int32_t flags) { return false; }
extern "C" bool CWVe_encode_mono_audio_sample(void *handle, void *samples, uint32_t count) { return false; }
extern "C" bool CWVe_encode_stereo_audio_sample(void *handle, void *samples, uint32_t count) { return false; }
extern "C" bool CWVe_finalize(void *handle) { return false; }
extern "C" bool CWVe_try_force_release(void *handle) { return true; }
extern "C" bool CWVe_initialize_decoding(void *config, void *handle) { return false; }
extern "C" bool CWVe_combine_webm_files(void *handle, void *array, int32_t length) { return false; }
