# Port Android nativo (experimental)

Objetivo: um APK ARM64 nativo do cliente RO3 que faça login no RO3Emu e permita o básico (andar, atacar, usar
skills). Não precisa ficar perfeito: vídeo, voz e partes do áudio podem ficar desligados no começo.

Nada aqui inclui arquivos do jogo. O script monta o projeto Unity a partir do **seu** cliente PC (`Client\Ragnarok3`).
As DLLs copiadas e o projeto gerado ficam fora do git (veja `.gitignore`).

## Como funciona

O cliente PC é um build Unity **2022.3.62f3** com backend Mono. O código do jogo (≈150 DLLs .NET, ofuscadas
com `Obf_*`) já tem caminhos de mobile (`Joystick`, `TouchPad`, `TouchControls`, `AndroidMediaPlayerAdapter`,
canal `android_google`). O port:

1. cria um projeto Unity vazio da mesma versão;
2. coloca as DLLs do jogo (já patcheadas pro seu servidor) como *precompiled plugins* em `Assets/Plugins/Game`;
3. compila tudo com **IL2CPP ARM64** (o Mono no Android só roda ARMv7);
4. troca as libs nativas de Windows (`ro3_Data\Plugins\x86_64`) por versões ARM64 ou stubs;
5. resolve os assets (o VFS de 24 GB, AssetBundles Unity dentro do container `HPY`), que foram feitos pra Windows.

## Marcos

| # | Marco | Pronto quando | Status |
|---|-------|---------------|--------|
| M0 | Ferramentas | Unity 2022.3.62f3 + Android Build Support (SDK/NDK/JDK) instalados | ok (`C:\Dev\Unity\Editors`) |
| M1 | Casca IL2CPP | `build-apk.ps1` gera um APK que abre e loga no logcat os assemblies do jogo carregados | **ok** (Galaxy S24, Vulkan) |
| M2 | Libs nativas | `libfsni.so`, `libxlua.so` ARM64; o resto em stubs | **ok** (decoder Lua validado nos 13.488 chunks; Wwise, Tencent, AVPro… em stub) |
| M3 | Boot do jogo | `Ro3Startup` roda e carrega o prefab do launcher do VFS | **ok** (GameMain, managers, Lua `Launch`) |
| M4 | Assets | AssetBundles carregam no Android; shaders traduzidos (DXBC → GLES3/Vulkan) | bundles ok (retarget em runtime); shaders parciais (built-in, TMP, URP Utils; resto no fallback) |
| M5 | Login | tela de login → servidor RO3Emu → seleção de personagem | **login ok** (2026-10-21: conta criada, MicroGate cifrado, verifySdk, getRoleInfo); textos da UI ainda invisíveis |
| M6 | Jogável | entrar no mapa, andar, atacar, skill; UI touch do próprio jogo | — |

## Bloqueios conhecidos

- **Shaders (o maior).** O build PC só tem shaders D3D11 (DXBC), e o celular precisa de Vulkan ou GLES3.
  - Mapeado (`BundleTool scan`): o VFS tem 627 bundles Unity (224 MB). Os outros ~22.600 arquivos são formato
    próprio da HappyEngine (`HPY`, `RO.V`). **Todos os 453 shaders estão num bundle só**, `b2/b27aef35c2b67c45.bundle`,
    junto com 4 `ShaderVariantCollection`.
  - Por grupo: `Hidden` 121, URP 33, Spine 27, UI 21, HappyEngine 21, Unlit 15, TextMeshPro 13, Legacy 13,
    Particles 10, Skybox 7, Shader Graphs 7, Mobile 6, Custom 6.
  - Plano: recompilar no projeto Unity os que têm fonte pública (URP 14, built-in 2022.3, TMP, spine-unity) e
    descompilar de DXBC pra HLSL os do jogo (~35–60). Depois, gerar um bundle Android que substitua o
    `b27aef35c2b67c45.bundle` com os mesmos path IDs, pra que os materiais dos outros bundles continuem achando os shaders.
  - A GPU do teste (Xclipse 940) suporta DXT5/BC3 mas não BC7; ASTC sim.
- **Texturas BC/DXT.** A maioria das GPUs Android não suporta. A Unity descompacta na CPU (pesado em RAM).
  Uma solução definitiva seria recomprimir pra ASTC.
- **Plataforma dos bundles.** O Android recusa bundles `StandaloneWindows64` ("File's Build target is: 19").
  - Além dos 627 bundles soltos, os 4.823 packs `RO3V` da raiz do VFS (11,9 GB) guardam bundles Unity concatenados,
    carregados com `AssetBundle.LoadFromFile(pack, 0, offset)`.
  - Por isso a correção é em runtime: o `AndroidPatch` manda toda `AssetBundle.LoadFromFile[Async]` do jogo pro
    `hooks\Bundles.cs`. Ele lê o bundle no arquivo e offset pedidos, e se o alvo não for Android, descompacta (LZ4),
    troca o `m_TargetPlatform` pra 13 e carrega da memória. Bundle que já é Android segue o caminho normal.
  - `tools\BundleTool.cs retarget` ainda gera `build\vfs-android` (os soltos já convertidos, que o `push-data.ps1`
    manda por cima), o que evita o custo em runtime nesses 627.
- **HybridCLR.** `HybridCLRLoadAssemblyWork` chama `LoadOriginalDifferentialHybridAssembly` (DHE), uma *internal call*
  do libil2cpp do HybridCLR. Resolvido: o `AndroidPatch` troca as 2 internal calls por "retorna OK", já que as DLLs
  originais entram compiladas em AOT.

## O que o `setup-project.ps1` faz com as DLLs (além do patch do RO3Emu)

- **Plugins só pra Android.** Carregadas no próprio editor, as DLLs do jogo derrubam a Unity
  (`MonoManager ReloadAssembly`). Por isso o editor não vê os tipos do jogo, e o código do shell usa reflection.
- **Inicializadores.** A Unity registra sozinha, no build, os `[RuntimeInitializeOnLoadMethod]` das DLLs do jogo.
  O `AndroidPatch` tira o atributo dos que não podem rodar no Android: a trava do launcher (`RecoveryLaunchGate`,
  o "exit 70"), os SDKs `HL.Windows` e o autopilot de testes `DeNA.Anjin`.
- **Boot.** O `level0` do PC só tem um `Startup` (com `Ro3Startup`), Camera, Canvas e EventSystem. O `GameInit` cria o
  EventSystem e o `Startup`, e o `Ro3Startup` carrega o `startup_bundle` do VFS e instancia o `Launcher.prefab`.
- **`AndroidPatch.cs`.**
  - `Application.streamingAssetsPath` vira `Ro3Android.Hooks.Paths.StreamingAssets()` (39 chamadas em 9 DLLs),
    apontando pro armazenamento externo.
  - Código inalcançável vira `nop`. A reconstrução deixou um `ret` com a pilha vazia depois de um `br` em
    `VersionNoticeUtil.GetResAsync`: o Mono ignora, o IL2CPP falha com "Stack empty".
  - Os stubs do HybridCLR acima.
- O cliente reconstruído já traz um `RecoveryAndroidRuntimeBootstrap` (não usado: falta o
  `Recovery/recovery-bootstrap.json`).
- **Hooks do RO3Emu.** O `Ro3Emu.ClientHooks.dll` usa `winmm`, `user32` e `kernel32`, que não existem no Android.
  A voz fica desligada.

## Libs nativas (`ro3_Data\Plugins\x86_64`) → Android

| Lib | Usada por | Plano |
|-----|-----------|-------|
| `fsni` (21 funções: open/read/seek/mmap…) | `Happyengine.Base.Runtime` (VFS) | reimplementar em C (pequeno) |
| `xlua` | 132 P/Invokes, Lua do jogo | **feito**: `xlua\build-xlua.ps1` (xLua + Lua 5.4.1 + pb, lpeg, rapidjson, luasocket, cmsgpack); o `luaL_loadbufferx` desofusca os chunks do cliente (`ro3_chunk.c`, porte do `LuaChunkDecoder.cs`) |
| `ikcp` | `Phonest.Platform` (rede) | KCP modificado (`rdc_*`, `recv_ex`); o RO3Emu não usa KCP, então não é chamado |
| `RecastDll`, `cjson`, `ktx` | outras libs nativas | nenhum P/Invoke gerenciado; só se algo pedir |
| `sqlite3` | `HL`, `Mono.Data.Sqlite` | compilar (open source) se for chamado |
| `Cysharp.Net.Http.YetAnotherHttpHandler.Native` | HTTP | tem build Android oficial |
| `AkUnitySoundEngine` (1333 P/Invokes) | Wwise | stub no começo (jogo mudo); depois o SDK Android da mesma versão |
| `AVProVideo`, `libvlc` | vídeos | stub |
| `gmesdk`, `ImSDK` | voz e chat Tencent | stub |
| `CrashSight64`, `httpdnsplugin`, `NetworkDiagnosis`, `windowsplugin` | telemetria e info do aparelho | stub |
| `lib_burst_generated` | Burst | não precisa: sem Burst o código roda gerenciado |

## Instalação (SSD, `C:\Dev`)

O projeto e as ferramentas ficam no SSD desde 22/10/2026 (o HD de 1 TB com D: e E: está com falha prevista pela SMART);
só a VFS do jogo continua no D:, por junction (o build não a lê, ela já está no celular).

- Editor: `C:\Dev\Unity\Editors\2022.3.62f3` (copiado com o SDK, o NDK e o JDK do Android, que ficam dentro da pasta do
  editor). Todos os scripts usam esse caminho por padrão; outro lugar → `-Unity`. No Unity Hub, *Installs* → *Locate*
  aponta pra ele se quiser abrir o editor pelo Hub.
- `build-apk.ps1` aponta os caches do Gradle e do Package Manager pra `C:\Dev\Unity\cache`. Pra valer também quando
  você abrir o editor na mão: `setx GRADLE_USER_HOME C:\Dev\Unity\cache\gradle` e `setx UPM_CACHE_ROOT C:\Dev\Unity\cache\upm`.

## Uso

```powershell
# 0) uma vez: libxlua.so (clona os fontes em android\build\third_party)
.\android\xlua\build-xlua.ps1

# 1) montar/atualizar o projeto Unity (patcheia as DLLs pro servidor que o celular alcança)
.\android\setup-project.ps1 -Server http://192.168.0.10:5380

# 2) gerar o APK (Unity em batchmode)
.\android\build-apk.ps1
```

`-Server` precisa ser um endereço que o **celular** alcance: o IP do PC na rede local (servidor em modo `host`) ou
a VPS. `127.0.0.1` no celular é o próprio celular.

## Teste no celular

1. Ative a **Depuração USB** no celular (Opções do desenvolvedor) e conecte no PC.
2. `.\android\build-apk.ps1 -Install`, abra o app uma vez (cria a pasta dele) e feche.
3. `.\android\push-data.ps1` copia o `StreamingAssets` (24 GB, demora) pra
   `/sdcard/Android/data/com.ro3emu.client/files/StreamingAssets`. Use `-SmallOnly` pra testar o boot sem o VFS.
4. Suba o servidor escutando na rede, com o host que o celular alcança (e libere as portas no Firewall do Windows):
   `--Ro3Emu:PublicHost=192.168.1.100` no lugar do `127.0.0.1` do `RODAR_LOCAL.cmd`.
5. Logs: `adb logcat -s Unity fsni` (tudo do port tem o prefixo `[Ro3Android]`).
