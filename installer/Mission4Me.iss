; ============================================================================
;  Mission4Me — instalador (Inno Setup 6.3+)
;  Compilado pelo build.ps1:  ISCC /DAppVersion=1.2.3 installer\Mission4Me.iss
;
;  • Instalação POR USUÁRIO, sem administrador (o app só mexe em HKCU e %APPDATA%)
;  • Executável self-contained: não exige .NET instalado
;  • Atualização: fecha o app de forma limpa (devolve a cor da barra) antes de copiar
;  • Desinstalação: restaura a barra, remove a inicialização automática e pergunta
;    se deve apagar o histórico
; ============================================================================

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName      "Mission4Me"
#define AppExe       "Mission4Me.exe"
#define AppPublisher "HNKL Tecnologia"
; Repositório oficial
#define AppURL       "https://github.com/rafahenk/Mission4Me"

[Setup]
; AppId identifica o produto para atualizações/desinstalação — NUNCA altere depois do 1º release
AppId={{6C1F3B7E-9A2D-4E85-B3C4-4D1E8F27A9B5}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} — instalador

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\Mission4Me\Assets\Mission4Me.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes

; o app é fechado pelo próprio [Code] (com --sair); o Restart Manager mataria o processo
; sem devolver a cor original da barra de tarefas
CloseApplications=no
RestartApplications=no

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "en";   MessagesFile: "compiler:Default.isl"

[CustomMessages]
ptbr.AutoStart=Iniciar o Mission4Me junto com o Windows
en.AutoStart=Start Mission4Me with Windows
ptbr.DeleteData=Apagar também o seu histórico de missões, a fila e as configurações?%n%n(pasta %1)%n%nEscolha "Não" para manter os dados caso reinstale.
en.DeleteData=Also delete your mission history, queue and settings?%n%n(folder %1)%n%nChoose "No" to keep them in case you reinstall.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart";   Description: "{cm:AutoStart}"

[Files]
Source: "..\dist\app\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; mesmo caminho que o app usa para gravar a tecla de atalho (Ctrl+Alt+M) — o app atualiza este atalho
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--missao"; Comment: "Iniciar missão de foco"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; Parameters: "--missao"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; fecha o app aberto, restaura a barra de tarefas e remove a inicialização automática
Filename: "{app}\{#AppExe}"; Parameters: "--desinstalar"; Flags: runhidden waituntilterminated; RunOnceId: "Mission4MeCleanup"

[UninstallDelete]
; logs gravados ao lado do executável
Type: files; Name: "{app}\*.log"

[Code]
// Antes de instalar/atualizar: pede ao app aberto para fechar de forma limpa
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  Code, I: Integer;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
  begin
    Exec(Exe, '--sair', '', SW_HIDE, ewWaitUntilTerminated, Code);
    // dá tempo da instância aberta encerrar e liberar o arquivo
    for I := 1 to 20 do
    begin
      if not FileExists(Exe) then Break;
      if RenameFile(Exe, Exe + '.check') then
      begin
        RenameFile(Exe + '.check', Exe);
        Break;
      end;
      Sleep(250);
    end;
  end;
end;

// Depois de desinstalar: pergunta se apaga os dados do usuário
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\{#AppName}');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox(FmtMessage(CustomMessage('DeleteData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
