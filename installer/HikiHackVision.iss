; Instalador do HikiHackVision (Inno Setup 6).
; Gere com: powershell -ExecutionPolicy Bypass -File installer\build.ps1
; O app instalado usa a própria pasta como raiz: tools\ (FFmpeg) e as pastas de saída
; (convertidos, cortados, normatizados, prontos) ficam dentro da pasta de instalação.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir SourcePath + "..\publish\win-x64"
#endif
#ifndef ToolsDir
  #define ToolsDir SourcePath + "..\tools"
#endif
#define AppName "HikiHackVision"
#define AppExe "HikiHackVision.exe"

[Setup]
AppId={{FDB1CE23-52EF-409D-B477-EFEECE98B762}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
; Pasta visível e fora de Documentos/OneDrive: os vídeos gerados ficam nela
DefaultDirName={sd}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Instala só para o usuário atual, sem pedir administrador
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#SourcePath}..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
; Compressão sólida: ffmpeg.exe e ffprobe.exe compartilham quase todo o código
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
SelectDirLabel3=O [name] será instalado na pasta abaixo. Os vídeos gerados (pastas convertidos, cortados, normatizados e prontos) também ficarão nela, então escolha um disco com bastante espaço livre.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
#if FileExists(ToolsDir + "\ffmpeg.exe") && FileExists(ToolsDir + "\ffprobe.exe")
Source: "{#ToolsDir}\ffmpeg.exe"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "{#ToolsDir}\ffprobe.exe"; DestDir: "{app}\tools"; Flags: ignoreversion
#else
  #pragma warning "FFmpeg não encontrado em " + ToolsDir + ": o app vai baixá-lo na primeira execução."
#endif

[Dirs]
; Removida na desinstalação só se estiver vazia (os vídeos gerados são preservados)
Name: "{app}\prontos"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{group}\Vídeos prontos"; Filename: "{app}\prontos"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
