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
AppName=InvokersRu
AppVersion={#AppVersion}
AppVerName=InvokersRu 3.0 Preview
AppPublisher=InvokersRu Community
AppPublisherURL=https://github.com/Braintfy/ruslocal-invokers
AppSupportURL=https://github.com/Braintfy/ruslocal-invokers/issues
AppUpdatesURL=https://github.com/Braintfy/ruslocal-invokers/releases
DefaultDirName={localappdata}\Programs\InvokersRu
DefaultGroupName=InvokersRu
DisableProgramGroupPage=yes
DisableDirPage=yes
AlwaysShowDirOnReadyPage=yes
DisableWelcomePage=no
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
UninstallDisplayName=InvokersRu 3.0 Preview
UninstallDisplayIcon={app}\InvokersRu.Gui.exe
SetupLogging=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
ChangesAssociations=no
ChangesEnvironment=no
UsePreviousAppDir=no
LicenseFile={#SourceDir}\LICENSE.txt
VersionInfoDescription=InvokersRu 3.0 Preview installer
VersionInfoCompany=InvokersRu Community
VersionInfoProductName=InvokersRu 3.0 Preview
#ifdef InnoSignTool
SignTool={#InnoSignTool}
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; The build script has already verified every relative path, length and SHA-256
; against PAYLOAD-SHA256.json, then copied the exact tree to SourceDir.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\InvokersRu"; Filename: "{app}\InvokersRu.Gui.exe"; WorkingDir: "{app}"; Comment: "Русификатор Invokers: Titan Legacy"

; Intentionally no [Run], [Registry], [Tasks], or service entries.
; The small [Code] guard below only enforces the one fixed per-user directory,
; including for silent installs and a hostile /DIR command-line override.

[Code]
function CanonicalDirectory(const Path: String): String;
begin
  Result := AddBackslash(ExpandFileName(Path));
end;

function IsSameOrBelow(const Candidate, Root: String): Boolean;
var
  CanonicalCandidate: String;
  CanonicalRoot: String;
begin
  CanonicalCandidate := Lowercase(CanonicalDirectory(Candidate));
  CanonicalRoot := Lowercase(CanonicalDirectory(Root));
  Result := Pos(CanonicalRoot, CanonicalCandidate) = 1;
end;

function IsProtectedGameOrCachePath(const Candidate: String): Boolean;
begin
  Result :=
    IsSameOrBelow(Candidate, ExpandConstant('{userappdata}\zone.hitzone.invokers.launcher\game')) or
    IsSameOrBelow(Candidate, ExpandConstant('{localappdata}\Programs\Invokers Titan Legacy')) or
    IsSameOrBelow(Candidate, ExpandConstant('{%USERPROFILE}\AppData\LocalLow\Hit_Zone\Invokers\i18n'));
end;

function IsExistingReparsePoint(const Path: String): Boolean;
var
  SearchPath: String;
  FindRec: TFindRec;
begin
  Result := False;
  SearchPath := RemoveBackslash(Path);

  { Do not query a drive-relative name such as C:. }
  if (Length(SearchPath) = 2) and (SearchPath[2] = ':') then
    Exit;

  if FindFirst(SearchPath, FindRec) then
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
  ParentWithoutSlash: String;
begin
  Result := False;
  CurrentPath := CanonicalDirectory(Path);

  while CurrentPath <> '' do
  begin
    if IsExistingReparsePoint(CurrentPath) then
    begin
      Result := True;
      Exit;
    end;

    CurrentWithoutSlash := RemoveBackslash(CurrentPath);
    ParentPath := ExtractFileDir(CurrentWithoutSlash);
    if ParentPath = '' then
      Exit;

    ParentWithoutSlash := RemoveBackslash(ParentPath);
    if CompareText(CurrentWithoutSlash, ParentWithoutSlash) = 0 then
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
  FixedDirectory := CanonicalDirectory(ExpandConstant('{localappdata}\Programs\InvokersRu'));

  if IsProtectedGameOrCachePath(RequestedDirectory) then
  begin
    Result := 'Installation into an Invokers game or localization-cache directory is blocked.';
    Exit;
  end;

  if CompareText(RequestedDirectory, FixedDirectory) <> 0 then
  begin
    Result := 'InvokersRu uses one fixed per-user directory. Remove the /DIR override and run Setup again.';
    Exit;
  end;

  if PathTraversesReparsePoint(RequestedDirectory) then
  begin
    Result := 'Installation through a directory junction or symbolic link is blocked.';
    Exit;
  end;
end;
