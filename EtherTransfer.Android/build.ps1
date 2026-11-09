$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$sdkRoot = 'C:\ruanjian\code\android-sdk'
$buildTools = Join-Path $sdkRoot 'build-tools\36.0.0'
$androidJar = Join-Path $sdkRoot 'platforms\android-37.0\android.jar'
$javaBin = 'C:\Program Files\Java\jdk-21\bin'
$buildDir = Join-Path $projectRoot 'build'

New-Item -ItemType Directory -Force "$buildDir\classes", "$buildDir\dex", "$buildDir\gen" | Out-Null
Remove-Item "$buildDir\classes\*" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$buildDir\dex\*" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$buildDir\gen\*" -Recurse -Force -ErrorAction SilentlyContinue
# aapt2 does not overwrite old outputs: a stale resources.zip yields an R.java without the new
# resources, and a stale APK keeps an old classes.dex. Always delete them first.
Remove-Item "$buildDir\resources.zip", "$buildDir\unsigned.apk", "$buildDir\aligned.apk" -Force -ErrorAction SilentlyContinue

function Run-Tool([string]$tool, [string[]]$toolArgs) {
    # Native tools write warnings to stderr; under $ErrorActionPreference='Stop' that would
    # abort the build on the first javac warning even though the tool succeeded.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $tool @toolArgs
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
    if ($code -ne 0) { throw "BUILD FAILED: $tool (exit $code)" }
}
function Assert-File([string]$path, [string]$what) {
    if (!(Test-Path $path)) { throw "BUILD FAILED: $what was not produced ($path)" }
    if ((Get-Item $path).Length -lt 1024) { throw "BUILD FAILED: $what looks empty ($path)" }
}

# 1) resources: aapt2 compile + link (produces R.java and a resource-only APK)
Run-Tool "$buildTools\aapt2.exe" @('compile', '--dir', "$projectRoot\res", '-o', "$buildDir\resources.zip")
Run-Tool "$buildTools\aapt2.exe" @('link', '-o', "$buildDir\unsigned.apk",
    '-I', $androidJar, '--manifest', "$projectRoot\AndroidManifest.xml",
    '--java', "$buildDir\gen", '--min-sdk-version', '26', '--target-sdk-version', '35',
    "$buildDir\resources.zip")

# 2) java sources + generated R.java
$sourceFiles = @(Get-ChildItem "$projectRoot\src" -Recurse -Filter *.java | ForEach-Object FullName)
$sourceFiles += @(Get-ChildItem "$buildDir\gen" -Recurse -Filter *.java | ForEach-Object FullName)
Run-Tool "$javaBin\javac.exe" (@('-encoding', 'UTF-8', '-source', '8', '-target', '8',
    '-classpath', $androidJar, '-d', "$buildDir\classes") + $sourceFiles)
# javac exit code is unreliable under PowerShell, so verify the key class directly.
Assert-File "$buildDir\classes\cn\ethertransfer\mobile\MainActivity.class" "MainActivity.class"

# 3) dex + package
Run-Tool "$javaBin\jar.exe" @('cf', "$buildDir\classes.jar", '-C', "$buildDir\classes", '.')
Run-Tool "$javaBin\java.exe" @('-cp', "$buildTools\lib\d8.jar", 'com.android.tools.r8.D8',
    '--lib', $androidJar, '--min-api', '26', '--output', "$buildDir\dex", "$buildDir\classes.jar")
Assert-File "$buildDir\dex\classes.dex" "classes.dex"
Push-Location "$buildDir\dex"
try { Run-Tool "$buildTools\aapt.exe" @('add', "$buildDir\unsigned.apk", 'classes.dex') } finally { Pop-Location }
Run-Tool "$buildTools\zipalign.exe" @('-f', '4', "$buildDir\unsigned.apk", "$buildDir\aligned.apk")

# 4) sign with the development key (replace with a private release key before shipping)
$key = Join-Path $buildDir 'development.p12'
if (!(Test-Path $key)) {
    Run-Tool "$javaBin\keytool.exe" @('-genkeypair', '-keystore', $key, '-storepass', 'android',
        '-keypass', 'android', '-alias', 'ethertransfer', '-keyalg', 'RSA', '-keysize', '2048',
        '-validity', '10000', '-dname', 'CN=EtherTransfer Development')
}
Run-Tool "$javaBin\java.exe" @('-jar', "$buildTools\lib\apksigner.jar", 'sign', '--ks', $key,
    '--ks-pass', 'pass:android', '--out', "$buildDir\EtherTransfer-Android.apk", "$buildDir\aligned.apk")
Run-Tool "$javaBin\java.exe" @('-jar', "$buildTools\lib\apksigner.jar", 'verify', '--verbose',
    "$buildDir\EtherTransfer-Android.apk")
Write-Output "APK: $buildDir\EtherTransfer-Android.apk"
