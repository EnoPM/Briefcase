#ifndef AppVersion
  #error AppVersion must be supplied by the release build.
#endif

[Setup]
AppId={{902F3A53-7B96-4E2F-A28D-1537D8365E4C}
AppName=Briefcase
AppVersion={#AppVersion}
AppPublisher=EnoPM
DefaultDirName={localappdata}\Programs\Briefcase
DefaultGroupName=Briefcase
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
WizardStyle=modern
LanguageDetectionMethod=uilanguage
UsePreviousLanguage=no
ShowLanguageDialog=no
SetupIconFile=..\Briefcase.ServerApp.Launcher\Assets\BriefcaseIcon.ico
OutputDir=..\dist
OutputBaseFilename=Briefcase-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\ServerApp\Briefcase.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: ShouldInstallBundledLauncher
Source: "..\dist\ServerApp\libSkiaSharp.dll"; DestDir: "{app}"; Flags: ignoreversion; Check: ShouldInstallBundledLauncher
Source: "..\dist\ServerApp\libHarfBuzzSharp.dll"; DestDir: "{app}"; Flags: ignoreversion; Check: ShouldInstallBundledLauncher
Source: "..\dist\ServerApp\av_libglesv2.dll"; DestDir: "{app}"; Flags: ignoreversion; Check: ShouldInstallBundledLauncher
Source: "..\dist\ServerApp\UpdateSource.json"; DestDir: "{app}"; Flags: ignoreversion; Check: ShouldInstallBundledLauncher
Source: "..\dist\ServerApp\Launcher\manifest.json"; DestDir: "{app}\Launcher"; Flags: ignoreversion; Check: ShouldInstallBundledLauncher
Source: "..\dist\ServerApp\Translations\*.json"; DestDir: "{app}\Translations"; Flags: onlyifdoesntexist
Source: "..\dist\ServerApp\App\current.json"; DestDir: "{app}\App"; Flags: onlyifdoesntexist
Source: "..\dist\ServerApp\App\versions\{#AppVersion}\*"; DestDir: "{app}\App\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Briefcase"; Filename: "{app}\Briefcase.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Briefcase"; Filename: "{app}\Briefcase.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Briefcase.exe"; Description: "{cm:LaunchProgram,Briefcase}"; Flags: nowait postinstall skipifsilent

[Code]
function InstalledVersion: String;
var
  Contents: AnsiString;
  Tail: String;
  Position: Integer;
begin
  Result := '';
  if not LoadStringFromFile(ExpandConstant('{app}\App\current.json'), Contents) then Exit;
  Position := Pos('"version"', Contents);
  if Position = 0 then Exit;
  Tail := Copy(Contents, Position + Length('"version"'), Length(Contents));
  Position := Pos(':', Tail);
  if Position = 0 then Exit;
  Tail := Copy(Tail, Position + 1, Length(Tail));
  Position := Pos('"', Tail);
  if Position = 0 then Exit;
  Tail := Copy(Tail, Position + 1, Length(Tail));
  Position := Pos('"', Tail);
  if Position = 0 then Exit;
  Result := Copy(Tail, 1, Position - 1);
end;

function CompareVersions(Left, Right: String): Integer;
var
  LeftFirst, LeftSecond, RightFirst, RightSecond: Integer;
  LeftTail, RightTail: String;
  LMajor, LMinor, LPatch, RMajor, RMinor, RPatch: Integer;
begin
  Result := 0;
  LeftFirst := Pos('.', Left);
  RightFirst := Pos('.', Right);
  if (LeftFirst = 0) or (RightFirst = 0) then Exit;
  LeftTail := Copy(Left, LeftFirst + 1, Length(Left));
  RightTail := Copy(Right, RightFirst + 1, Length(Right));
  LeftSecond := Pos('.', LeftTail);
  RightSecond := Pos('.', RightTail);
  if (LeftSecond = 0) or (RightSecond = 0) then Exit;
  try
    LMajor := StrToInt(Copy(Left, 1, LeftFirst - 1));
    LMinor := StrToInt(Copy(LeftTail, 1, LeftSecond - 1));
    LPatch := StrToInt(Copy(LeftTail, LeftSecond + 1, Length(LeftTail)));
    RMajor := StrToInt(Copy(Right, 1, RightFirst - 1));
    RMinor := StrToInt(Copy(RightTail, 1, RightSecond - 1));
    RPatch := StrToInt(Copy(RightTail, RightSecond + 1, Length(RightTail)));
  except
    Exit;
  end;
  if LMajor <> RMajor then Result := LMajor - RMajor
  else if LMinor <> RMinor then Result := LMinor - RMinor
  else Result := LPatch - RPatch;
end;

function ShouldInstallBundledLauncher: Boolean;
var
  Current: String;
begin
  Current := InstalledVersion;
  Result := (Current = '') or (CompareVersions(Current, '{#AppVersion}') <= 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Current, Pointer: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Current := InstalledVersion;
  if (Current <> '') and (CompareVersions(Current, '{#AppVersion}') >= 0) then Exit;
  Pointer := ExpandConstant('{app}\App\current.json');
  if not SaveStringToFile(Pointer, '{"version":"{#AppVersion}"}' + #13#10, False) then
    RaiseException('Unable to activate the bundled Briefcase version.');
end;
