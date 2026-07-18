; ============================================================================
;  Bank Reconciliation Tool - Inno Setup Script
; ============================================================================
;  Builds Setup.exe from the self-contained single-file publish output.
;  Requires Inno Setup 6+ (https://jrsoftware.org/isdl.php)
;
;  Usage:
;    1. Run build.bat first (or `dotnet publish` manually) to produce
;       dist\app\BankReconciliation.exe
;    2. Compile this script with the Inno Setup Compiler (ISCC.exe), or open
;       it in the Inno Setup IDE and click Build.
;    3. The installer is written to dist\installer\BankReconciliationSetup.exe
;
;  build.bat does all of this automatically if Inno Setup is installed.
; ============================================================================

#define MyAppName "Bank Reconciliation Tool"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Xfinity Holdings"
#define MyAppExeName "BankReconciliation.exe"
#define MyPublishDir "..\dist\app"

[Setup]
; Unique installer GUID - do not change between versions (lets the installer
; detect/upgrade a previous install rather than creating a duplicate entry).
AppId={{9E6F2B3E-7B2C-4E8A-9F0F-4C9C7C4A8B21}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=BankReconciliationSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything dotnet publish produced (the single exe plus any native
; libraries that can't be embedded) - excluding .pdb debug symbols, which
; aren't needed on an end user's machine.
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
