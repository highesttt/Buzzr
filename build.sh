#!/bin/bash
# Usage: ./build.sh [--version 0.0.3.0] [--platform x64] [--config Release] [--skip-sign] [--debug-only]

VERSION="0.0.3.0"
PLATFORM="x64"
CONFIG="Release"
SKIP_SIGN=false
DEBUG_ONLY=false
ROOT="$(cd "$(dirname "$0")" && pwd -W)"
PROJ="$ROOT\\Buzzr\\Buzzr.csproj"
OUT="$ROOT\\release"
CC="E:/Programs/msys64/ucrt64/bin/gcc.exe"

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

while [[ $# -gt 0 ]]; do
    case $1 in
        --version) VERSION="$2"; shift 2;;
        --platform) PLATFORM="$2"; shift 2;;
        --config) CONFIG="$2"; shift 2;;
        --skip-sign) SKIP_SIGN=true; shift;;
        --debug-only) DEBUG_ONLY=true; shift;;
        *) echo "Unknown: $1"; exit 1;;
    esac
done

step() { echo -e "\n\033[36m>> $1\033[0m"; }
ok() { echo -e "   \033[32m$1\033[0m"; }
warn() { echo -e "   \033[33m$1\033[0m"; }
fail() { echo -e "   \033[31m$1\033[0m"; exit 1; }

UROOT="$(cd "$(dirname "$0")" && pwd)"

step "Building Buzzr sidecar"
cd "$UROOT/sidecar" || fail "sidecar dir not found"
CC="$CC" CGO_ENABLED=1 go build -tags goolm -ldflags "-s -w" -o buzzr-sidecar.exe . || fail "Sidecar build failed"
SIZE=$(du -h buzzr-sidecar.exe | cut -f1)
ok "Built buzzr-sidecar.exe ($SIZE)"

if $DEBUG_ONLY; then
    step "Building debug app"
    cd "$UROOT"
    dotnet build "$PROJ" -p:Platform=$PLATFORM -c Debug || fail "Build failed"
    cp "$UROOT/sidecar/buzzr-sidecar.exe" "$UROOT/Buzzr/bin/$PLATFORM/Debug/net8.0-windows10.0.22621.0/"
    ok "Debug build ready at Buzzr/bin/$PLATFORM/Debug/net8.0-windows10.0.22621.0/Buzzr.exe"
    exit 0
fi

step "Preparing release directory"
rm -rf "$UROOT/release"
mkdir -p "$UROOT/release"

step "Building portable ($PLATFORM $CONFIG)"
PORTDIR="$OUT\\portable"
cd "$UROOT"
dotnet publish "$PROJ" \
    --configuration "$CONFIG" \
    --runtime "win-$PLATFORM" \
    --self-contained true \
    --output "$PORTDIR" \
    -p:Platform=$PLATFORM \
    -p:WindowsPackageType=None \
    -p:PublishSingleFile=false \
    -p:Version=$VERSION \
    || fail "Portable build failed"

ZIP="$UROOT/release/Buzzr-$VERSION-$PLATFORM-portable.zip"
cd "$UROOT/release/portable"
unset MSYS_NO_PATHCONV
zip -r "$ZIP" . > /dev/null 2>&1 || 7z a -tzip "$ZIP" . > /dev/null 2>&1 || warn "Couldn't create zip"
export MSYS_NO_PATHCONV=1
ok "Portable: $ZIP"

step "Building MSIX ($PLATFORM $CONFIG)"
cd "$UROOT"
dotnet publish "$PROJ" \
    --configuration "$CONFIG" \
    --runtime "win-$PLATFORM" \
    --self-contained true \
    --output "$OUT\\msix-publish" \
    -p:Platform=$PLATFORM \
    -p:WindowsPackageType=MSIX \
    -p:AppxPackageDir="$OUT\\msix\\" \
    -p:AppxBundle=Never \
    -p:AppxPackageSigningEnabled=false \
    -p:GenerateAppxPackageOnBuild=true \
    -p:Version=$VERSION \
    2>&1

MSIX=$(find "$UROOT/release" -name "*.msix" 2>/dev/null | head -1)
if [ -n "$MSIX" ]; then
    ok "MSIX: $MSIX"

    if ! $SKIP_SIGN; then
        step "Signing MSIX"
        CERTPATH="$ROOT\\.certs\\Buzzr.pfx"
        CERTPW="Buzzr-Dev"
        CERTSUBJECT="CN=dev.highest.buzzr"

        # create cert if it doesn't exist
        if [ ! -f "$UROOT/.certs/Buzzr.pfx" ]; then
            mkdir -p "$UROOT/.certs"
            powershell -c "
                \$cert = New-SelfSignedCertificate -Type Custom -Subject '$CERTSUBJECT' -KeyUsage DigitalSignature -FriendlyName 'Buzzr Development' -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
                \$pw = ConvertTo-SecureString -String '$CERTPW' -Force -AsPlainText
                Export-PfxCertificate -Cert \"Cert:\CurrentUser\My\\\$(\$cert.Thumbprint)\" -FilePath '$CERTPATH' -Password \$pw | Out-Null
                try {
                    \$pfx = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2('$CERTPATH', '$CERTPW')
                    \$store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPeople','LocalMachine')
                    \$store.Open('ReadWrite'); \$store.Add(\$pfx); \$store.Close()
                } catch {}
            " 2>&1
            ok "Created signing cert"
        fi

        # find signtool from NuGet cache
        SIGNTOOL=$(find "$USERPROFILE/.nuget/packages/microsoft.windows.sdk.buildtools" -path "*/x64/signtool.exe" 2>/dev/null | sort -r | head -1)
        if [ -z "$SIGNTOOL" ]; then
            SIGNTOOL=$(find "/c/Program Files" "/c/Program Files (x86)" -name "signtool.exe" -path "*/x64/*" 2>/dev/null | sort -r | head -1)
        fi

        if [ -n "$SIGNTOOL" ]; then
            "$SIGNTOOL" sign /fd SHA256 /a /f "$ROOT\\.certs\\Buzzr.pfx" /p "$CERTPW" "$MSIX" 2>&1
            if [ $? -eq 0 ]; then
                ok "Signed"
                FINAL="$UROOT/release/Buzzr-$VERSION-$PLATFORM.msix"
                cp "$MSIX" "$FINAL"
                ok "Output: $FINAL"
            else
                warn "Signing failed"
            fi
        else
            warn "signtool.exe not found — MSIX is unsigned"
        fi
    fi
else
    warn "MSIX not found in output"
fi

# bundle MSIX installer (msix + cert + install script)
if [ -n "$MSIX" ] && [ -f "$UROOT/.certs/Buzzr.pfx" ]; then
    step "Creating MSIX installer bundle"
    BUNDLE="$UROOT/release/Buzzr-$VERSION-$PLATFORM-installer"
    mkdir -p "$BUNDLE"
    cp "$UROOT/release/Buzzr-$VERSION-$PLATFORM.msix" "$BUNDLE/" 2>/dev/null || cp "$MSIX" "$BUNDLE/Buzzr-$VERSION-$PLATFORM.msix"
    cp "$UROOT/.certs/Buzzr.pfx" "$BUNDLE/"
    cp "$UROOT/install.ps1" "$BUNDLE/"
    BUNDLEZIP="$UROOT/release/Buzzr-$VERSION-$PLATFORM-installer.zip"
    cd "$BUNDLE"
    unset MSYS_NO_PATHCONV
    zip -r "$BUNDLEZIP" . > /dev/null 2>&1 || 7z a -tzip "$BUNDLEZIP" . > /dev/null 2>&1
    export MSYS_NO_PATHCONV=1
    rm -rf "$BUNDLE"
    ok "Installer: $BUNDLEZIP"
fi

step "Done"
echo ""
find "$UROOT/release" -maxdepth 1 -type f -exec ls -lh {} \; 2>/dev/null | awk '{print "   " $5 "  " $9}'
echo -e "\n\033[36mOutput: $UROOT/release\033[0m"
