; ============================================================
; Soundboard - Script Inno Setup
; ============================================================
; A adapter :
;   - MyPublishDir : dossier de sortie de `dotnet publish`
;   - MyAppVersion : version affichee dans l'installeur
;   - AppId (GUID) : genere le tien une fois et ne le change plus
;     (Tools > Generate GUID dans l'IDE Inno Setup)
;
; Pas de secrets a gerer ici (contrairement a MacroPad.Host / Discord) :
; Soundboard n'a pas d'identifiants externes, juste sounds.json et
; port.txt, qui sont ecrits par l'appli elle-meme dans %APPDATA%\Soundboard
; (voir AppPaths.cs) et n'ont donc rien a faire dans cet installeur.
; ============================================================

#define MyAppName "Soundboard"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Yohan"
#define MyAppExeName "Soundboard.exe"

; Dossier contenant le resultat de `dotnet publish` (exe + dll + bass.dll + Assets\...)
#define MyPublishDir "..\bin\Release\net10.0\win-x64\publish"

[Setup]
AppId={{6B2E4C1A-9E3F-4B5A-8C6D-1A2B3C4D5E6F}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=.\Output
OutputBaseFilename=Soundboard-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
SetupIconFile=..\Assets\Soundboard.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
PrivilegesRequired=lowest

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked
Name: "startupicon"; Description: "Lancer Soundboard automatiquement au démarrage de Windows"; GroupDescription: "Démarrage :"; Flags: unchecked

[Files]
; Tout le contenu publié (exe, dll, bass.dll, Assets\...).
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Désinstaller {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

; Lancement au démarrage de Windows via le dossier Startup (plus simple à gérer/nettoyer qu'une clé Run)
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Lancer {#MyAppName} maintenant"; Flags: nowait postinstall skipifsilent

; Rien dans [UninstallDelete] : sounds.json et port.txt vivent dans
; %APPDATA%\Soundboard (hors de {app}), pas embarqués par cet installeur ;
; on les laisse en place à la désinstallation pour ne pas perdre la
; config des sons si l'utilisateur réinstalle.
