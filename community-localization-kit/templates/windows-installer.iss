#ifndef SourceDir
  #error SourceDir must be supplied by the package builder.
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by the package builder.
#endif
#ifndef AppVersion
  #error AppVersion must be supplied by the package builder.
#endif
#ifndef AppName
  #error AppName must be supplied by the package builder.
#endif
#ifndef AppId
  #error AppId must be supplied by the package builder.
#endif
#ifndef InstallerBaseName
  #error InstallerBaseName must be supplied by the package builder.
#endif
#ifndef InstallLeaf
  #error InstallLeaf must be supplied by the package builder.
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Invokers community localization builder
DefaultDirName={localappdata}\Programs\InvokersCommunity\{#InstallLeaf}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
Compression=lzma/normal
SolidCompression=no
OutputDir={#OutputDir}
OutputBaseFilename={#InstallerBaseName}
Uninstallable=yes
CreateUninstallRegKey=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
ChangesAssociations=no
ChangesEnvironment=no
UsePreviousAppDir=no
LicenseFile={#SourceDir}\LICENSE.txt
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs replacesameversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\CommunityLocalization.cmd"; WorkingDir: "{app}"

; Setup installs only the locally built patcher. It never starts the patcher and never writes game data.

[Code]
function CanonicalDirectory(const Path: String): String;
begin
  Result := AddBackslash(ExpandFileName(Path));
end;

function ExistingPathIsReparsePoint(const Path: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(RemoveBackslash(Path), FindRec) then
  begin
    try
      Result := (FindRec.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0;
    finally
      FindClose(FindRec);
    end;
  end;
end;

function PathTraversesReparsePoint(const Path: String): Boolean;
var
  CurrentPath: String;
  CurrentWithoutSlash: String;
  ParentPath: String;
begin
  Result := False;
  CurrentPath := CanonicalDirectory(Path);
  while CurrentPath <> '' do
  begin
    if ExistingPathIsReparsePoint(CurrentPath) then
    begin
      Result := True;
      Exit;
    end;
    CurrentWithoutSlash := RemoveBackslash(CurrentPath);
    ParentPath := ExtractFileDir(CurrentWithoutSlash);
    if (ParentPath = '') or (CompareText(CurrentWithoutSlash, RemoveBackslash(ParentPath)) = 0) then
      Exit;
    CurrentPath := ParentPath;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RequestedDirectory: String;
  FixedDirectory: String;
begin
  Result := '';
  RequestedDirectory := CanonicalDirectory(WizardDirValue);
  FixedDirectory := CanonicalDirectory(ExpandConstant('{localappdata}\Programs\InvokersCommunity\{#InstallLeaf}'));
  if CompareText(RequestedDirectory, FixedDirectory) <> 0 then
  begin
    Result := 'This self-build uses one fixed per-user directory. Remove any /DIR override and run Setup again.';
    Exit;
  end;
  if PathTraversesReparsePoint(RequestedDirectory) then
  begin
    Result := 'Installation through a directory junction or symbolic link is blocked.';
    Exit;
  end;
end;
