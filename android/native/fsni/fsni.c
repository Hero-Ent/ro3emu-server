// libfsni.so: the file API HappyEngine.FileStreamNI (Happyengine.Base.Runtime.dll) P/Invokes, rebuilt on POSIX stdio
// for Android. Only the semantics its managed callers rely on:
//   fsni_startup(internal, external)  internal = Application.streamingAssetsPath + "/", external = persistentDataPath + "/"
//   fsni_open(path, mode)             mode bit 0: read-write, creating the file (JobWriteThread writes blocks at offsets,
//                                     so an existing file is not truncated); bit 16 ("security") is ignored;
//                                     a relative path is taken under the internal root
//   fsni_seek(fp, off, origin)        origin as in SEEK_SET/CUR/END; returns the new position
//   fsni_mmap(fp, off, size, 1, 1)    read-only shared mapping, released by fsni_munmap(ptr, size)
//   fsni_get_last_error(buf, size)    0 and the message of the last failure on this thread
// Every failure returns -1 / NULL and records errno's message.
#include <errno.h>
#include <fcntl.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#include <android/log.h>

#define EXPORT __attribute__((visibility("default")))
#define TAG "fsni"

typedef struct { FILE* f; int fd; } fsni_file;

static char g_internal[1024], g_external[1024];
static __thread char t_error[256];

static void fail(const char* what, const char* path)
{
    snprintf(t_error, sizeof t_error, "%s %s: %s", what, path ? path : "", strerror(errno));
    __android_log_print(ANDROID_LOG_WARN, TAG, "%s", t_error);
}

static void copy_root(char* dst, const char* src)
{
    snprintf(dst, sizeof g_internal, "%s", src ? src : "");
    size_t n = strlen(dst);
    if (n && dst[n - 1] != '/' && n + 1 < sizeof g_internal) { dst[n] = '/'; dst[n + 1] = 0; }
}

static const char* resolve(const char* path, char* buf, size_t size)
{
    if (!path) return "";
    if (path[0] == '/') return path;
    snprintf(buf, size, "%s%s", g_internal, path);
    return buf;
}

EXPORT int fsni_startup(const char* internal_path, const char* external_path)
{
    copy_root(g_internal, internal_path);
    copy_root(g_external, external_path);
    __android_log_print(ANDROID_LOG_INFO, TAG, "startup internal=%s external=%s", g_internal, g_external);
    if (strncmp(g_internal, "jar:", 4) == 0)
        __android_log_print(ANDROID_LOG_ERROR, TAG, "internal root is inside the APK; the RO3Emu port expects StreamingAssets redirected to external storage");
    return 0;
}

EXPORT void fsni_cleanup(void) {}

static void make_parent_dirs(const char* path)
{
    char tmp[1024];
    snprintf(tmp, sizeof tmp, "%s", path);
    for (char* p = tmp + 1; *p; p++)
        if (*p == '/') { *p = 0; mkdir(tmp, 0775); *p = '/'; }
}

EXPORT void* fsni_open(const char* name, int mode)
{
    char buf[1024];
    const char* path = resolve(name, buf, sizeof buf);
    int fd;
    if (mode & 1) { make_parent_dirs(path); fd = open(path, O_RDWR | O_CREAT, 0664); }
    else fd = open(path, O_RDONLY);
    if (fd < 0) { fail("open", path); return NULL; }
    FILE* f = fdopen(fd, (mode & 1) ? "r+b" : "rb");
    if (!f) { fail("fdopen", path); close(fd); return NULL; }
    fsni_file* h = (fsni_file*)malloc(sizeof *h);
    h->f = f; h->fd = fd;
    return h;
}

EXPORT int fsni_read(void* fp, void* dst, int len)
{
    fsni_file* h = (fsni_file*)fp;
    if (!h || len < 0) { errno = EINVAL; fail("read", NULL); return -1; }
    size_t n = fread(dst, 1, (size_t)len, h->f);
    if (n < (size_t)len && ferror(h->f)) { fail("read", NULL); clearerr(h->f); return -1; }
    return (int)n;
}

EXPORT int fsni_write(void* fp, const void* src, int len)
{
    fsni_file* h = (fsni_file*)fp;
    if (!h || len < 0) { errno = EINVAL; fail("write", NULL); return -1; }
    size_t n = fwrite(src, 1, (size_t)len, h->f);
    if (n < (size_t)len) { fail("write", NULL); return -1; }
    return (int)n;
}

EXPORT int fsni_seek(void* fp, int offset, int origin)
{
    fsni_file* h = (fsni_file*)fp;
    if (!h) { errno = EINVAL; fail("seek", NULL); return -1; }
    if (fseeko(h->f, offset, origin) != 0) { fail("seek", NULL); return -1; }
    return (int)ftello(h->f);
}

EXPORT int fsni_close(void* fp)
{
    fsni_file* h = (fsni_file*)fp;
    if (!h) return 0;
    int r = fclose(h->f);
    free(h);
    if (r != 0) { fail("close", NULL); return -1; }
    return 0;
}

EXPORT int fsni_getsize(void* fp)
{
    fsni_file* h = (fsni_file*)fp;
    struct stat st;
    if (!h) { errno = EINVAL; fail("getsize", NULL); return -1; }
    fflush(h->f);
    if (fstat(h->fd, &st) != 0) { fail("fstat", NULL); return -1; }
    return (int)st.st_size;
}

EXPORT int fsni_remove(const char* name)
{
    char buf[1024];
    const char* path = resolve(name, buf, sizeof buf);
    if (unlink(path) != 0) { fail("remove", path); return -1; }
    return 0;
}

EXPORT int fsni_rename(const char* from, const char* to)
{
    char a[1024], b[1024];
    const char* pa = resolve(from, a, sizeof a);
    const char* pb = resolve(to, b, sizeof b);
    make_parent_dirs(pb);
    if (rename(pa, pb) != 0) { fail("rename", pa); return -1; }
    return 0;
}

EXPORT int fsni_exists(const char* name, int flags)
{
    (void)flags;
    char buf[1024];
    struct stat st;
    return stat(resolve(name, buf, sizeof buf), &st) == 0;
}

EXPORT void* fsni_alloc(int size) { return size > 0 ? malloc((size_t)size) : NULL; }
EXPORT void fsni_free(void* p) { free(p); }

// The managed side marshals strings as ANSI (UTF-8 on Android); the copies are freed with fsni_free.
EXPORT int fsni_strlen(const char* s) { return s ? (int)strlen(s) : 0; }

EXPORT char* fsni_strdup(const char* s, int* len)
{
    size_t n = s ? strlen(s) : 0;
    char* r = (char*)malloc(n + 1);
    if (n) memcpy(r, s, n);
    r[n] = 0;
    if (len) *len = (int)n;
    return r;
}

EXPORT char* fsni_strndup(const char* s, int len)
{
    size_t n = s ? strnlen(s, len > 0 ? (size_t)len : 0) : 0;
    char* r = (char*)malloc(n + 1);
    if (n) memcpy(r, s, n);
    r[n] = 0;
    return r;
}

EXPORT void* fsni_memdup(const void* s, int len)
{
    if (!s || len <= 0) return NULL;
    void* r = malloc((size_t)len);
    memcpy(r, s, (size_t)len);
    return r;
}

EXPORT void* fsni_mmap(void* fp, long long offset, int size, int prot, int flags)
{
    (void)prot; (void)flags;
    fsni_file* h = (fsni_file*)fp;
    if (!h || size <= 0) { errno = EINVAL; fail("mmap", NULL); return NULL; }
    void* p = mmap(NULL, (size_t)size, PROT_READ, MAP_SHARED, h->fd, (off_t)offset);
    if (p == MAP_FAILED) { fail("mmap", NULL); return NULL; }
    return p;
}

EXPORT int fsni_munmap(void* p, int size)
{
    if (munmap(p, (size_t)size) != 0) { fail("munmap", NULL); return -1; }
    return 0;
}

EXPORT int fsni_get_last_error(char* out, int size)
{
    if (!out || size <= 0) return EINVAL;
    snprintf(out, (size_t)size, "%s", t_error);
    return 0;
}
