#!/usr/bin/env bash
set -euo pipefail

echo Setting up Python environment...

pip3 install -r requirements-dev.txt

echo Setting up commit hooks...
if git rev-parse --is-inside-work-tree > /dev/null 2>&1; then
    pre-commit install --install-hooks
else
    echo "Not inside a Git repository, skipping pre-commit hook installation."
fi

NPM_REGISTRY="${NPM_CONFIG_REGISTRY:-https://packagefeedproxy.microsoft.io/npm/}"
echo "Configuring npm registry to $NPM_REGISTRY..."
npm config set registry "$NPM_REGISTRY"

echo Setting up Registration environment...
cd src/registration
npm i
cd /workspaces/azure-ai-proxy-lite
npm install -g @azure/static-web-apps-cli

echo Installing test coverage tools...
dotnet tool install --global dotnet-reportgenerator-globaltool

echo "Playwright setup is on-demand. Run 'npm run e2e:install' when you need E2E tests."

# SWA CLI's StaticSitesClient is x86-64 only.
# Install emulation only when needed:
# - default auto mode: arm64 + Docker Desktop/LinuxKit (typical macOS devcontainer runtime)
# - force mode: set SWA_FORCE_QEMU_INSTALL=1
# - skip mode: set SWA_SKIP_QEMU_INSTALL=1
arch="$(uname -m)"
is_arm64=false
if [ "$arch" = "aarch64" ] || [ "$arch" = "arm64" ]; then
    is_arm64=true
fi

is_linuxkit=false
if grep -qiE 'linuxkit|moby' /proc/version /proc/sys/kernel/osrelease 2>/dev/null; then
    is_linuxkit=true
fi

install_qemu=false
if [ "${SWA_SKIP_QEMU_INSTALL:-0}" = "1" ]; then
    install_qemu=false
elif [ "${SWA_FORCE_QEMU_INSTALL:-0}" = "1" ]; then
    install_qemu=true
elif [ "$is_arm64" = true ] && [ "$is_linuxkit" = true ]; then
    install_qemu=true
fi

if [ "$install_qemu" = true ]; then
    echo "Installing QEMU x86-64 emulation for SWA CLI..."

    sudo dpkg --add-architecture amd64
    sudo apt-get update

    qemu_pkg=""
    if apt-cache show qemu-user-static >/dev/null 2>&1; then
        qemu_pkg="qemu-user-static"
    elif apt-cache show qemu-user >/dev/null 2>&1; then
        qemu_pkg="qemu-user"
    fi

    if [ -z "$qemu_pkg" ]; then
        echo "Warning: no qemu user-mode package found in apt sources; skipping SWA emulation setup."
    else
        sudo apt-get install -y "$qemu_pkg"

        libs="libc6:amd64 zlib1g:amd64 libstdc++6:amd64"
        if apt-cache show libicu72 >/dev/null 2>&1; then
            libs="$libs libicu72:amd64"
        elif apt-cache show libicu74 >/dev/null 2>&1; then
            libs="$libs libicu74:amd64"
        elif apt-cache show libicu71 >/dev/null 2>&1; then
            libs="$libs libicu71:amd64"
        fi

        if apt-cache show libssl3 >/dev/null 2>&1; then
            libs="$libs libssl3:amd64"
        elif apt-cache show libssl1.1 >/dev/null 2>&1; then
            libs="$libs libssl1.1:amd64"
        fi

        sudo apt-get install -y $libs

        # Register binfmt handler so x86-64 ELF binaries run automatically via QEMU.
        if [ -d /proc/sys/fs/binfmt_misc ]; then
            sudo mount -t binfmt_misc binfmt_misc /proc/sys/fs/binfmt_misc 2>/dev/null || true
            if [ -e /proc/sys/fs/binfmt_misc/register ] && [ -x /usr/bin/qemu-x86_64-static ]; then
                echo ':qemu-x86_64:M::\x7fELF\x02\x01\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x02\x00\x3e\x00:\xff\xff\xff\xff\xff\xfe\xfe\x00\xff\xff\xff\xff\xff\xff\xff\xff\xfe\xff\xff\xff:/usr/bin/qemu-x86_64-static:OCF' \
                    | sudo tee /proc/sys/fs/binfmt_misc/register >/dev/null 2>&1 || true
            fi
        fi

        echo "QEMU x86-64 emulation setup complete."
    fi
elif [ "$is_arm64" = true ]; then
    echo "arm64 detected on non-LinuxKit runtime; skipping QEMU auto-install."
    echo "Set SWA_FORCE_QEMU_INSTALL=1 to force installation for this environment."
fi
