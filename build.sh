#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
export MSBuildEnableWorkloadResolver=false
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) default_rid=linux-x64 ;;
  Linux-aarch64) default_rid=linux-arm64 ;;
  Darwin-x86_64) default_rid=osx-x64 ;;
  Darwin-arm64) default_rid=osx-arm64 ;;
  *) echo 'Specify a .NET runtime identifier as the first argument.' >&2; exit 1 ;;
esac
rid="${1:-$default_rid}"
dotnet publish src/Govori.Desktop/Govori.Desktop.csproj -c Release -r "$rid" --self-contained true --artifacts-path artifacts/dotnet -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o "artifacts/publish/$rid" --nologo "${@:2}"
echo "Built: artifacts/publish/$rid/Govori.CrossPlatform"
