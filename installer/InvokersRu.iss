#ifndef SourceDir
  #error SourceDir must be supplied by the verified build script.
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by the verified build script.
#endif
#ifndef AppVersion
  #error AppVersion must be supplied by the verified build script.
#endif
#ifndef InstallerBaseName
  #error InstallerBaseName must be supplied by the verified build script.
#endif

[Setup]
AppId={{4A91DD92-3A74-4B5B-AC04-9417294887B3}
AppName=InvokersRu Diagnostic Preview
AppVersion={#AppVersion}
AppVerName=InvokersRu Diagnostic Preview {#AppVersion}
AppPublisher=InvokersRu Community
DefaultDirName={localappdata}\Programs\InvokersRu
DefaultGroupName=InvokersRu
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename={#InstallerBaseName}
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayName=InvokersRu Diagnostic Preview
UninstallDisplayIcon={app}\InvokersRu.Gui.exe
SetupLogging=yes
RestartApplications=no
CloseApplications=yes
UsePreviousAppDir=no
LicenseFile={#SourceDir}\LICENSE.txt
VersionInfoDescription=InvokersRu diagnostic patcher preview installer
VersionInfoCompany=InvokersRu Community
VersionInfoProductName=InvokersRu Diagnostic Preview

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#SourceDir}\InvokersRu.Gui.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\InvokersRu.Cli.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\ru_RU.mvp.jsonl"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\GUI-PUBLISH.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\TRUSTED-COMPATIBILITY.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\PREVIEW-BUILD-REPORT.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\TRANSLATION-AUDIT.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\SUPERVISED-PUBLISH.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\TEST-INSTRUCTIONS.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\glossary.ru.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\style-guide.ru.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\PAYLOAD-SHA256.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\InvokersRu"; Filename: "{app}\InvokersRu.Gui.exe"; WorkingDir: "{app}"; Comment: "Русификатор Invokers: Titan Legacy"

; Intentionally no [Run] entry. Installation never starts the patcher or changes game files.
