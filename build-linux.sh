#!/usr/bin/env bash
# Builds SnipFlow for Linux x64 (Ubuntu 22.04+ / Debian 12+):
#   release/packages-linux/snipflow_<version>_amd64.deb   apt-installable package (/opt/snipflow, menu entry, icons)
#   release/packages-linux/SnipFlow-<version>-linux-x64.tar.gz   portable folder with install-desktop-entry.sh
#   release/packages-linux/*.AppImage + releases.linux.json + *.nupkg   Velopack AppImage with in-app updates
# Usage: ./build-linux.sh [version] [--skip-appimage] [--skip-deb]
set -euo pipefail

version="${1:-0.8.2}"
shift || true
skip_appimage=0
skip_deb=0
for argument in "$@"; do
  case "$argument" in
    --skip-appimage) skip_appimage=1 ;;
    --skip-deb) skip_deb=1 ;;
    *) echo "Unknown option: $argument" >&2; exit 2 ;;
  esac
done
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Version must look like 0.8.0" >&2
  exit 2
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
output="$root/release"
publish="$output/publish-linux-$version"
packages="$output/packages-linux"
vpk_version="1.2.161"
tools="${TMPDIR:-/tmp}/SnipFlowBuildTools/$vpk_version"
rm -rf "$publish"
mkdir -p "$publish" "$packages" "$tools"

dotnet publish "$root/src/SnipFlow.csproj" -c Release -f net10.0 -r linux-x64 --self-contained true \
  -p:Version="$version" -o "$publish" --nologo
chmod +x "$publish/SnipFlow"

# Portable archive.
portable="$output/SnipFlow-$version-linux-x64"
rm -rf "$portable"
mkdir -p "$portable"
cp -a "$publish/." "$portable/"
cp "$root/packaging/linux/snipflow.desktop" "$portable/snipflow.desktop"
cp "$root/packaging/linux/icons/snipflow-256.png" "$portable/snipflow.png"
cp "$root/packaging/linux/install-desktop-entry.sh" "$portable/install-desktop-entry.sh"
chmod +x "$portable/install-desktop-entry.sh"
tar -C "$output" -czf "$packages/SnipFlow-$version-linux-x64.tar.gz" "$(basename "$portable")"

# Debian package.
if [[ $skip_deb -eq 0 ]]; then
  staging="$output/deb-$version"
  rm -rf "$staging"
  mkdir -p "$staging/DEBIAN" "$staging/opt/snipflow" "$staging/usr/bin" "$staging/usr/share/applications" \
           "$staging/usr/share/doc/snipflow"
  cp -a "$publish/." "$staging/opt/snipflow/"
  cat > "$staging/usr/bin/snipflow" <<'EOF'
#!/bin/sh
exec /opt/snipflow/SnipFlow "$@"
EOF
  chmod 755 "$staging/usr/bin/snipflow"
  cp "$root/packaging/linux/snipflow.desktop" "$staging/usr/share/applications/snipflow.desktop"
  for size in 16 24 32 48 64 128 256 512; do
    mkdir -p "$staging/usr/share/icons/hicolor/${size}x${size}/apps"
    cp "$root/packaging/linux/icons/snipflow-$size.png" "$staging/usr/share/icons/hicolor/${size}x${size}/apps/snipflow.png"
  done
  cp "$root/THIRD-PARTY-NOTICES.md" "$staging/usr/share/doc/snipflow/THIRD-PARTY-NOTICES.md"
  cp -a "$root/licenses" "$staging/usr/share/doc/snipflow/licenses"
  installed_size=$(du -sk "$staging" | cut -f1)
  sed -e "s/@VERSION@/$version/" -e "s/@INSTALLED_SIZE@/$installed_size/" \
    "$root/packaging/linux/debian-control" > "$staging/DEBIAN/control"
  cp "$root/packaging/linux/debian-postinst" "$staging/DEBIAN/postinst"
  cp "$root/packaging/linux/debian-postinst" "$staging/DEBIAN/postrm"
  chmod 755 "$staging/DEBIAN/postinst" "$staging/DEBIAN/postrm"
  find "$staging/opt" "$staging/usr" -type d -exec chmod 755 {} +
  dpkg-deb --root-owner-group --build "$staging" "$packages/snipflow_${version}_amd64.deb"
fi

# Velopack AppImage with in-app updates (channel "linux").
if [[ $skip_appimage -eq 0 ]]; then
  if [[ ! -x "$tools/vpk" ]]; then
    dotnet tool install vpk --version "$vpk_version" --tool-path "$tools"
  fi
  export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
  "$tools/vpk" pack --packId SnipFlow --packTitle SnipFlow --packVersion "$version" --packDir "$publish" \
    --mainExe SnipFlow --runtime linux-x64 --channel linux --icon "$root/packaging/linux/icons/snipflow-512.png" \
    --categories "Graphics;Utility" --outputDir "$packages"
fi

ls -l "$packages"
