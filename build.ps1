param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'SpentZ.exe')
)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path $compiler)) {
    throw 'O compilador do .NET Framework 4.x não foi encontrado.'
}
$wpf = Join-Path $framework 'WPF'
$markup = Join-Path $PSScriptRoot 'Main.xaml'
$icon = Join-Path $PSScriptRoot 'assets\SpentZ.ico'
$badge = Join-Path $PSScriptRoot 'assets\SpentZ.png'
$sources = @(
    (Join-Path $PSScriptRoot 'Program.cs')
    (Join-Path $PSScriptRoot 'Ledger.cs')
    (Join-Path $PSScriptRoot 'Budget.cs')
    (Join-Path $PSScriptRoot 'BudgetUi.cs')
    (Join-Path $PSScriptRoot 'OpeningBalance.cs')
    (Join-Path $PSScriptRoot 'ReleaseTests.cs')
    (Join-Path $PSScriptRoot 'Billing.cs')
    (Join-Path $PSScriptRoot 'BillingUi.cs')
    (Join-Path $PSScriptRoot 'Closures.cs')
    (Join-Path $PSScriptRoot 'Reserves.cs')
    (Join-Path $PSScriptRoot 'ReservesUi.cs')
    (Join-Path $PSScriptRoot 'CalendarClock.cs')
    (Join-Path $PSScriptRoot 'Navigation.cs')
    (Join-Path $PSScriptRoot 'Reconciliation.cs')
    (Join-Path $PSScriptRoot 'BackupUi.cs')
    (Join-Path $PSScriptRoot 'Appearance.cs')
)
$references = @(
    "/reference:$wpf\PresentationFramework.dll"
    "/reference:$wpf\PresentationCore.dll"
    "/reference:$wpf\WindowsBase.dll"
    '/reference:System.Xaml.dll'
    '/reference:System.Windows.Forms.dll'
)
& $compiler /nologo /target:winexe /optimize+ /codepage:65001 "/out:$OutputPath" "/win32icon:$icon" `
    "/resource:$markup,Main.xaml" "/resource:$badge,SpentZ.png" $references $sources
if ($LASTEXITCODE -ne 0) {
    throw "A compilação falhou ($LASTEXITCODE)."
}
