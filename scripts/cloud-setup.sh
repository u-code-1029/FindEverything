#!/usr/bin/env bash
set -euo pipefail

cd /workspace/FindEverything
source scripts/dotnet-env.sh

findeverything_sdk_version=10.0.401
findeverything_sdk_url=https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-x64.tar.gz
# Published by Microsoft's official .NET 10 release metadata:
# https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
findeverything_sdk_sha512=51c8b999af9e8dd9998c9edc5944e19a90788862068acd38694e098889054ce8c23d4f0c5cccfa16bf187d044562359e5ee69a9f8ad0bbe913ba90311fbce25b

if [[ ! -x "$DOTNET_ROOT/dotnet" ]] ||
    ! "$DOTNET_ROOT/dotnet" --list-sdks | awk -v expected="$findeverything_sdk_version" '
        $1 == expected { found = 1 }
        END { exit !found }
    '; then
    if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
        printf '%s\n' 'This cloud setup script requires Linux x86_64.' >&2
        exit 1
    fi

    findeverything_archive=$(mktemp /tmp/findeverything-dotnet.XXXXXX.tar.gz)
    trap 'rm -f -- "$findeverything_archive"' EXIT
    curl --fail --silent --show-error --location \
        --proto '=https' --proto-redir '=https' \
        "$findeverything_sdk_url" --output "$findeverything_archive"
    printf '%s  %s\n' "$findeverything_sdk_sha512" "$findeverything_archive" |
        sha512sum --check --strict

    mkdir -p "$DOTNET_ROOT"
    # Install side by side: keep existing SDK version directories intact.
    tar --extract --gzip --file "$findeverything_archive" \
        --directory "$DOTNET_ROOT" --no-same-owner
fi

dotnet restore FindEverything.sln --locked-mode
dotnet build FindEverything.sln --configuration Release --no-restore
