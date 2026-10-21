"""
Hero Saga - envia o repositorio de patches (patch-server/repo) para a producao (Cloudflare R2).

Uso:  ENVIAR_ATUALIZACAO.cmd                       (recomendado)
      python upload_to_r2.py [ACCESS_KEY_ID SECRET_ACCESS_KEY] [--simular]

Credenciais, na ordem: argumentos, variaveis R2_ACCESS_KEY_ID / R2_SECRET_ACCESS_KEY, ou o arquivo
r2-credentials.txt na raiz do repositorio (duas linhas: access key id e secret; nunca vai para o git).

Ordem segura: chunks, manifestos, launchers e o resto PRIMEIRO; channels/*.json por ULTIMO e so se nada falhou,
para nenhum jogador ver um canal apontando para arquivos que ainda nao subiram.
Arquivos so do ambiente local (canal test e manifestos *-local / *-dev.*) nunca sao enviados.
"""
import fnmatch
import os
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed

ACCOUNT_ID = "d7d9ea2ffe835c072a9bb1084f01bd57"
BUCKET_NAME = "hero-patch"
ENDPOINT_URL = f"https://{ACCOUNT_ID}.r2.cloudflarestorage.com"
ROOT_DIR = os.path.dirname(os.path.abspath(__file__))
LOCAL_DIR = os.path.join(ROOT_DIR, "patch-server", "repo")
CREDENTIALS_FILE = os.path.join(ROOT_DIR, "r2-credentials.txt")
MAX_WORKERS = 40

# Nunca enviados para producao (relativos a patch-server/repo).
LOCAL_ONLY = [
    "channels/test.json",
    "manifests/*-local.json.zst",
    "manifests/*-local.*.json.zst",
    "manifests/*-dev.*.json.zst",
    ".publish.lock",
    "*.tmp",
]


def get_content_type(key):
    if key.endswith(".json"):
        return "application/json"
    if key.endswith(".zst"):
        return "application/zstd"
    if key.endswith(".exe"):
        return "application/vnd.microsoft.portable-executable"
    if key.endswith(".zip"):
        return "application/zip"
    return "application/octet-stream"


def is_local_only(rel_path):
    return any(fnmatch.fnmatch(rel_path, pattern) for pattern in LOCAL_ONLY)


def read_credentials(args):
    if len(args) >= 2:
        return args[0].strip(), args[1].strip()
    key, secret = os.environ.get("R2_ACCESS_KEY_ID"), os.environ.get("R2_SECRET_ACCESS_KEY")
    if key and secret:
        return key.strip(), secret.strip()
    if os.path.exists(CREDENTIALS_FILE):
        with open(CREDENTIALS_FILE, encoding="utf-8") as f:
            lines = [line.strip() for line in f if line.strip() and not line.strip().startswith("#")]
        if len(lines) >= 2:
            return lines[0], lines[1]
    print("ERRO: credenciais do R2 nao encontradas.")
    print(f"  Crie {CREDENTIALS_FILE} com duas linhas (access key id e secret access key),")
    print("  ou defina R2_ACCESS_KEY_ID e R2_SECRET_ACCESS_KEY, ou passe as duas como argumentos.")
    sys.exit(1)


def upload_file(s3, local_path, s3_key, file_size):
    try:
        s3.upload_file(
            Filename=local_path,
            Bucket=BUCKET_NAME,
            Key=s3_key,
            ExtraArgs={"ContentType": get_content_type(s3_key)},
        )
        return True, file_size, None
    except Exception as e:  # noqa: BLE001 - reportado no fim
        return False, 0, str(e)


def upload_batch(s3, items, label):
    """Envia uma lista de (caminho, chave, tamanho); devolve a lista de erros."""
    if not items:
        return []
    total_files = len(items)
    total_bytes = sum(size for _, _, size in items)
    total_gb = total_bytes / (1024 ** 3)
    print(f"\n-> {label}: {total_files} arquivos ({total_gb:.2f} GB)")
    start_time = time.time()
    uploaded_files = 0
    uploaded_bytes = 0
    errors = []
    with ThreadPoolExecutor(max_workers=MAX_WORKERS) as executor:
        futures = {executor.submit(upload_file, s3, path, key, size): (path, key, size) for path, key, size in items}
        last_print = 0
        for future in as_completed(futures):
            ok, size, err = future.result()
            if ok:
                uploaded_files += 1
                uploaded_bytes += size
            else:
                errors.append((futures[future][1], err))
            now = time.time()
            if now - last_print > 0.5 or uploaded_files + len(errors) == total_files:
                last_print = now
                elapsed = now - start_time
                pct = ((uploaded_files + len(errors)) / total_files) * 100
                speed_mb = (uploaded_bytes / (1024 * 1024)) / elapsed if elapsed > 0 else 0
                print(f"\r   [{uploaded_files}/{total_files}] {pct:.1f}% | {uploaded_bytes / (1024 ** 3):.2f}/{total_gb:.2f} GB | {speed_mb:.1f} MB/s", end="", flush=True)
    print()
    return errors


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    simulate = "--simular" in sys.argv

    print("=" * 64)
    print("   HERO SAGA - ENVIAR ATUALIZACAO PARA PRODUCAO (CLOUDFLARE R2)")
    print("=" * 64)
    print(f"Bucket:       {BUCKET_NAME}")
    print(f"Pasta local:  {LOCAL_DIR}")

    if not os.path.isdir(LOCAL_DIR):
        print(f"ERRO: pasta {LOCAL_DIR} nao existe. Rode PUBLICAR_ATUALIZACAO.cmd antes.")
        sys.exit(1)

    import boto3
    from botocore.config import Config

    access_key, secret_key = read_credentials(args)
    s3 = boto3.client(
        "s3",
        endpoint_url=ENDPOINT_URL,
        aws_access_key_id=access_key,
        aws_secret_access_key=secret_key,
        config=Config(signature_version="s3v4", max_pool_connections=MAX_WORKERS + 10),
    )

    try:
        s3.head_bucket(Bucket=BUCKET_NAME)
        print("-> Conexao com o R2 validada.")
    except Exception as e:  # noqa: BLE001
        print(f"ERRO ao conectar no bucket '{BUCKET_NAME}': {e}")
        sys.exit(1)

    print("-> Listando o que ja esta no R2...")
    existing_objects = {}
    paginator = s3.get_paginator("list_objects_v2")
    for page in paginator.paginate(Bucket=BUCKET_NAME):
        for obj in page.get("Contents", []):
            existing_objects[obj["Key"]] = obj["Size"]
    print(f"   {len(existing_objects)} arquivos ja no R2.")

    data_files, channel_files = [], []
    skipped_local = 0
    for root, _, files in os.walk(LOCAL_DIR):
        for f in files:
            full_path = os.path.join(root, f)
            rel_path = os.path.relpath(full_path, LOCAL_DIR).replace("\\", "/")
            if is_local_only(rel_path):
                skipped_local += 1
                continue
            size = os.path.getsize(full_path)
            if rel_path.startswith("channels/"):
                channel_files.append((full_path, rel_path, size))
                continue
            # Chunks, manifestos e launchers sao imutaveis (mesmo nome = mesmo conteudo). news.json sempre atualiza.
            if not rel_path.endswith(".json") and existing_objects.get(rel_path) == size:
                continue
            data_files.append((full_path, rel_path, size))

    # Downloads diretos para jogadores novos, na raiz do bucket.
    for extra_file in ["HeroSaga-Launcher.zip", "HeroLauncher.exe"]:
        extra_path = os.path.join(ROOT_DIR, extra_file)
        if os.path.exists(extra_path):
            data_files.append((extra_path, extra_file, os.path.getsize(extra_path)))

    print(f"   Ignorados (so locais): {skipped_local} | A enviar: {len(data_files)} arquivos + {len(channel_files)} canais")
    for _, key, _ in channel_files:
        print(f"   canal: {key}")
    if simulate:
        print("\n--simular: nada foi enviado.")
        return

    start = time.time()
    errors = upload_batch(s3, data_files, "Chunks, manifestos e launchers")
    if errors:
        print(f"\nERRO: {len(errors)} arquivos falharam; os canais NAO foram atualizados (os jogadores continuam na versao anterior).")
        for key, err in errors[:10]:
            print(f"  {key}: {err}")
        print("Rode de novo: o que ja subiu e pulado.")
        sys.exit(1)

    errors = upload_batch(s3, channel_files, "Canais (por ultimo)")
    if errors:
        print(f"\nERRO ao enviar canais: {errors}")
        sys.exit(1)

    print(f"\n-> SUCESSO! Producao atualizada em {time.time() - start:.1f} s.")
    print("   Os jogadores recebem a atualizacao na proxima vez que abrirem o launcher.")
    print("   (Se usar cache do Cloudflare para channels/, ele pode levar alguns minutos para expirar.)")


if __name__ == "__main__":
    main()
