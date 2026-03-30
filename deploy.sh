#!/bin/bash
set -e

SERVER="lachlan@192.168.1.122"
PLUGIN_DIR="/docker/jellyfin/config/data/plugins/LetterboxdSync_1.0.0.0"
PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"

read -s -p "Server password: " PASS
echo

echo "Building..."
dotnet build -c Release "$PROJECT_DIR/LetterboxdSync/LetterboxdSync.csproj" -q

echo "Deploying..."
sshpass -p "$PASS" scp \
    "$PROJECT_DIR/LetterboxdSync/bin/Release/net9.0/LetterboxdSync.dll" \
    "$SERVER:$PLUGIN_DIR/"

echo "Restarting Jellyfin..."
sshpass -p "$PASS" ssh "$SERVER" 'docker restart jellyfin'

echo "Done. Wait a few seconds for Jellyfin to start."
