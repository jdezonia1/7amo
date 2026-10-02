#!/usr/bin/env bash
# Cloud-session check of the WPF app before a Windows run: compile it (needs Microsoft's
# Sdks/Microsoft.NET.Sdk.WindowsDesktop folder in the SDK, see README "Building on Linux"), then
# check every {Binding} path on every screen. Exit code 0 = clean.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build src/Raffaello.App/Raffaello.App.csproj -nologo -v q
dotnet run --project tools/Raffaello.XamlCheck -- src/Raffaello.App/bin/Debug/net8.0-windows10.0.19041.0 src/Raffaello.App
