$ErrorActionPreference = 'Stop'
$javaBin = 'C:\Program Files\Java\jdk-21\bin'
$testClasses = Join-Path $PSScriptRoot 'build\test-classes'
New-Item -ItemType Directory -Force $testClasses | Out-Null
& "$javaBin\javac.exe" -encoding UTF-8 -d $testClasses "$PSScriptRoot\src\cn\ethertransfer\mobile\Wire.java" "$PSScriptRoot\tests\InteropPeer.java"
if ($LASTEXITCODE) { throw 'Java test compilation failed' }
& "$javaBin\java.exe" -cp $testClasses cn.ethertransfer.mobile.InteropPeer self
if ($LASTEXITCODE) { throw 'Java protocol checks failed' }
$env:ETHERTRANSFER_JAVA_TEST_CLASSES = $testClasses
try {
    & dotnet test "$PSScriptRoot\..\EtherTransfer.Tests" -c Release --nologo
    if ($LASTEXITCODE) { throw 'C# / Android interoperability tests failed' }
} finally { Remove-Item Env:ETHERTRANSFER_JAVA_TEST_CLASSES -ErrorAction SilentlyContinue }
