# WinSnap

WinSnap is a Windows screenshot tool built with WPF and .NET 10. It supports
screen capture, annotation, mosaic redaction, pinned screenshots, GIF capture,
scroll capture, tray hotkeys, and installer packaging.

## Requirements

- Windows 10 19041 or later
- .NET 10 SDK for development
- Inno Setup 6 for installer packaging

## Build

```powershell
dotnet build src\WinSnap.App\WinSnap.App.csproj -c Release
```

## Test

```powershell
dotnet test tests\WinSnap.Core.Tests\WinSnap.Core.Tests.csproj
```

## Package

```powershell
pwsh build\publish-and-pack-variants.ps1
```

The packaging script creates two installers:

- `build\installer\Output\WinSnap-Setup-with-dotnet10.exe`
- `build\installer\Output\WinSnap-Setup-no-dotnet10.exe`

## License

GNU Affero General Public License v3.0
