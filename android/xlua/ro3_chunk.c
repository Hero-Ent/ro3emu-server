// Turns an RO3 client Lua chunk (Recovery/LuaPayload/*.lua.bytes) into stock Lua 5.4 bytecode, in a copy, so the stock
// lua-5.4.1 VM of xLua runs it. Port of src/Ro3Emu.DataExport/LuaChunkDecoder.cs (the format is documented there):
//   - signature 0x1E instead of ESC;
//   - every instruction XORed with (sizecode << 16 | sizecode), opcodes permuted (OpMap: client -> stock);
//   - every string payload chained: s[k] ^= enc[k-1] for k = n-1..1, then s[0] ^= (byte)n.
// Hooked into luaL_loadbufferx (build-xlua.ps1 patches lauxlib.c), so the payload files, which RO3.ClientSecurity
// checks against their RSA signatures, stay byte-for-byte the client's.
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

typedef struct { unsigned char *d; size_t n, p; int bad; } Chunk;

static unsigned char opmap[128];

static void build_opmap(void)
{
    int i;
    for (i = 0; i < 128; i++) opmap[i] = (unsigned char)i;
    for (i = 1; i <= 20; i++) opmap[i] = (unsigned char)(33 + i);  // ADD..CONCAT     -> 34..53
    for (i = 21; i <= 53; i++) opmap[i] = (unsigned char)(i - 20); // LOADI..SHLI     -> 1..33
    opmap[83] = 12;                                               // a second GETTABLE
}

static int need(Chunk *c, size_t k) { if (c->p + k > c->n) { c->bad = 1; return 0; } return 1; }

static uint64_t varint(Chunk *c)
{
    uint64_t x = 0;
    unsigned char b;
    do {
        if (!need(c, 1)) return 0;
        b = c->d[c->p++];
        x = (x << 7) | (b & 0x7F);
    } while ((b & 0x80) == 0);
    return x;
}

static void str(Chunk *c)
{
    size_t size = (size_t)varint(c), n, k;
    if (c->bad || size == 0) return;
    n = size - 1;
    if (!need(c, n)) return;
    for (k = n - 1; n > 1 && k >= 1; k--) c->d[c->p + k] ^= c->d[c->p + k - 1];
    if (n > 0) c->d[c->p] ^= (unsigned char)n;
    c->p += n;
}

static void skip(Chunk *c, size_t k) { if (need(c, k)) c->p += k; }

static void function(Chunk *c, int depth)
{
    uint64_t i, count;
    uint32_t sizecode, key;
    if (depth > 200) { c->bad = 1; return; }
    str(c);                 // source
    varint(c);              // linedefined
    varint(c);              // lastlinedefined
    skip(c, 3);             // numparams, is_vararg, maxstacksize
    sizecode = (uint32_t)varint(c);
    key = (sizecode << 16) | sizecode;
    if (!need(c, (size_t)sizecode * 4)) return;
    for (i = 0; i < sizecode; i++, c->p += 4) {
        uint32_t w;
        memcpy(&w, c->d + c->p, 4);
        w ^= key;
        if ((w & 0x7F) > 83) { c->bad = 1; return; }
        w = (w & ~0x7Fu) | opmap[w & 0x7F];
        memcpy(c->d + c->p, &w, 4);
    }
    count = varint(c);      // constants
    for (i = 0; i < count && !c->bad; i++) {
        unsigned char tag;
        if (!need(c, 1)) return;
        tag = c->d[c->p++];
        switch (tag) {
        case 0x00: case 0x01: case 0x11: break;
        case 0x03: case 0x13: skip(c, 8); break;
        case 0x04: case 0x14: str(c); break;
        default: c->bad = 1; return;
        }
    }
    count = varint(c);      // upvalues
    skip(c, (size_t)(3 * count));
    count = varint(c);      // protos
    for (i = 0; i < count && !c->bad; i++) function(c, depth + 1);
    count = varint(c);      // lineinfo
    skip(c, (size_t)count);
    count = varint(c);      // abslineinfo
    for (i = 0; i < count && !c->bad; i++) { varint(c); varint(c); }
    count = varint(c);      // locvars
    for (i = 0; i < count && !c->bad; i++) { str(c); varint(c); varint(c); }
    count = varint(c);      // upvalue names
    for (i = 0; i < count && !c->bad; i++) str(c);
}

// Returns a malloc'ed stock copy of an RO3 chunk (the caller frees it), or NULL when buff is not one / is malformed.
unsigned char *ro3_decode_chunk(const char *buff, size_t size)
{
    Chunk c;
    if (size < 33 || (unsigned char)buff[0] != 0x1E || (unsigned char)buff[4] != 0x54) return NULL;
    if (opmap[1] == 0) build_opmap();
    c.d = (unsigned char *)malloc(size);
    if (!c.d) return NULL;
    memcpy(c.d, buff, size);
    c.n = size;
    c.bad = 0;
    c.d[0] = 0x1B;
    c.p = 31 + 1;           // header, then the main closure's sizeupvalues
    function(&c, 0);
    if (c.bad || c.p != c.n) { free(c.d); return NULL; }
    return c.d;
}
