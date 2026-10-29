/**
 * «Установка на ПК» on the hall page (owner): where the club's installer is downloaded, and an install file made for this
 * club — a `.cmd` that asks for administrator rights, takes `ClubShellSetup*.exe` from its own folder or Downloads (or the
 * latest GitHub release when there is none) and runs it silently with this server's address and the PC key the owner
 * pastes. The server keeps only the key's hash, so the console cannot fill it in by itself; the file is made in the
 * browser and nothing is sent anywhere. Its messages are in the console's language.
 */
import { useState } from 'react';
import { SERVER_ORIGIN } from '@/api';
import { t } from '@/i18n';
import { Button, Field, Input, Note, Section } from '@/ui';

/** GitHub repository whose releases carry `ClubShellSetup-<version>.exe`. */
const RELEASES_REPO = (import.meta.env['VITE_INSTALLER_REPO'] as string | undefined) ?? 'Nightcall7442/club-shell';
const RELEASES_URL = `https://github.com/${RELEASES_REPO}/releases/latest`;

const KEY = /^[A-Za-z0-9._-]{8,128}$/;
const KIOSK = /^[A-Za-z0-9._-]{1,20}$/;

/** A PowerShell single-quoted literal. */
const ps = (s: string): string => `'${s.replace(/'/g, "''")}'`;

/** `powershell -EncodedCommand` wants UTF-16LE in base64: the .cmd itself stays ASCII, the messages keep their letters. */
function encodedCommand(script: string): string {
  let binary = '';
  for (let i = 0; i < script.length; i++) {
    const c = script.charCodeAt(i);
    binary += String.fromCharCode(c & 0xff, c >> 8);
  }
  return btoa(binary);
}

/** The install file for this club (CRLF, ASCII only). */
export function installCmd(server: string, key: string, kiosk: string, repo: string): string {
  const script = [
    "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'",
    '[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12',
    `$server=${ps(server)};$key=${ps(key)};$kiosk=${ps(kiosk)};$repo=${ps(repo)}`,
    'function Fail($m){Write-Host $m -ForegroundColor Red;exit 1}',
    `try{$null=Invoke-WebRequest "$server/health" -UseBasicParsing -TimeoutSec 15}catch{Fail (${ps(t('Сервер не отвечает:'))}+" $server - "+$_.Exception.Message)}`,
    "$setup=@($env:CS_DIR,(Join-Path $env:USERPROFILE 'Downloads'))|%{Get-ChildItem -Path $_ -Filter 'ClubShellSetup*.exe' -File -EA SilentlyContinue}|sort LastWriteTime -Desc|select -First 1 -Expand FullName",
    'if(-not $setup){',
    `Write-Host ${ps(t('Скачиваю установщик ClubShell…'))}`,
    "$rel=Invoke-RestMethod \"https://api.github.com/repos/$repo/releases/latest\" -Headers @{'User-Agent'='ClubShell'}",
    "$asset=$rel.assets|?{$_.name -like 'ClubShellSetup*.exe'}|select -First 1",
    `if(-not $asset){Fail ${ps(t('В последнем релизе нет установщика ClubShellSetup.'))}}`,
    '$setup=Join-Path $env:TEMP $asset.name',
    'Invoke-WebRequest $asset.browser_download_url -OutFile $setup -UseBasicParsing}',
    `Write-Host ${ps(t('Ставлю ClubShell, это несколько минут…'))} $setup`,
    '$p=Start-Process $setup -ArgumentList \'/quiet\',"SERVERURL=$server","CLUBAPIKEY=$key","KIOSKUSER=$kiosk",\'INSTALLSHELL=1\' -Wait -PassThru',
    `if($p.ExitCode -notin 0,3010){Fail (${ps(t('Установщик завершился с ошибкой, код:'))}+" $($p.ExitCode), $env:TEMP\\ClubShellSetup_*.log")}`,
    `Write-Host ${ps(t('ClubShell установлен. Одобрите этот ПК в кассе: «Зал и устройства».'))} -ForegroundColor Green`,
    `if($p.ExitCode -eq 3010){Write-Host ${ps(t('Перезагрузите ПК, чтобы закончить установку.'))} -ForegroundColor Yellow}`,
  ].join('\n');
  return [
    '@echo off',
    'rem ClubShell install for this club. It holds the club PC key: keep this file in the club.',
    'net session >nul 2>&1',
    'if errorlevel 1 (',
    '  set "CS_SELF=%~f0"',
    '  powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath $env:CS_SELF"',
    '  exit /b',
    ')',
    'set "CS_DIR=%~dp0"',
    `powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand ${encodedCommand(script)}`,
    'pause',
    '',
  ].join('\r\n');
}

export function InstallKit(): JSX.Element {
  const [key, setKey] = useState('');
  const [kiosk, setKiosk] = useState('club');
  const issue = !KEY.test(key.trim())
    ? t('Ключ — латинские буквы и цифры, без пробелов')
    : !KIOSK.test(kiosk.trim())
      ? t('Учётная запись — латинские буквы и цифры, до 20 символов')
      : null;

  const download = (): void => {
    const text = installCmd(SERVER_ORIGIN, key.trim(), kiosk.trim(), RELEASES_REPO);
    const url = URL.createObjectURL(new Blob([text], { type: 'application/octet-stream' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = 'clubshell-install.cmd';
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <Section
      title={t('Установка на ПК')}
      actions={
        <a
          className="text-sm font-medium text-accent underline-offset-4 hover:underline"
          href={RELEASES_URL}
          target="_blank"
          rel="noreferrer"
        >
          {t('Скачать установщик')}
        </a>
      }
    >
      <p className="text-[13px] leading-5 text-muted">
        {t(
          'Файл установки ставит агента и оболочку на игровой ПК одним запуском: в нём уже адрес сервера и ключ для ПК. Установщик он берёт рядом с собой или в «Загрузках», а без него скачивает последний. Новый ПК появится здесь как ожидающий — одобрите его.',
        )}
      </p>
      <div className="grid grid-cols-1 gap-4 md:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <Field
          label={t('Ключ для ПК')}
          hint={t(
            'Выдаётся при создании клуба в суперадминке или задан на сервере (Club__EnrollmentKey). Касса его не хранит.',
          )}
        >
          <Input
            className="font-mono"
            autoComplete="off"
            spellCheck={false}
            value={key}
            onChange={(e) => setKey(e.target.value)}
          />
        </Field>
        <Field label={t('Учётная запись киоска')} hint={t('Обычно club')}>
          <Input
            className="font-mono"
            autoComplete="off"
            spellCheck={false}
            value={kiosk}
            onChange={(e) => setKiosk(e.target.value)}
          />
        </Field>
      </div>
      {key.trim() && issue && <Note note={{ text: issue, tone: 'err' }} />}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <span className="text-[12px] leading-4 text-muted">
          {t('Запустите файл на игровом ПК. В нём ключ для ПК: храните его только в клубе.')}
        </span>
        <Button variant="primary" disabled={issue !== null} onClick={download}>
          {t('Скачать файл установки')}
        </Button>
      </div>
    </Section>
  );
}
