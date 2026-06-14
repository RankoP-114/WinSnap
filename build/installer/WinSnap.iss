#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "WinSnap-Setup"
#endif

#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif

#define MyAppName "WinSnap"
#define MyAppVersion AppVersion
#define MyAppPublisher "WinSnap"
#define MyAppExeName "WinSnap.exe"
#define MyAppIcon "..\..\src\WinSnap.App\Assets\WinSnap.ico"

[Setup]
AppId=WinSnap
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/RankoP-114/WinSnap
AppSupportURL=https://github.com/RankoP-114/WinSnap/issues
AppUpdatesURL=https://github.com/RankoP-114/WinSnap/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
OutputDir=Output
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile={#MyAppIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no
UsedUserAreasWarning=no
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start WinSnap when Windows starts"; GroupDescription: "Startup options:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{autoprograms}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
#ifdef RequiresDotNet10
function DotNet10DesktopRuntimeInstalled(): Boolean;
var
  FindRec: TFindRec;
  RuntimeRoot: string;
begin
  Result := False;
  RuntimeRoot := ExpandConstant('{pf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(RuntimeRoot + '\10.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;
#endif

function InitializeSetup(): Boolean;
begin
  Result := True;
#ifdef RequiresDotNet10
  if not DotNet10DesktopRuntimeInstalled() then
  begin
    MsgBox('This WinSnap package does not include the .NET 10 Windows Desktop Runtime x64. Install that runtime first, or use WinSnap-Setup-with-dotnet10.exe.', mbInformation, MB_OK);
  end;
#endif
end;
