// Lua allocation-profiling hooks the client's xlua.dll exports and Assembly-CSharp P/Invokes (Obf_TF): each one
// registers a callback (or drops them all). The port does not profile Lua memory, so they accept and ignore it.
#define EXPORT __attribute__((visibility("default")))

EXPORT void lua_logalloctab(void *callback) { (void)callback; }
EXPORT void lua_logrehashtab(void *callback) { (void)callback; }
EXPORT void lua_logallocstring(void *callback) { (void)callback; }
EXPORT void lua_logfreestring(void *callback) { (void)callback; }
EXPORT void lua_releaseAllocCb(void) {}
