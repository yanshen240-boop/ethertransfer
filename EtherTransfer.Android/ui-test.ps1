$ErrorActionPreference = 'Stop'
$javaBin = 'C:\Program Files\Java\jdk-21\bin'
$sdkRoot = 'C:\ruanjian\code\android-sdk'
$buildTools = "$sdkRoot\build-tools\36.0.0"
$androidJar = "$sdkRoot\platforms\android-37.0\android.jar"
$buildDir = "$PSScriptRoot\build\ui-checks"
New-Item -ItemType Directory -Force "$buildDir\classes", "$buildDir\dex" | Out-Null
function Run-Tool([string]$tool,[string[]]$toolArgs) {
    & $tool @toolArgs
    if ($LASTEXITCODE) { throw "UI checks failed: $tool ($LASTEXITCODE)" }
}
Run-Tool "$javaBin\javac.exe" @('-encoding','UTF-8','-source','8','-target','8','-classpath',$androidJar,'-d',"$buildDir\classes","$PSScriptRoot\tests\UiChecks.java","$PSScriptRoot\src\cn\ethertransfer\mobile\ChatStore.java")
Run-Tool "$javaBin\jar.exe" @('cf',"$buildDir\classes.jar",'-C',"$buildDir\classes",'.')
Run-Tool "$javaBin\java.exe" @('-cp',"$buildTools\lib\d8.jar",'com.android.tools.r8.D8','--lib',$androidJar,'--min-api','26','--output',"$buildDir\dex","$buildDir\classes.jar")
Run-Tool "$buildTools\aapt.exe" @('package','-f','-M',"$PSScriptRoot\tests\AndroidManifest.xml",'-I',$androidJar,'-F',"$buildDir\unsigned.apk")
Push-Location "$buildDir\dex"
try { Run-Tool "$buildTools\aapt.exe" @('add',"$buildDir\unsigned.apk",'classes.dex') } finally { Pop-Location }
Run-Tool "$buildTools\zipalign.exe" @('-f','4',"$buildDir\unsigned.apk","$buildDir\aligned.apk")
Run-Tool "$javaBin\java.exe" @('-jar',"$buildTools\lib\apksigner.jar",'sign','--ks',"$PSScriptRoot\build\development.p12",'--ks-pass','pass:android','--out',"$buildDir\ui-checks.apk","$buildDir\aligned.apk")
Run-Tool "$sdkRoot\platform-tools\adb.exe" @('install','-r',"$buildDir\ui-checks.apk")
$uiResult = & "$sdkRoot\platform-tools\adb.exe" shell am instrument -w cn.ethertransfer.mobile.uitest/.UiChecks
$uiResult
if ($LASTEXITCODE -or !($uiResult -match 'PASS: add selection')) { throw 'Android UI assertions failed' }
