param(
    [ValidateSet('2019','2023','All')][string]$RevitVersion = 'All',
    [string]$RevitApiDir = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Generate-Icons.ps1')
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$years = if ($RevitVersion -eq 'All') { @('2019','2023') } else { @($RevitVersion) }
foreach ($year in $years) {
    $major = [int]$year - 2000
    $apiDir = $RevitApiDir
    if (!$apiDir) {
        $apiDir = if ($year -eq '2019') { Join-Path $root 'references\Revit2019' } else { "C:\Program Files\Autodesk\Revit $year" }
    }
    foreach ($name in @('RevitAPI.dll','RevitAPIUI.dll')) {
        $api = Join-Path $apiDir $name
        if (!(Test-Path -LiteralPath $api)) { throw "Missing $api" }
        if ([Reflection.AssemblyName]::GetAssemblyName($api).Version.Major -ne $major) { throw "Wrong Revit API version: $api" }
    }
    $output = "bin\Package${year}_1_6_0\"
    $intermediate = "obj\Package${year}_1_6_0\"
    & $msbuild (Join-Path $root 'MEP_Align_Round_Duct_3D_ver1.csproj') /t:Rebuild /p:Configuration=Release "/p:RevitVersion=$year" "/p:RevitApiDir=$apiDir" "/p:OutputPath=$output" "/p:IntermediateOutputPath=$intermediate" /nologo /verbosity:minimal
    if ($LASTEXITCODE -ne 0) { throw "Add-in build failed for $year" }
    $folder = Join-Path $root "dist\HoL_DuctAlign_Revit${year}_1.6.0"
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    $dll = Join-Path (Join-Path $root $output) "HoL.DuctAlign.Revit${year}.dll"
    $exe = Join-Path $folder 'HoL_DuctAlign_Setup.exe'
    # Build the installer from a per-year source copy; the shared template targets 2023.
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Setup.cs')).Replace('2023',$year).Replace('== 23',"== $major").Replace('RevitAPI=23',"RevitAPI=$major")
    $generatedSource = Join-Path (Join-Path $root $intermediate) 'Setup.cs'
    [IO.File]::WriteAllText($generatedSource,$source)
    & $csc /nologo /target:winexe /platform:x64 /optimize+ "/out:$exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.Core.dll "/resource:$dll,Payload.Addin.dll" "/win32icon:$(Join-Path $root 'Assets\RoundAlign.ico')" "/win32manifest:$(Join-Path $PSScriptRoot 'Setup.manifest')" $generatedSource
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed for $year" }
    $readme = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'README_VI.txt')).Replace('2023',$year)
    if ($year -eq '2019') { $readme += "`r`nBẢN 2019: Đã build với Revit API 19 và kiểm tra payload. Chưa chạy thử trong Revit 2019.`r`nCác kiểm tra hình học và kết nối kể trên được thực hiện trong Revit 2023.`r`n" }
    $readme = $readme.Replace('Revit API 23',"Revit API $major")
    [IO.File]::WriteAllText((Join-Path $folder 'HUONG_DAN.txt'),$readme)
    $verify = Join-Path $folder 'PACKAGE_CHECK.txt'
    $process = Start-Process -FilePath $exe -ArgumentList @('/verify', ('"' + $verify + '"')) -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "Installer verification failed for $year" }
    $zip = "$folder.zip"
    Compress-Archive -LiteralPath $folder -DestinationPath $zip -Force
    Get-Content -LiteralPath $verify
    Get-FileHash -LiteralPath $zip -Algorithm SHA256 | Format-List
}
