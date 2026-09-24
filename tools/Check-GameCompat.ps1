#requires -Version 7.0
<#
  Check-GameCompat.ps1 —— 守墓人 2 修改器兼容性自检
  正式版发布后，对新的游戏目录跑一次，即可判断现有修改器还能不能用。

  用法：
    .\Check-GameCompat.ps1 -GameDir "…\Graveyard Keeper 2"
#>
param(
  [Parameter(Mandatory = $true)][string]$GameDir
)

if (-not (Test-Path $GameDir)) { Write-Output "目录不存在: $GameDir"; exit 1 }

Write-Output "=== 守墓人 2 修改器兼容性检查 ==="
Write-Output ("游戏目录: {0}" -f $GameDir)
Write-Output ""

$exe = Get-ChildItem $GameDir -Filter "*.exe" -ErrorAction SilentlyContinue |
Where-Object { $_.Name -notmatch 'UnityCrashHandler' } | Select-Object -First 1
$dataDir = Get-ChildItem $GameDir -Directory -ErrorAction SilentlyContinue |
Where-Object { $_.Name -like "*_Data" } | Select-Object -First 1

if ($exe) { Write-Output ("主程序  : {0}    <- 进程名去掉 .exe 即为进程名" -f $exe.Name) }
if ($dataDir) { Write-Output ("数据目录: {0}" -f $dataDir.Name) }
Write-Output ""

$managed = if ($dataDir) { Join-Path $dataDir.FullName "Managed\Assembly-CSharp.dll" } else { "" }
$gameAsm = Join-Path $GameDir "GameAssembly.dll"
$mono = Join-Path $GameDir "MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll"

$hasManaged = $managed -and (Test-Path $managed)
$hasGameAsm = Test-Path $gameAsm

Write-Output "--- 引擎后端 ---"
if ($hasGameAsm -and -not $hasManaged) {
  Write-Output "[!] IL2CPP 后端 —— 现有修改器【不兼容】"
  Write-Output "    该版本没有托管程序集，Mono 结构定位全部失效。"
  Write-Output "    需要改用 Il2CppDumper 导出 dump.cs，再按字段 RVA + 模块基址定位。"
} elseif ($hasManaged) {
  Write-Output "[OK] Mono 后端 —— 现有修改器【兼容】"
} else {
  Write-Output "[?] 无法判定后端"
}
Write-Output ("  GameAssembly.dll (IL2CPP 标志) : {0}" -f $hasGameAsm)
Write-Output ("  Managed/Assembly-CSharp.dll    : {0}" -f $hasManaged)
Write-Output ("  mono-2.0-bdwgc.dll             : {0}" -f (Test-Path $mono))

if ($hasManaged) {
  Write-Output ""
  Write-Output "--- 关键类型是否仍在（决定定位是否还成立）---"
  $s = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($managed))
  foreach ($t in @('GameResAtom', 'GK2GameResSystem', 'PlayerMoneyGameResSystem',
      'PlayerEnergyGameResSystem', 'PlayerInsanityGameResSystem', 'LazyBearTechnology')) {
    Write-Output ("  {0,-32} {1}" -f $t, $(if ($s.Contains($t)) { "存在" } else { "缺失 <-- 注意" }))
  }

  Write-Output ""
  Write-Output "--- Unity / Mono 版本（决定 MonoClass 结构偏移是否变化）---"
  $up = Join-Path $GameDir "UnityPlayer.dll"
  if (Test-Path $up) {
    Write-Output ("  UnityPlayer.dll : {0}" -f (Get-Item $up).VersionInfo.FileVersion)
    Write-Output "  上次适配版本    : 6000.3.9f1 (Unity 6)"
    Write-Output "  说明：Unity 大版本升级时 mono 内部结构偏移可能变化，需要重新校准。"
  }
}
