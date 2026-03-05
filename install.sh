#!/bin/bash
set -euo pipefail

REPO="fifthsegment/distill"
INSTALL_DIR="${DISTILL_INSTALL_DIR:-$HOME/.local/bin}"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)  RID="linux-x64" ;;
  Darwin-arm64)  RID="osx-arm64" ;;
  Darwin-x86_64) RID="osx-x64" ;;
  MINGW*|MSYS*|CYGWIN*)  RID="win-x64" ;;
  *) echo "Unsupported platform: $(uname -s) $(uname -m)" >&2; exit 1 ;;
esac

VERSION=$(curl -sfL "https://api.github.com/repos/${REPO}/releases/latest" | grep '"tag_name"' | cut -d'"' -f4)
if [ -z "$VERSION" ]; then
  echo "Failed to fetch latest version" >&2
  exit 1
fi

URL="https://github.com/${REPO}/releases/download/${VERSION}/distill-${VERSION#v}-${RID}.tar.gz"

echo "Installing distill ${VERSION} (${RID}) to ${INSTALL_DIR}"
mkdir -p "$INSTALL_DIR"
curl -sfL "$URL" | tar xz -C "$INSTALL_DIR"
chmod +x "${INSTALL_DIR}/distill" 2>/dev/null || true
echo "Installed: ${INSTALL_DIR}/distill"

if ! echo "$PATH" | grep -q "$INSTALL_DIR"; then
  echo "Add to PATH: export PATH=\"${INSTALL_DIR}:\$PATH\""
fi
