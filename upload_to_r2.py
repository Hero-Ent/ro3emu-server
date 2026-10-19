import os
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
import boto3
from botocore.config import Config

ACCOUNT_ID = "d7d9ea2ffe835c072a9bb1084f01bd57"
BUCKET_NAME = "hero-patch"
ENDPOINT_URL = f"https://{ACCOUNT_ID}.r2.cloudflarestorage.com"
LOCAL_DIR = r"D:\ro3emu-server\patch-server\repo"
MAX_WORKERS = 40

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

def upload_file(s3, local_path, s3_key, file_size):
    try:
        s3.upload_file(
            Filename=local_path,
            Bucket=BUCKET_NAME,
            Key=s3_key,
            ExtraArgs={
                'ContentType': get_content_type(s3_key)
            }
        )
        return True, file_size, None
    except Exception as e:
        return False, 0, str(e)

def main():
    if len(sys.argv) < 3:
        print("Uso: python upload_to_r2.py <ACCESS_KEY_ID> <SECRET_ACCESS_KEY>")
        sys.exit(1)

    access_key = sys.argv[1].strip()
    secret_key = sys.argv[2].strip()

    print("=" * 60)
    print("      HERO SAGA - UPLOAD DO CLIENTE / PATCH PARA CLOUDFLARE R2")
    print("=" * 60)
    print(f"Bucket:       {BUCKET_NAME}")
    print(f"Endpoint:     {ENDPOINT_URL}")
    print(f"Pasta local:  {LOCAL_DIR}")
    print(f"Conexoes:     {MAX_WORKERS} simultaneas")

    s3 = boto3.client(
        's3',
        endpoint_url=ENDPOINT_URL,
        aws_access_key_id=access_key,
        aws_secret_access_key=secret_key,
        config=Config(signature_version='s3v4', max_pool_connections=MAX_WORKERS + 10)
    )

    try:
        s3.head_bucket(Bucket=BUCKET_NAME)
        print("-> Conexao com R2 validada com sucesso!")
    except Exception as e:
        print(f"ERRO ao conectar no bucket '{BUCKET_NAME}': {e}")
        sys.exit(1)

    print("\n-> Verificando arquivos ja enviados no R2 para resumo rapido...")
    existing_objects = {}
    try:
        paginator = s3.get_paginator('list_objects_v2')
        for page in paginator.paginate(Bucket=BUCKET_NAME):
            if 'Contents' in page:
                for obj in page['Contents']:
                    existing_objects[obj['Key']] = obj['Size']
    except Exception as e:
        print(f"Aviso ao listar bucket: {e}")

    print(f"-> Arquivos ja presentes no R2: {len(existing_objects)}")

    print("-> Indexando arquivos locais...")
    to_upload = []
    total_bytes = 0
    all_local_count = 0

    for root, _, files in os.walk(LOCAL_DIR):
        for f in files:
            all_local_count += 1
            full_path = os.path.join(root, f)
            rel_path = os.path.relpath(full_path, LOCAL_DIR).replace("\\", "/")
            size = os.path.getsize(full_path)
            
            # Chunks com o mesmo tamanho são imutáveis (hash sha256). Metadados (.json) sempre atualizam.
            if not rel_path.endswith(".json") and rel_path in existing_objects and existing_objects[rel_path] == size:
                continue

            to_upload.append((full_path, rel_path, size))
            total_bytes += size

    # Adiciona tambem HeroSaga-Launcher.zip e HeroLauncher.exe para download direto do CDN
    root_dir = r"D:\ro3emu-server"
    for extra_file in ["HeroSaga-Launcher.zip", "HeroLauncher.exe"]:
        extra_path = os.path.join(root_dir, extra_file)
        if os.path.exists(extra_path):
            size = os.path.getsize(extra_path)
            to_upload.append((extra_path, extra_file, size))
            total_bytes += size

    total_files = len(to_upload)
    total_gb = total_bytes / (1024 ** 3)
    skipped = all_local_count - total_files

    print(f"Total local: {all_local_count} arquivos | Ja enviados: {skipped} | A enviar: {total_files} ({total_gb:.2f} GB)")

    if total_files == 0:
        print("\n-> Todos os arquivos ja estao atualizados no Cloudflare R2! Nada a enviar.")
        return

    start_time = time.time()
    uploaded_files = 0
    uploaded_bytes = 0
    errors = []

    print(f"\nIniciando envio acelerado...\n")

    with ThreadPoolExecutor(max_workers=MAX_WORKERS) as executor:
        futures = {
            executor.submit(upload_file, s3, path, key, size): (path, key, size)
            for path, key, size in to_upload
        }

        last_print = 0
        for future in as_completed(futures):
            ok, size, err = future.result()
            if ok:
                uploaded_files += 1
                uploaded_bytes += size
            else:
                _, key, _ = futures[future]
                errors.append((key, err))

            now = time.time()
            if now - last_print > 0.5 or uploaded_files == total_files:
                last_print = now
                elapsed = now - start_time
                pct = (uploaded_files / total_files) * 100
                speed_mb = (uploaded_bytes / (1024 * 1024)) / elapsed if elapsed > 0 else 0
                gb_up = uploaded_bytes / (1024 ** 3)
                print(f"\rProgresso: [{uploaded_files}/{total_files}] {pct:.1f}% | {gb_up:.2f}/{total_gb:.2f} GB | {speed_mb:.1f} MB/s", end="", flush=True)

    print("\n")
    if errors:
        print(f"Concluido com {len(errors)} erros:")
        for k, err in errors[:10]:
            print(f"  {k}: {err}")
    else:
        print(f"-> SUCESSO TOTAL! Todos os {total_files} arquivos foram sincronizados no Cloudflare R2!")
        print(f"Tempo decorrido: {(time.time() - start_time):.1f} segundos.")

if __name__ == "__main__":
    main()
