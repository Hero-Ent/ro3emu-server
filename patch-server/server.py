#!/usr/bin/env python3
"""
Hero Saga - Servidor Local de Patches (Ro3Emu Patch Server)
Porta padrão: 8790
Diretório padrão: patch-server/repo
"""

import http.server
import socketserver
import os
import sys
import mimetypes

PORT = 8790
DEFAULT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "repo")
TARGET_DIR = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DIR

# Assegurar tipos MIME corretos
mimetypes.add_type("application/json; charset=utf-8", ".json")
mimetypes.add_type("application/zstd", ".zst")
mimetypes.add_type("application/octet-stream", ".bin")
mimetypes.add_type("application/vnd.microsoft.portable-executable", ".exe")

class PatchHandler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=TARGET_DIR, **kwargs)

    def end_headers(self):
        # Habilitar CORS para WebView2 e navegadores
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "*")

        # Cache headers específicos
        path = self.path.split("?")[0].lower()
        if "channels/" in path or path.endswith("news.json"):
            # Canais e notícias nunca devem ficar em cache
            self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
            self.send_header("Pragma", "no-cache")
            self.send_header("Expires", "0")
        elif "manifests/" in path or "chunks/" in path:
            # Arquivos imutáveis por hash podem ficar em cache
            self.send_header("Cache-Control", "public, max-age=31536000, immutable")

        super().end_headers()

    def do_OPTIONS(self):
        self.send_response(200, "OK")
        self.end_headers()

    def log_message(self, format, *args):
        # Log simplificado com data/hora
        sys.stdout.write(f"[{self.log_date_time_string()}] {args[0]} -> {args[1]}\n")
        sys.stdout.flush()

class ThreadingTCPServer(socketserver.ThreadingMixIn, socketserver.TCPServer):
    allow_reuse_address = True
    daemon_threads = True

def main():
    if not os.path.isdir(TARGET_DIR):
        os.makedirs(TARGET_DIR, exist_ok=True)

    print("=" * 65)
    print("  HERO SAGA - SERVIDOR DE ATUALIZAÇÕES / PATCH SERVER")
    print("=" * 65)
    print(f"  Status:     Online")
    print(f"  Endereço:   http://localhost:{PORT}/")
    print(f"  Diretório:  {TARGET_DIR}")
    print(f"  Canal test: http://localhost:{PORT}/channels/test.json")
    print(f"  Notícias:   http://localhost:{PORT}/news.json")
    print("=" * 65)
    print("Pressione Ctrl+C para encerrar o servidor a qualquer momento.\n")

    try:
        with ThreadingTCPServer(("", PORT), PatchHandler) as httpd:
            httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nServidor de patches encerrado.")
    except Exception as e:
        print(f"\nErro no servidor: {e}")

if __name__ == "__main__":
    main()
