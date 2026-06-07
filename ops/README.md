# ops — эксплуатация

## Ночной авто-тест (`nightly_test.ps1`)

Ежедневная проверка здоровья линкера и собранных пакетов. **Ничего не переносит и
не ломает** — только сборка из исходников + read-only проверки.

Что делает:
- собирает линкер из исходников (`dotnet build -c Release`) — ловит регрессии;
- находит portable-пакеты на **всех фиксированных дисках** (`C:`/`D:`/`E:`/`F:`…) в
  папках `\Programs`, `\Portable`, `\ClubPortable` + в `CatalogRoots` из настроек линкера
  (не привязано к конкретному диску);
- для каждого пакета — `--verify-package` (целостность ссылок/reg/манифеста) по exit-коду;
- пишет отчёт в `reports\<дата>.log` (история 30 дней), статус в `LAST_STATUS.txt`,
  а при проблемах — строку в `ALERTS.log` + всплывающее уведомление Windows.

Где живёт (чтобы НЕ зависеть от игрового диска):
- скрипт: `C:\ProgramData\ClubPortableLinker\nightly_test.ps1`
- отчёты/статус: рядом со скриптом (`reports\`, `LAST_STATUS.txt`, `ALERTS.log`)
- этот файл в репозитории — версионируемый эталон; копия в `%ProgramData%` — рабочая.

Регистрация задачи (ежедневно 04:00, права администратора):
```powershell
$tr = 'powershell -NoProfile -ExecutionPolicy Bypass -File "C:\ProgramData\ClubPortableLinker\nightly_test.ps1"'
schtasks /Create /TN "ClubPortableLinker-Nightly" /TR $tr /SC DAILY /ST 04:00 /RL HIGHEST /F
```

Проверить вручную:
```powershell
schtasks /Run /TN "ClubPortableLinker-Nightly"
Get-Content "C:\ProgramData\ClubPortableLinker\LAST_STATUS.txt"
```

Примечание: линкер — WinExe (GUI-сабсистема), поэтому в скрипте он вызывается через
`Start-Process -Wait` с перенаправлением вывода, а НЕ через `& $exe` (PowerShell не
дожидается GUI-процесса и не ловит его stdout/exit-код).
