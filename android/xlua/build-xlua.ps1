# Builds libxlua.so (arm64-v8a) the way the client's xlua.dll was built: xLua + Lua 5.4.1 + lua-protobuf, lpeg,
# lua-rapidjson, luasocket, i64lib (chexiongsheng/build_xlua_with_libs) + lua-cmsgpack, with three RO3 changes:
#   - luaL_loadbufferx decodes the client's obfuscated chunks (ro3_chunk.c) before lua_load;
#   - Lpb_clearCache(L), which the client P/Invokes, clears lua-protobuf's name cache;
#   - luaopen_cmsgpack / luaopen_cmsgpack_safe are linked in, and the Lua allocation-profiling hooks are no-ops.
# Sources are cloned into android\build\third_party (gitignored); the result goes to android\build\native-prebuilt,
# where setup-project.ps1 picks it up.
param([string]$Unity = 'C:\Dev\Unity\Editors\2022.3.62f3\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$android = Split-Path $PSScriptRoot -Parent
$third = Join-Path $android 'build\third_party'
$ndk = Join-Path (Split-Path $Unity) 'Data\PlaybackEngines\AndroidPlayer\NDK'
$make = Join-Path $ndk 'prebuilt\windows-x86_64\bin\make.exe'
if (-not (Test-Path $make)) { throw "NDK not found at $ndk" }

New-Item -ItemType Directory -Force $third | Out-Null
foreach ($repo in @(
    @{ Name = 'build_xlua_with_libs'; Url = 'https://github.com/chexiongsheng/build_xlua_with_libs.git'; Commit = '88ac2f6b608c208f31b357be4c59143bc068df34' },
    @{ Name = 'lua-cmsgpack'; Url = 'https://github.com/antirez/lua-cmsgpack.git'; Commit = $null })) {
  $dir = Join-Path $third $repo.Name
  if (-not (Test-Path $dir)) { git clone --depth 1 $repo.Url $dir; if ($LASTEXITCODE) { throw "clone $($repo.Name) failed" } }
  if ($repo.Commit) {
    $head = git -C $dir rev-parse HEAD
    if ($head -ne $repo.Commit) { Write-Warning "$($repo.Name) is at $head, tested with $($repo.Commit)" }
  }
}

$src = Join-Path $android 'build\xlua-src'
if (Test-Path $src) { Remove-Item -Recurse -Force $src }
Copy-Item -Recurse (Join-Path $third 'build_xlua_with_libs\build') $src
New-Item -ItemType Directory -Force (Join-Path $src 'cmsgpack') | Out-Null
Copy-Item (Join-Path $third 'lua-cmsgpack\lua_cmsgpack.c') (Join-Path $src 'cmsgpack')
Copy-Item (Join-Path $PSScriptRoot 'ro3_chunk.c'), (Join-Path $PSScriptRoot 'ro3_stubs.c') $src

function Patch([string]$file, [string]$old, [string]$new) {
  $text = [IO.File]::ReadAllText($file)
  if (-not $text.Contains($old)) { throw "patch target not found in $file" }
  [IO.File]::WriteAllText($file, $text.Replace($old, $new))
}

$lauxlib = Join-Path $src 'lua-5.4.1\src\lauxlib.c'
$text = [IO.File]::ReadAllText($lauxlib) -replace "`r`n", "`n"
[IO.File]::WriteAllText($lauxlib, $text)
Patch $lauxlib @"
LUALIB_API int luaL_loadbufferx (lua_State *L, const char *buff, size_t size,
                                 const char *name, const char *mode) {
  LoadS ls;
  ls.s = buff;
  ls.size = size;
  return lua_load(L, getS, &ls, name, mode);
}
"@ @"
unsigned char *ro3_decode_chunk(const char *buff, size_t size);

LUALIB_API int luaL_loadbufferx (lua_State *L, const char *buff, size_t size,
                                 const char *name, const char *mode) {
  LoadS ls;
  int status;
  /* RO3: an obfuscated client chunk is decoded into a stock 5.4 copy first */
  unsigned char *decoded = ro3_decode_chunk(buff, size);
  ls.s = decoded ? (const char *)decoded : buff;
  ls.size = size;
  status = lua_load(L, getS, &ls, name, mode);
  free(decoded);
  return status;
}
"@

$pb = Join-Path $src 'lua-protobuf\pb.c'
Add-Content -Path $pb -Encoding ascii -Value @"

/* RO3: the client P/Invokes Lpb_clearCache(L) to drop lua-protobuf's cached type names. */
LUALIB_API void Lpb_clearCache(lua_State *L) {
    lpb_State *LS = lpb_lstate(L);
    memset(&LS->cache, 0, sizeof(LS->cache));
}
"@

# RO3: the client's lua-protobuf is a fork with extra entry points its Lua calls (Network/NW_NetworkManager,
# NW_ServerClientHandler, Global, Quality). encode_unsafe returns nil, which sends the game down its own pb.encode path;
# decode_unsafe decodes the bytes C# hands over as (pointer, length) - every received message goes through it;
# decode_raw decodes like decode; the buffer and cache helpers do nothing.
$pbText = [IO.File]::ReadAllText($pb) -replace "`r`n", "`n"
[IO.File]::WriteAllText($pb, $pbText)
Patch $pb 'LUALIB_API int luaopen_pb(lua_State *L) {' @"
static int Ro3pb_nil(lua_State *L) { lua_pushnil(L); return 1; }
static int Ro3pb_none(lua_State *L) { (void)L; return 0; }

static int Ro3pb_decode_unsafe(lua_State *L) {
    const char *p = lua_islightuserdata(L, 2) ? (const char *)lua_touserdata(L, 2)
                                              : (const char *)(intptr_t)luaL_checkinteger(L, 2);
    lua_Integer n = luaL_checkinteger(L, 3);
    lua_settop(L, 1);
    if (p == NULL || n <= 0) lua_pushliteral(L, "");
    else lua_pushlstring(L, p, (size_t)n);
    return Lpb_decode(L);
}

static void ro3_pb_extras(lua_State *L) {
    luaL_Reg extras[] = {
        { "encode_unsafe", Ro3pb_nil },
        { "decode_unsafe", Ro3pb_decode_unsafe },
        { "decode_raw", Lpb_decode },
        { "resetbuffer_unsafe", Ro3pb_none },
        { "resetbuffer", Ro3pb_none },
        { "clearCache", Ro3pb_none },
        { "turn", Ro3pb_none },
        { "cache", Ro3pb_none },
        { "profiler", Ro3pb_none },
        { NULL, NULL }
    };
    luaL_setfuncs(L, extras, 0);
}

LUALIB_API int luaopen_pb(lua_State *L) {
"@
Patch $pb @"
        lua_setfield(L, -2, "__index");
    }
    luaL_newlib(L, libs);
    return 1;
"@ @"
        lua_setfield(L, -2, "__index");
    }
    luaL_newlib(L, libs);
    ro3_pb_extras(L);
    return 1;
"@

# RO3: lua-cmsgpack predates MessagePack's bin and ext families, which the server (MessagePack-CSharp) sends for byte[]
# and extension values in Micro RPC replies ("Bad data format in input"). bin decodes as a Lua string like raw, ext as
# the string of its data (type byte dropped).
$msgpack = Join-Path $src 'cmsgpack\lua_cmsgpack.c'
$mpText = [IO.File]::ReadAllText($msgpack) -replace "`r`n", "`n"
[IO.File]::WriteAllText($msgpack, $mpText)
Patch $msgpack '    case 0xd9:  /* raw 8 */' @"
    case 0xc4:  /* bin 8 */
        mp_cur_need(c,2);
        {
            size_t l = c->p[1];
            mp_cur_need(c,2+l);
            lua_pushlstring(L,(char*)c->p+2,l);
            mp_cur_consume(c,2+l);
        }
        break;
    case 0xc5:  /* bin 16 */
        mp_cur_need(c,3);
        {
            size_t l = (c->p[1] << 8) | c->p[2];
            mp_cur_need(c,3+l);
            lua_pushlstring(L,(char*)c->p+3,l);
            mp_cur_consume(c,3+l);
        }
        break;
    case 0xc6:  /* bin 32 */
        mp_cur_need(c,5);
        {
            size_t l = ((size_t)c->p[1] << 24) | ((size_t)c->p[2] << 16) | ((size_t)c->p[3] << 8) | (size_t)c->p[4];
            mp_cur_need(c,5+l);
            lua_pushlstring(L,(char*)c->p+5,l);
            mp_cur_consume(c,5+l);
        }
        break;
    case 0xd4: case 0xd5: case 0xd6: case 0xd7: case 0xd8:  /* fixext 1/2/4/8/16 */
        {
            size_t l = (size_t)1 << (c->p[0] - 0xd4);
            mp_cur_need(c,2+l);
            lua_pushlstring(L,(char*)c->p+2,l);
            mp_cur_consume(c,2+l);
        }
        break;
    case 0xc7:  /* ext 8 */
        mp_cur_need(c,3);
        {
            size_t l = c->p[1];
            mp_cur_need(c,3+l);
            lua_pushlstring(L,(char*)c->p+3,l);
            mp_cur_consume(c,3+l);
        }
        break;
    case 0xc8:  /* ext 16 */
        mp_cur_need(c,4);
        {
            size_t l = (c->p[1] << 8) | c->p[2];
            mp_cur_need(c,4+l);
            lua_pushlstring(L,(char*)c->p+4,l);
            mp_cur_consume(c,4+l);
        }
        break;
    case 0xc9:  /* ext 32 */
        mp_cur_need(c,6);
        {
            size_t l = ((size_t)c->p[1] << 24) | ((size_t)c->p[2] << 16) | ((size_t)c->p[3] << 8) | (size_t)c->p[4];
            mp_cur_need(c,6+l);
            lua_pushlstring(L,(char*)c->p+6,l);
            mp_cur_consume(c,6+l);
        }
        break;
    case 0xd9:  /* raw 8 */
"@

$cmake = Join-Path $src 'CMakeLists.txt'
$text = [IO.File]::ReadAllText($cmake) -replace "`r`n", "`n"
[IO.File]::WriteAllText($cmake, $text)
# The MSVC /MT block runs before project(), so ANDROID is not set yet on a Windows host; ANDROID_ABI (ours) is.
Patch $cmake 'AND NOT ANDROID)' 'AND NOT ANDROID AND NOT DEFINED ANDROID_ABI)'
Patch $cmake 'set (THIRDPART_SRC ${THIRDPART_SRC} ${LPEG_SRC})' @'
set (THIRDPART_SRC ${THIRDPART_SRC} ${LPEG_SRC})

# RO3: lua-cmsgpack and the client chunk decoder
set (THIRDPART_SRC ${THIRDPART_SRC} cmsgpack/lua_cmsgpack.c ro3_chunk.c ro3_stubs.c)
'@

# cmake writes warnings to stderr, which Windows PowerShell turns into terminating errors under 'Stop'.
$ErrorActionPreference = 'Continue'
$out = Join-Path $src 'build.android.arm64-v8a'
# Quoted: Windows PowerShell splits a bare -DLUA_VERSION=5.4.1 into "-DLUA_VERSION=5" and ".4.1".
& cmake -Wno-dev -G 'Unix Makefiles' "-DCMAKE_MAKE_PROGRAM=$make" "-H$src" "-B$out" '-DLUA_VERSION=5.4.1' '-DANDROID_ABI=arm64-v8a' `
  '-DANDROID_PLATFORM=android-26' "-DCMAKE_TOOLCHAIN_FILE=$ndk\build\cmake\android.toolchain.cmake" '-DCMAKE_BUILD_TYPE=Release' 2>&1 | Out-Host
if ($LASTEXITCODE) { throw 'cmake configure failed' }
& cmake --build $out --config Release -- -j8 2>&1 | Out-Host
if ($LASTEXITCODE) { throw 'cmake build failed' }
$ErrorActionPreference = 'Stop'

$prebuilt = Join-Path $android 'build\native-prebuilt'
New-Item -ItemType Directory -Force $prebuilt | Out-Null
Copy-Item -Force (Join-Path $out 'libxlua.so') $prebuilt
Write-Host "built $prebuilt\libxlua.so"
