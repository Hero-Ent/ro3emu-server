#!/bin/bash
# Hero Saga: keeps the 4 newest database backups (each is as big as ro3emu.db) and drops stale partial copies.
cd /opt/ro3emu-server/data/backups || exit 0
ls -1t ro3emu-*.db 2>/dev/null | tail -n +5 | xargs -r rm -f
find . -maxdepth 1 -name 'ro3emu-*.partial*' -mmin +120 -delete
