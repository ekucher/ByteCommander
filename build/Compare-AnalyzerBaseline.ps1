# Ratchet попереджень аналізаторів за ідентифікатором правила.
# Перший запуск з -Update створює build/analyzers-baseline.json із фактичних чисел (поріг не вигадується).
param(
  [Parameter(Mandatory)] [string] $Sarif,
  [Parameter(Mandatory)] [string] $Baseline,
  [switch] $Update
)
$results = (Get-Content $Sarif -Raw | ConvertFrom-Json).runs.results |
  Where-Object { $_.level -in 'warning','error' }
$actual = @{}
foreach ($g in ($results | Group-Object ruleId)) { $actual[$g.Name] = $g.Count }

if ($Update -or -not (Test-Path $Baseline)) {
  $actual.GetEnumerator() | Sort-Object Name | ForEach-Object -Begin { $o = [ordered]@{} } -Process { $o[$_.Name] = $_.Value } -End { $o } |
    ConvertTo-Json | Set-Content $Baseline -Encoding utf8
  "Baseline записано: $($actual.Count) правил, $(($actual.Values | Measure-Object -Sum).Sum) попереджень"
  exit 0
}

$base = Get-Content $Baseline -Raw | ConvertFrom-Json -AsHashtable
$grew = @(); $shrunk = @()
foreach ($k in $actual.Keys) {
  $b = if ($base.ContainsKey($k)) { $base[$k] } else { 0 }
  if ($actual[$k] -gt $b) { $grew += "$k`: $b → $($actual[$k])" }
}
foreach ($k in $base.Keys) {
  $a = if ($actual.ContainsKey($k)) { $actual[$k] } else { 0 }
  if ($a -lt $base[$k]) { $shrunk += "$k`: $($base[$k]) → $a" }
}
"### Analyzers ratchet" >> $env:GITHUB_STEP_SUMMARY
if ($shrunk) { "Зменшилось (оновіть baseline з -Update): " + ($shrunk -join ', ') >> $env:GITHUB_STEP_SUMMARY }
if ($grew)   { "Зросло: " + ($grew -join ', ') >> $env:GITHUB_STEP_SUMMARY; throw "Кількість попереджень зросла: $($grew -join '; ')" }
"Без зростання." >> $env:GITHUB_STEP_SUMMARY
